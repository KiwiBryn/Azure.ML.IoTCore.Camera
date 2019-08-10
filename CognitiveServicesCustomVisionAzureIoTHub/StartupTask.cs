// <copyright file="StartupTask.cs" company="devMobile Software">
// Copyright ® 2019 August devMobile Software, All Rights Reserved
//
//  MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE"
//
// </copyright>


namespace devMobile.Windows10IotCore.IoT.CognitiveServicesCustomVisionAzureIoTHub
{
	using System;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction;
	using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction.Models;
	using Microsoft.Azure.Devices.Client;
	using Microsoft.Azure.Devices.Shared;
	using Microsoft.Extensions.Configuration;
	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;
	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.Storage.Streams;
	using Windows.System;
	using Windows.System.Profile;

	enum ModelType
	{
		Undefined = 0,
		Classification,
		Detection
	}

	public sealed class StartupTask : IBackgroundTask
	{
		private const string ConfigurationFilename = "appsettings.json";
		private const string ImageFilename = "CustomVisionAPILatest.jpg";
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Cognitive Services Custom Vision API", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private readonly TimeSpan deviceRebootDelayPeriod = new TimeSpan(0, 0, 25);
		private readonly TimeSpan timerPeriodInfinite = new TimeSpan(0, 0, 0);
		private readonly TimeSpan timerPeriodDetectIlluminated = new TimeSpan(0, 0, 0, 0, 10);
		private GpioPin interruptGpioPin = null;
		private GpioPinEdge interruptTriggerOn = GpioPinEdge.RisingEdge;
		private int interruptPinNumber;
		private GpioPin displayGpioPin = null;
		private int displayPinNumber;
		private Timer displayOffTimer;
		private TimeSpan debounceTimeout;
		private string azureIoTHubConnectionString;
		private TransportType transportType;
		private DeviceClient azureIoTHubClient = null;
		private CustomVisionPredictionClient customVisionClient;
		private DateTime imageLastCapturedAtUtc = DateTime.MinValue;
		private MediaCapture mediaCapture;
		private ModelType modelType;
		private string azureCognitiveServicesEndpoint;
		private string azureCognitiveServicesSubscriptionKey;
		private Guid projectId;
		private string modelPublishedName;
		private double probabilityThreshold;
		private volatile bool cameraBusy = false;
		private Timer imageUpdatetimer;
		private BackgroundTaskDeferral backgroundTaskDeferral = null;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			StorageFolder localFolder = ApplicationData.Current.LocalFolder;
			TimeSpan imageUpdateDue;
			TimeSpan imageUpdatePeriod;

			this.logging.LogEvent("Application starting");

			// Log the Application build, OS version information etc.
			LoggingFields startupInformation = new LoggingFields();
			startupInformation.AddString("Timezone", TimeZoneSettings.CurrentTimeZoneDisplayName);
			startupInformation.AddString("OSVersion", Environment.OSVersion.VersionString);
			startupInformation.AddString("MachineName", Environment.MachineName);

			// This is from the application manifest
			Package package = Package.Current;
			PackageId packageId = package.Id;
			PackageVersion version = packageId.Version;
			startupInformation.AddString("ApplicationVersion", string.Format($"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"));

			try
			{
				// see if the configuration file is present if not copy minimal sample one from application directory
				if (localFolder.TryGetItemAsync(ConfigurationFilename).AsTask().Result == null)
				{
					StorageFile templateConfigurationfile = Package.Current.InstalledLocation.GetFileAsync(ConfigurationFilename).AsTask().Result;
					templateConfigurationfile.CopyAsync(localFolder, ConfigurationFilename).AsTask();
				}

				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(localFolder.Path, ConfigurationFilename), false, true).Build();

				this.interruptPinNumber = int.Parse(configuration.GetSection("InterruptPinNumber").Value);
				startupInformation.AddInt32("Interrupt pin", this.interruptPinNumber);

				this.interruptTriggerOn = (GpioPinEdge)Enum.Parse(typeof(GpioPinEdge), configuration.GetSection("interruptTriggerOn").Value);
				startupInformation.AddString("Interrupt Trigger on", this.interruptTriggerOn.ToString());

				this.displayPinNumber = int.Parse(configuration.GetSection("DisplayPinNumber").Value);
				startupInformation.AddInt32("Display pin", this.interruptPinNumber);

				this.azureIoTHubConnectionString = configuration.GetSection("AzureIoTHubConnectionString").Value;
				startupInformation.AddString("AzureIoTHubConnectionString", this.azureIoTHubConnectionString);

