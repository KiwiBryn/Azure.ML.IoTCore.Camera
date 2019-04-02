/*
    Copyright ® 2019 March devMobile Software, All Rights Reserved
 
    MIT License

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE

*/
namespace devMobile.Windows10IotCore.IoT.PhotoTimerTriggerAzureIoTHubStorage
{
	using System;
	using System.IO;
	using System.Diagnostics;
	using System.Threading;

	using Microsoft.Azure.Devices.Client;
	using Microsoft.Azure.Devices.Shared;
	using Microsoft.Extensions.Configuration;

	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.System;
	using Windows.System.Profile;
	using Windows.Storage.Streams;
	using System.Threading.Tasks;

	public sealed class StartupTask : IBackgroundTask
	{
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Timer Azure IoT Hub Storage", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private const string ConfigurationFilename = "appsettings.json";
		private string azureIoTHubConnectionString;
		private TransportType transportType;
		private DeviceClient azureIoTHubClient = null;
		private MediaCapture mediaCapture;
		private const string ImageFilenameLocal = "latest.jpg";
		private string azureStorageimageFilenameLatestFormat;
		private string azureStorageImageFilenameHistoryFormat;
		private Timer imageUpdatetimer;
		private volatile bool cameraBusy = false;
		private readonly TimeSpan DeviceRebootDelayPeriod = new TimeSpan(0, 0, 25);

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			StorageFolder localFolder = ApplicationData.Current.LocalFolder;
			Twin deviceTwin = null;
			TimeSpan imageUpdateDue;
			TimeSpan imageUpdatePeriod;

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

			this.logging.LogEvent("Application starting", startupInformation);

			#region Configuration file settings load and creation if not present
			try
			{
				// see if the configuration file is present if not copy minimal sample one from application directory
				if (localFolder.TryGetItemAsync(ConfigurationFilename).AsTask().Result == null)
				{
					StorageFile templateConfigurationfile = Package.Current.InstalledLocation.GetFileAsync(ConfigurationFilename).AsTask().Result;
					templateConfigurationfile.CopyAsync(localFolder, ConfigurationFilename).AsTask();

					this.logging.LogMessage("JSON configuration file missing, templated created", LoggingLevel.Warning);
					return;
				}

				LoggingFields connnectionInformation = new LoggingFields();
				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(localFolder.Path, ConfigurationFilename), false, true).Build();

				azureIoTHubConnectionString = configuration.GetSection("AzureIoTHubConnectionString").Value;
				connnectionInformation.AddString("AzureIoTHubConnectionString", azureIoTHubConnectionString);

				transportType = (TransportType)Enum.Parse( typeof(TransportType), configuration.GetSection("TransportType").Value);
				connnectionInformation.AddString("TransportType", transportType.ToString());

