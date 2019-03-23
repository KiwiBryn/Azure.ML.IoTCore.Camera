﻿/*
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
	using System.Linq;
	using System.Net.NetworkInformation;
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
		private string deviceMacAddress;
		private string azureStorageConnectionString;
		private string azureStorageContainerNameLatestFormat;
		private string azureStorageimageFilenameLatestFormat;
		private string azureStorageContainerNameHistoryFormat;
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

			// ethernet mac address
			deviceMacAddress = NetworkInterface.GetAllNetworkInterfaces()
				 .Where(i => i.NetworkInterfaceType.ToString().ToLower().Contains("ethernet"))
				 .FirstOrDefault()
				 ?.GetPhysicalAddress().ToString();

			// remove unsupported charachers from MacAddress
			deviceMacAddress = deviceMacAddress.Replace("-", "").Replace(" ", "").Replace(":", "");
			startupInformation.AddString("MacAddress", deviceMacAddress);

			try
			{
				// see if the configuration file is present if not copy minimal sample one from application directory
				if (localFolder.TryGetItemAsync(ConfigurationFilename).AsTask().Result == null)
				{
					StorageFile templateConfigurationfile = Package.Current.InstalledLocation.GetFileAsync(ConfigurationFilename).AsTask().Result;
					templateConfigurationfile.CopyAsync(localFolder, ConfigurationFilename).AsTask();
				}

				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(localFolder.Path, ConfigurationFilename), false, true).Build();

				azureStorageConnectionString = configuration.GetSection("AzureStorageConnectionString").Value;
				startupInformation.AddString("AzureStorageConnectionString", azureStorageConnectionString);

				azureStorageContainerNameLatestFormat = configuration.GetSection("AzureContainerNameFormatLatest").Value;
				startupInformation.AddString("ContainerNameLatestFormat", azureStorageContainerNameLatestFormat);

				azureStorageimageFilenameLatestFormat = configuration.GetSection("AzureImageFilenameFormatLatest").Value;
				startupInformation.AddString("ImageFilenameLatestFormat", azureStorageimageFilenameLatestFormat);

				azureStorageContainerNameHistoryFormat = configuration.GetSection("AzureContainerNameFormatHistory").Value;
				startupInformation.AddString("ContainerNameHistoryFormat", azureStorageContainerNameHistoryFormat);

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
				StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(ImageFilenameLocal, CreationCollisionOption.ReplaceExisting);
				ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
				await mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

				/*
				string azureContainernameLatest = string.Format(azureStorageContainerNameLatestFormat, Environment.MachineName, deviceMacAddress, currentTime).ToLower();
				string azureFilenameLatest = string.Format(azureStorageimageFilenameLatestFormat, Environment.MachineName, deviceMacAddress, currentTime);
				string azureContainerNameHistory = string.Format(azureStorageContainerNameHistoryFormat, Environment.MachineName, deviceMacAddress, currentTime).ToLower();
				string azureFilenameHistory = string.Format(azureStorageImageFilenameHistoryFormat, Environment.MachineName.ToLower(), deviceMacAddress, currentTime);

				LoggingFields imageInformation = new LoggingFields();
				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddString("LocalFilename", photoFile.Path);
				imageInformation.AddString("AzureContainerNameLatest", azureContainernameLatest);
				imageInformation.AddString("AzureFilenameLatest", azureFilenameLatest);
				imageInformation.AddString("AzureContainerNameHistory", azureContainerNameHistory);
				imageInformation.AddString("AzureFilenameHistory", azureFilenameHistory);
				this.logging.LogEvent("Saving image(s) to Azure storage", imageInformation);

				CloudStorageAccount storageAccount = CloudStorageAccount.Parse(azureStorageConnectionString);
				CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

				// Update the latest image in storage
				if (!string.IsNullOrWhiteSpace(azureContainernameLatest) && !string.IsNullOrWhiteSpace(azureFilenameLatest))
				{
					CloudBlobContainer containerLatest = blobClient.GetContainerReference(azureContainernameLatest);
					await containerLatest.CreateIfNotExistsAsync();

					CloudBlockBlob blockBlobLatest = containerLatest.GetBlockBlobReference(azureFilenameLatest);
					await blockBlobLatest.UploadFromFileAsync(photoFile);

					this.logging.LogEvent("Image latest saved to Azure storage");
				}

				// Upload the historic image to storage
				if (!string.IsNullOrWhiteSpace(azureContainerNameHistory) && !string.IsNullOrWhiteSpace(azureFilenameHistory))
				{
					CloudBlobContainer containerHistory = blobClient.GetContainerReference(azureContainerNameHistory);
					await containerHistory.CreateIfNotExistsAsync();

					CloudBlockBlob blockBlob = containerHistory.GetBlockBlobReference(azureFilenameHistory);
					await blockBlob.UploadFromFileAsync(photoFile);

					this.logging.LogEvent("Image historic saved to Azure storage");
				}
				*/
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo save or upload failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				cameraBusy = false;
			}
		}
	}
}
