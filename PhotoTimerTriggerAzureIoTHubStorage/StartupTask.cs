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
	using Microsoft.Extensions.Configuration;

	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.System;
	
	public sealed class StartupTask : IBackgroundTask
	{
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Timer Azure IoT Hub Storage", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private DeviceClient azureIoTHubClient = null;
		private const string ConfigurationFilename = "appsettings.json";
		private Timer ImageUpdatetimer;
		private MediaCapture mediaCapture;
		private string azureIoTHubConnectionString;
		private TransportType transportType;
		private string azureStorageimageFilenameLatestFormat;
		private string azureStorageImageFilenameHistoryFormat;
		private const string ImageFilenameLocal = "latest.jpg";
		private volatile bool cameraBusy = false;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			StorageFolder localFolder = ApplicationData.Current.LocalFolder;
			int imageUpdateDueSeconds;
			int imageUpdatePeriodSeconds;

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

					this.logging.LogMessage("JSON configuration file missing, templated created", LoggingLevel.Warning);
					return;
				}

				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(localFolder.Path, ConfigurationFilename), false, true).Build();

				azureIoTHubConnectionString = configuration.GetSection("AzureIoTHubConnectionString").Value;
				startupInformation.AddString("AzureIoTHubConnectionString", azureIoTHubConnectionString);

				transportType = (TransportType)Enum.Parse( typeof(TransportType), configuration.GetSection("TransportType").Value);
				startupInformation.AddString("TransportType", transportType.ToString());

				azureStorageimageFilenameLatestFormat = configuration.GetSection("AzureImageFilenameFormatLatest").Value;
				startupInformation.AddString("ImageFilenameLatestFormat", azureStorageimageFilenameLatestFormat);

				azureStorageImageFilenameHistoryFormat = configuration.GetSection("AzureImageFilenameFormatHistory").Value;
				startupInformation.AddString("ImageFilenameHistoryFormat", azureStorageImageFilenameHistoryFormat);

				imageUpdateDueSeconds = int.Parse(configuration.GetSection("ImageUpdateDueSeconds").Value);
				startupInformation.AddInt32("ImageUpdateDueSeconds", imageUpdateDueSeconds);

				imageUpdatePeriodSeconds = int.Parse(configuration.GetSection("ImageUpdatePeriodSeconds").Value);
				startupInformation.AddInt32("ImageUpdatePeriodSeconds", imageUpdatePeriodSeconds);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("JSON configuration file load or settings retrieval failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			try
			{
				azureIoTHubClient = DeviceClient.CreateFromConnectionString(azureIoTHubConnectionString, transportType);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("AzureIOT Hub connection failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			try
			{
				mediaCapture = new MediaCapture();
				mediaCapture.InitializeAsync().AsTask().Wait();
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			ImageUpdatetimer = new Timer(ImageUpdateTimerCallback, null, new TimeSpan(0, 0, imageUpdateDueSeconds), new TimeSpan(0, 0, imageUpdatePeriodSeconds));

			this.logging.LogEvent("Application started", startupInformation);

			//enable task to continue running in background
			backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private async void ImageUpdateTimerCallback(object state)
		{
			DateTime currentTime = DateTime.UtcNow;
			Debug.WriteLine($"{DateTime.UtcNow.ToLongTimeString()} Timer triggered");

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
				this.logging.LogMessage("Camera photo save or AzureIoTHub storage upload failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				cameraBusy = false;
			}
		}
	}
}


