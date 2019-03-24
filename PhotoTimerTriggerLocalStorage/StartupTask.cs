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
namespace devMobile.Windows10IotCore.IoT.PhotoTimerTriggerLocalStorage
{
	using System;
	using System.IO;
	using System.Diagnostics;
	using System.Threading;

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
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Timer Trigger Local Storage", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private const string ConfigurationFilename = "appsettings.json";
		private Timer ImageUpdatetimer;
		private MediaCapture mediaCapture;
		private string localImageFilenameLatestFormat;
		private string localFolderNameHistoryFormat;
		private string localImageFilenameHistoryFormat;
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

				localImageFilenameLatestFormat = configuration.GetSection("ImageFilenameFormatLatest").Value;
				startupInformation.AddString("ImageFilenameLatestFormat", localImageFilenameLatestFormat);

				localFolderNameHistoryFormat = configuration.GetSection("FolderNameFormatHistory").Value;
				startupInformation.AddString("ContainerNameHistoryFormat", localFolderNameHistoryFormat);

				localImageFilenameHistoryFormat = configuration.GetSection("ImageFilenameFormatHistory").Value;
				startupInformation.AddString("ImageFilenameHistoryFormat", localImageFilenameHistoryFormat);

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
				string localFilename = string.Format(localImageFilenameLatestFormat, currentTime);
				string folderNameHistory = string.Format(localFolderNameHistoryFormat, currentTime);
				string filenameHistory = string.Format(localImageFilenameHistoryFormat, currentTime);

				StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(localFilename, CreationCollisionOption.ReplaceExisting);
				ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
				await mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

				LoggingFields imageInformation = new LoggingFields();
				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddString("LocalFilename", photoFile.Path);
				imageInformation.AddString("FolderNameHistory", folderNameHistory);
				imageInformation.AddString("FilenameHistory", filenameHistory);
				this.logging.LogEvent("Image saved to local storage", imageInformation);

				// Upload the historic image to storage
				if (!string.IsNullOrWhiteSpace(folderNameHistory) && !string.IsNullOrWhiteSpace(filenameHistory))
				{
					// Check to see if historic images folder exists and if it doesn't create it
					IStorageFolder storageFolder = (IStorageFolder)await KnownFolders.PicturesLibrary.TryGetItemAsync(folderNameHistory);
					if (storageFolder == null)
					{
						storageFolder = await KnownFolders.PicturesLibrary.CreateFolderAsync(folderNameHistory);
					}
					await photoFile.CopyAsync(storageFolder, filenameHistory, NameCollisionOption.ReplaceExisting);

					this.logging.LogEvent("Image historic saved to local storage", imageInformation);
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo or image save failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				cameraBusy = false;
			}
		}
	}
}

