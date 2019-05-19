// <copyright file="StartupTask.cs" company="devMobile Software">
// Copyright ® 2019 Feb devMobile Software, All Rights Reserved
//
//  MIT License
//
//  Permission is hereby granted, free of charge, to any person obtaining a copy
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

namespace devMobile.Windows10IotCore.IoT.CognitiveServicesCustomVision
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Threading;

	using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction;
	using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction.Models;
	using Microsoft.Extensions.Configuration;

	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.System;

	public sealed class StartupTask : IBackgroundTask
	{
		private const string ConfigurationFilename = "appsettings.json";
		private const string ImageFilename = "ComputerVisionAPILatest.jpg";
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Cognitive Services Computer Vision API", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private readonly TimeSpan timerPeriodInfinite = new TimeSpan(0, 0, 0);
		private readonly TimeSpan timerPeriodDetectIlluminated = new TimeSpan(0, 0, 0, 0, 10);
		private readonly TimeSpan timerPeriodFaceIlluminated = new TimeSpan(0, 0, 0, 5);
		private GpioPin interruptGpioPin = null;
		private GpioPinEdge interruptTriggerOn = GpioPinEdge.RisingEdge;
		private int interruptPinNumber;
		private GpioPin displayGpioPin = null;
		private int displayPinNumber;
		private Timer displayOffTimer;
		private TimeSpan debounceTimeout;
		private CustomVisionPredictionClient customVisionClient;
		private DateTime imageLastCapturedAtUtc = DateTime.MinValue;
		private MediaCapture mediaCapture;
		private string azureCognitiveServicesEndpoint;
		private string azureCognitiveServicesSubscriptionKey;
		private Guid projectId;
		private string publishedName;
		private volatile bool cameraBusy = false;
		private BackgroundTaskDeferral backgroundTaskDeferral = null;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			StorageFolder localFolder = ApplicationData.Current.LocalFolder;

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

				this.azureCognitiveServicesEndpoint = configuration.GetSection("AzureCognitiveServicesEndpoint").Value;
				startupInformation.AddString("AzureCognitiveServicesEndpoint", this.azureCognitiveServicesEndpoint);

				this.azureCognitiveServicesSubscriptionKey = configuration.GetSection("AzureCognitiveServicesSubscriptionKey").Value;
				startupInformation.AddString("AzureCognitiveServicesSubscriptionKey", this.azureCognitiveServicesSubscriptionKey);

				this.projectId = Guid.Parse(configuration.GetSection("ProjectID").Value);
				startupInformation.AddGuid("ProjectID", this.projectId);

				this.publishedName = configuration.GetSection("PublishedName").Value;
				startupInformation.AddString("PublishedName", this.publishedName);

				this.interruptPinNumber = int.Parse(configuration.GetSection("InterruptPinNumber").Value);
				startupInformation.AddInt32("Interrupt pin", this.interruptPinNumber);

				this.interruptTriggerOn = (GpioPinEdge)Enum.Parse(typeof(GpioPinEdge), configuration.GetSection("interruptTriggerOn").Value);
				startupInformation.AddString("Interrupt Trigger on", this.interruptTriggerOn.ToString());

				this.displayPinNumber = int.Parse(configuration.GetSection("DisplayPinNumber").Value);
				startupInformation.AddInt32("Display pin", this.interruptPinNumber);

				this.debounceTimeout = TimeSpan.Parse(configuration.GetSection("debounceTimeout").Value);
				startupInformation.AddTimeSpan("Debounce timeout", this.debounceTimeout);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("JSON configuration file load or settings retrieval failed " + ex.Message, LoggingLevel.Error);
				return;
			}

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
				this.logging.LogMessage("Azure Cognitive Services Face Client configuration failed " + ex.Message, LoggingLevel.Error);
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

			this.logging.LogEvent("Application started", startupInformation);

			// enable task to continue running in background
			this.backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private async void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			DateTime currentTime = DateTime.UtcNow;
			Debug.WriteLine($"Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

			if (args.Edge != this.interruptTriggerOn)
			{
				return;
			}

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
				using (Windows.Storage.Streams.InMemoryRandomAccessStream captureStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
				{
					this.mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream).AsTask().Wait();
					captureStream.FlushAsync().AsTask().Wait();
					captureStream.Seek(0);

					IStorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(ImageFilename, CreationCollisionOption.ReplaceExisting);
					ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
					await this.mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

					ImagePrediction imagePrediction = await this.customVisionClient.ClassifyImageAsync(this.projectId, this.publishedName, captureStream.AsStreamForRead());

					Debug.WriteLine($"Prediction count {imagePrediction.Predictions.Count}");

					LoggingFields imageInformation = new LoggingFields();

					imageInformation.AddDateTime("TakenAtUTC", currentTime);
					imageInformation.AddInt32("Pin", sender.PinNumber);
					imageInformation.AddInt32("Predictions", imagePrediction.Predictions.Count);

					foreach (var prediction in imagePrediction.Predictions)
					{
						Debug.WriteLine($" Tag:{prediction.TagName} {prediction.Probability}");
						imageInformation.AddDouble($"Tag:{prediction.TagName}", prediction.Probability);
					}

					this.logging.LogEvent("Captured image processed by Cognitive Services", imageInformation);
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

		private void TimerCallback(object state)
		{
			this.displayGpioPin.Write(GpioPinValue.Low);
		}
	}
}