				this.transportType = (TransportType)Enum.Parse(typeof(TransportType), configuration.GetSection("TransportType").Value);
				startupInformation.AddString("TransportType", this.transportType.ToString());
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("JSON configuration file load or settings retrieval failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			#region AzureIoT Hub connection string creation
			try
			{
				this.azureIoTHubClient = DeviceClient.CreateFromConnectionString(this.azureIoTHubConnectionString, this.transportType);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("AzureIOT Hub DeviceClient.CreateFromConnectionString failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region Report device and application properties to AzureIoT Hub
			try
			{
				TwinCollection reportedProperties = new TwinCollection();

				// This is from the OS
				reportedProperties["Timezone"] = TimeZoneSettings.CurrentTimeZoneDisplayName;
				reportedProperties["OSVersion"] = Environment.OSVersion.VersionString;
				reportedProperties["MachineName"] = Environment.MachineName;

				reportedProperties["ApplicationDisplayName"] = package.DisplayName;
				reportedProperties["ApplicationName"] = packageId.Name;
				reportedProperties["ApplicationVersion"] = string.Format($"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");

				// Unique identifier from the hardware
				SystemIdentificationInfo systemIdentificationInfo = SystemIdentification.GetSystemIdForPublisher();
				using (DataReader reader = DataReader.FromBuffer(systemIdentificationInfo.Id))
				{
					byte[] bytes = new byte[systemIdentificationInfo.Id.Length];
					reader.ReadBytes(bytes);
					reportedProperties["SystemId"] = BitConverter.ToString(bytes);
				}

				this.azureIoTHubClient.UpdateReportedPropertiesAsync(reportedProperties).Wait();
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client UpdateReportedPropertiesAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region Retrieve device twin settings
			try
			{
				LoggingFields configurationInformation = new LoggingFields();

				Twin deviceTwin = this.azureIoTHubClient.GetTwinAsync().GetAwaiter().GetResult();

				if (!deviceTwin.Properties.Desired.Contains("ImageUpdateDue") || !TimeSpan.TryParse(deviceTwin.Properties.Desired["ImageUpdateDue"].value.ToString(), out imageUpdateDue))
				{
					this.logging.LogMessage("DeviceTwin.Properties ImageUpdateDue setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddTimeSpan("ImageUpdateDue", imageUpdateDue);

				if (!deviceTwin.Properties.Desired.Contains("ImageUpdatePeriod") || !TimeSpan.TryParse(deviceTwin.Properties.Desired["ImageUpdatePeriod"].value.ToString(), out imageUpdatePeriod))
				{
					this.logging.LogMessage("DeviceTwin.Properties ImageUpdatePeriod setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddTimeSpan("ImageUpdatePeriod", imageUpdatePeriod);

				if (!deviceTwin.Properties.Desired.Contains("ModelType") || (!Enum.TryParse(deviceTwin.Properties.Desired["ModelType"].value.ToString(), out modelType)))
				{
					this.logging.LogMessage("DeviceTwin.Properties ModelType setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddString("ModelType", modelType.ToString());

				if (!deviceTwin.Properties.Desired.Contains("ModelPublishedName") || (string.IsNullOrWhiteSpace(deviceTwin.Properties.Desired["ModelPublishedName"].value.ToString())))
				{
					this.logging.LogMessage("DeviceTwin.Properties ModelPublishedName setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				modelPublishedName = deviceTwin.Properties.Desired["ModelPublishedName"].value.ToString();
				configurationInformation.AddString("ModelPublishedName", modelPublishedName);

				if (!deviceTwin.Properties.Desired.Contains("ProjectID") || (!Guid.TryParse(deviceTwin.Properties.Desired["ProjectID"].value.ToString(), out projectId)))
				{
					this.logging.LogMessage("DeviceTwin.Properties ProjectId setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddGuid("ProjectID", projectId);

				if (!deviceTwin.Properties.Desired.Contains("ProbabilityThreshold") || (!Double.TryParse(deviceTwin.Properties.Desired["ProbabilityThreshold"].value.ToString(), out probabilityThreshold)))
				{
					this.logging.LogMessage("DeviceTwin.Properties ProbabilityThreshold setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddDouble("ProbabilityThreshold", probabilityThreshold);

				if (!deviceTwin.Properties.Desired.Contains("AzureCognitiveServicesEndpoint") || (string.IsNullOrWhiteSpace(deviceTwin.Properties.Desired["AzureCognitiveServicesEndpoint"].value.ToString())))
				{
					this.logging.LogMessage("DeviceTwin.Properties AzureCognitiveServicesEndpoint setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				azureCognitiveServicesEndpoint = deviceTwin.Properties.Desired["AzureCognitiveServicesEndpoint"].value.ToString();
				configurationInformation.AddString("AzureCognitiveServicesEndpoint", modelPublishedName);

				if (!deviceTwin.Properties.Desired.Contains("AzureCognitiveServicesSubscriptionKey") || (string.IsNullOrWhiteSpace(deviceTwin.Properties.Desired["AzureCognitiveServicesSubscriptionKey"].value.ToString())))
				{
					this.logging.LogMessage("DeviceTwin.Properties AzureCognitiveServicesSubscriptionKey setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				azureCognitiveServicesSubscriptionKey = deviceTwin.Properties.Desired["AzureCognitiveServicesSubscriptionKey"].value.ToString();
				configurationInformation.AddString("AzureCognitiveServicesSubscriptionKey", azureCognitiveServicesSubscriptionKey);

				if (!deviceTwin.Properties.Desired.Contains("DebounceTimeout") || !TimeSpan.TryParse(deviceTwin.Properties.Desired["DebounceTimeout"].value.ToString(), out debounceTimeout))
				{
					this.logging.LogMessage("DeviceTwin.Properties DebounceTimeout setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddTimeSpan("DebounceTimeout", debounceTimeout);

				this.logging.LogEvent("Configuration settings", configurationInformation);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client GetTwinAsync failed or property missing/invalid" + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			try
			{
				this.customVisionClient = new CustomVisionPredictionClient(new System.Net.Http.DelegatingHandler[] { })
				{
					ApiKey = this.azureCognitiveServicesSubscriptionKey,
					Endpoint = this.azureCognitiveServicesEndpoint,
				};
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure Cognitive Services Custom Vision Client configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			try
			{
				this.mediaCapture = new MediaCapture();
				this.mediaCapture.InitializeAsync().AsTask().Wait();
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			this.displayOffTimer = new Timer(this.TimerCallback, null, Timeout.Infinite, Timeout.Infinite);

			#region Wire up interupt handler for image capture request
			if (this.interruptPinNumber != 0)
			{
				try
				{
					GpioController gpioController = GpioController.GetDefault();
					this.interruptGpioPin = gpioController.OpenPin(this.interruptPinNumber);
					this.interruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
					this.interruptGpioPin.ValueChanged += this.InterruptGpioPin_ValueChanged;

					this.displayGpioPin = gpioController.OpenPin(this.displayPinNumber);
					this.displayGpioPin.SetDriveMode(GpioPinDriveMode.Output);
					this.displayGpioPin.Write(GpioPinValue.Low);
				}
				catch (Exception ex)
				{
					this.logging.LogMessage("Digital input configuration failed " + ex.Message, LoggingLevel.Error);
					return;
				}
			}
			#endregion

			#region Wire up command handler for image capture request
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("ImageCapture", this.ImageUpdateHandler, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client ImageCapture SetMethodHandlerAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region Wire up command handler for device reboot request
			try
			{
				this.azureIoTHubClient.SetMethodHandlerAsync("DeviceReboot", this.DeviceRebootAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client DeviceReboot SetMethodHandlerAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			if ((imageUpdateDue != TimeSpan.MinValue) || (imageUpdatePeriod != TimeSpan.MinValue))
			{
				this.imageUpdatetimer = new Timer(this.ImageUpdateTimerCallback, null, imageUpdateDue, imageUpdatePeriod);
			}
		
			this.logging.LogEvent("Application started", startupInformation);

			// enable task to continue running in background
			this.backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private async void ImageUpdateTimerCallback(object state)
		{
			Debug.WriteLine($"{DateTime.UtcNow.ToString("yy-MM-ss HH:mm:ss")} Timer triggered");

			await this.ImageUpdate(false);
		}

		private async void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			Debug.WriteLine($"Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

			if (args.Edge != this.interruptTriggerOn)
			{
				return;
			}

			await ImageUpdate(false);
		}

		private async Task ImageUpdate(bool isCommand)
		{
			DateTime currentTime = DateTime.UtcNow;

			// Check that enough time has passed for picture to be taken
			if ((currentTime - this.imageLastCapturedAtUtc) < this.debounceTimeout)
			{
				this.displayGpioPin.Write(GpioPinValue.High);
				this.displayOffTimer.Change(this.timerPeriodDetectIlluminated, this.timerPeriodInfinite);
				return;
			}

			this.imageLastCapturedAtUtc = currentTime;

			// Just incase - stop code being called while photo already in progress
			if (this.cameraBusy)
			{
				this.displayGpioPin.Write(GpioPinValue.High);
				this.displayOffTimer.Change(this.timerPeriodDetectIlluminated, this.timerPeriodInfinite);
				return;
			}

			this.cameraBusy = true;

			try
			{
				ImagePrediction imagePrediction;

				using (Windows.Storage.Streams.InMemoryRandomAccessStream captureStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
				{
					this.mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream).AsTask().Wait();
					captureStream.FlushAsync().AsTask().Wait();
					captureStream.Seek(0);

					IStorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(ImageFilename, CreationCollisionOption.ReplaceExisting);
					ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
					await this.mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);


					switch (modelType)
					{
						case ModelType.Classification:
							imagePrediction = await this.customVisionClient.ClassifyImageAsync(this.projectId, this.modelPublishedName, captureStream.AsStreamForRead());
							break;
						case ModelType.Detection:
							imagePrediction = this.customVisionClient.DetectImage(this.projectId, this.modelPublishedName, captureStream.AsStreamForRead());
							break;
						default:
							Debug.WriteLine($"Unknown modelType");
							return;
					}
					Debug.WriteLine($"Prediction count {imagePrediction.Predictions.Count}");
				}

				JObject telemetryDataPoint = new JObject();
				LoggingFields imageInformation = new LoggingFields();

				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddBoolean("IsCommand", isCommand);
				imageInformation.AddInt32("Predictions", imagePrediction.Predictions.Count);

				foreach (var prediction in imagePrediction.Predictions)
				{
					Debug.WriteLine($" Tag:{prediction.TagName} {prediction.Probability}");
					imageInformation.AddDouble($"Tag:{prediction.TagName}", prediction.Probability);
				}

				// Group the tags to get the count
				var groupedPredictions = from prediction in imagePrediction.Predictions where prediction.Probability > probabilityThreshold 
						group prediction by new { prediction.TagName }														
						into newGroup
						select new
						{
							TagName = newGroup.Key.TagName,
							Count = newGroup.Count(),
						};

				foreach (var prediction in groupedPredictions)
				{
					Debug.WriteLine($" Tag:{prediction.TagName} {prediction.Count}");
					telemetryDataPoint.Add(prediction.TagName, prediction.Count);
					imageInformation.AddInt32($"Tag:{prediction.TagName}", prediction.Count);
				}

				this.logging.LogEvent("Captured image processed by Cognitive Services", imageInformation);

				try
				{
					using (Message message = new Message(Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(telemetryDataPoint))))
					{
						Debug.WriteLine(" {0:HH:mm:ss} AzureIoTHubClient SendEventAsync start", DateTime.UtcNow);
						await this.azureIoTHubClient.SendEventAsync(message);
						Debug.WriteLine(" {0:HH:mm:ss} AzureIoTHubClient SendEventAsync finish", DateTime.UtcNow);
					}
					this.logging.LogEvent("SendEventAsync CSV payload", imageInformation, LoggingLevel.Information);
				}
				catch (Exception ex)
				{
					imageInformation.AddString("Exception", ex.ToString());
					this.logging.LogEvent("SendEventAsync payload", imageInformation, LoggingLevel.Error);
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo or save failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				this.cameraBusy = false;
			}
		}

		private async Task<MethodResponse> ImageUpdateHandler(MethodRequest methodRequest, object userContext)
		{
			Debug.WriteLine($"{DateTime.UtcNow.ToString("yy-MM-ss HH:mm:ss")} Method handler triggered");

			await this.ImageUpdate(true);

			return new MethodResponse(200);
		}

		#pragma warning disable 1998
		private async Task<MethodResponse> DeviceRebootAsync(MethodRequest methodRequest, object userContext)
		{
			this.logging.LogEvent("Reboot initiated");

			// Stop the image capture timer before reboot
			if (this.imageUpdatetimer != null)
			{
				this.imageUpdatetimer.Change(Timeout.Infinite, Timeout.Infinite);
			}

			ShutdownManager.BeginShutdown(ShutdownKind.Restart, this.deviceRebootDelayPeriod);

			return new MethodResponse(200);
		}
		#pragma warning restore 1998

		private void TimerCallback(object state)
		{
			this.displayGpioPin.Write(GpioPinValue.Low);
		}
	}
}