				this.logging.LogEvent("Connection", connnectionInformation);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("JSON configuration file load or AzureIoT HUb connection settings missing/invalid" + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region AzureIoT Hub connection string creation
			try
			{
				azureIoTHubClient = DeviceClient.CreateFromConnectionString(azureIoTHubConnectionString, transportType);
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

				azureIoTHubClient.UpdateReportedPropertiesAsync(reportedProperties).Wait();
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

				deviceTwin = azureIoTHubClient.GetTwinAsync().Result;

				if (!deviceTwin.Properties.Desired.Contains("AzureImageFilenameLatestFormat"))
				{
					this.logging.LogMessage("DeviceTwin.Properties AzureImageFilenameLatestFormat setting missing", LoggingLevel.Warning);
					return;
				}
				azureStorageimageFilenameLatestFormat = deviceTwin.Properties.Desired["AzureImageFilenameLatestFormat"].Value;
				configurationInformation.AddString("AzureImageFilenameLatestFormat", azureStorageimageFilenameLatestFormat);

				if (!deviceTwin.Properties.Desired.Contains("AzureImageFilenameHistoryFormat"))
				{
					this.logging.LogMessage("DeviceTwin.Properties AzureImageFilenameHistoryFormat setting missing", LoggingLevel.Warning);
					return;
				}
				azureStorageImageFilenameHistoryFormat = deviceTwin.Properties.Desired["AzureImageFilenameHistoryFormat"].Value;
				configurationInformation.AddString("AzureImageFilenameHistoryFormat", azureStorageImageFilenameHistoryFormat);

				if (!deviceTwin.Properties.Desired.Contains("ImageUpdateDue") || !TimeSpan.TryParse(deviceTwin.Properties.Desired["ImageUpdateDue"].Value.ToString(), out imageUpdateDue))
				{
					this.logging.LogMessage("DeviceTwin.Properties ImageUpdateDue setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddTimeSpan("ImageUpdateDue", imageUpdateDue);

				if (!deviceTwin.Properties.Desired.Contains("ImageUpdatePeriod") || !TimeSpan.TryParse(deviceTwin.Properties.Desired["ImageUpdatePeriod"].Value.ToString(), out imageUpdatePeriod))
				{
					this.logging.LogMessage("DeviceTwin.Properties ImageUpdatePeriod setting missing or invalid format", LoggingLevel.Warning);
					return;
				}
				configurationInformation.AddTimeSpan("ImageUpdatePeriod", imageUpdatePeriod);

				this.logging.LogEvent("Configuration", configurationInformation);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client GetTwinAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region Initialise the camera hardware
			try
			{
				mediaCapture = new MediaCapture();
				mediaCapture.InitializeAsync().AsTask().Wait();
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("mediaCapture.InitializeAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			#region Wire up command handler for image capture request
			try
			{
				azureIoTHubClient.SetMethodHandlerAsync("ImageCapture", ImageUpdateHandler, null);
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
				azureIoTHubClient.SetMethodHandlerAsync("DeviceReboot", DeviceRebootAsync, null);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Azure IoT Hub client DeviceReboot SetMethodHandlerAsync failed " + ex.Message, LoggingLevel.Error);
				return;
			}
			#endregion

			imageUpdatetimer = new Timer(ImageUpdateTimerCallback, null, imageUpdateDue, imageUpdatePeriod);

			this.logging.LogEvent("Application startup completed");

			//enable task to continue running in background
			backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private async Task<MethodResponse> ImageUpdateHandler(MethodRequest methodRequest, object userContext)
		{
			Debug.WriteLine($"{DateTime.UtcNow.ToString("yy-MM-ss HH:mm:ss")} Method handler triggered");

			await ImageUpdate(true);

			return new MethodResponse(200);
		}

		private async void ImageUpdateTimerCallback(object state)
		{
			Debug.WriteLine($"{DateTime.UtcNow.ToString("yy-MM-ss HH:mm:ss")} Timer triggered");

			await ImageUpdate(false);
		}

		private async Task ImageUpdate(bool isCommand)
		{ 
			DateTime currentTime = DateTime.UtcNow;

			// Just incase - stop code being called while photo already in progress
			if (cameraBusy)
			{
				return;
			}
			cameraBusy = true;

			try
			{
				using (Windows.Storage.Streams.InMemoryRandomAccessStream captureStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
				{
					await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream);
					await captureStream.FlushAsync();
#if DEBUG
					IStorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(ImageFilenameLocal, CreationCollisionOption.ReplaceExisting);
					ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
					await mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);
#endif

					string azureFilenameLatest = string.Format(azureStorageimageFilenameLatestFormat, currentTime);
					string azureFilenameHistory = string.Format(azureStorageImageFilenameHistoryFormat, currentTime);

					LoggingFields imageInformation = new LoggingFields();
					imageInformation.AddDateTime("TakenAtUTC", currentTime);
					imageInformation.AddBoolean("IsCommand", isCommand);
#if DEBUG
					imageInformation.AddString("LocalFilename", photoFile.Path);
#endif
					imageInformation.AddString("AzureFilenameLatest", azureFilenameLatest);
					imageInformation.AddString("AzureFilenameHistory", azureFilenameHistory);
					this.logging.LogEvent("Saving image(s) to Azure storage", imageInformation);

					// Update the latest image in storage
					if (!string.IsNullOrWhiteSpace(azureFilenameLatest))
					{
						captureStream.Seek(0);
						Debug.WriteLine("AzureIoT Hub latest image upload start");
						await azureIoTHubClient.UploadToBlobAsync(azureFilenameLatest, captureStream.AsStreamForRead());
						Debug.WriteLine("AzureIoT Hub latest image upload done");
					}

					// Upload the historic image to storage
					if (!string.IsNullOrWhiteSpace(azureFilenameHistory))
					{
						captureStream.Seek(0);
						Debug.WriteLine("AzureIoT Hub historic image upload start");
						await azureIoTHubClient.UploadToBlobAsync(azureFilenameHistory, captureStream.AsStreamForRead());
						Debug.WriteLine("AzureIoT Hub historic image upload done");
					}
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Image capture or AzureIoTHub storage upload failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				cameraBusy = false;
			}
		}

		private async Task<MethodResponse> DeviceRebootAsync(MethodRequest methodRequest, object userContext)
		{
			this.logging.LogEvent("Reboot initiated");

			// Stop the image capture timer before reboot
			if (imageUpdatetimer != null)
			{
				imageUpdatetimer.Change(Timeout.Infinite, Timeout.Infinite);
			}

			ShutdownManager.BeginShutdown(ShutdownKind.Restart, DeviceRebootDelayPeriod);

			return new MethodResponse(200);
		}
	}
}