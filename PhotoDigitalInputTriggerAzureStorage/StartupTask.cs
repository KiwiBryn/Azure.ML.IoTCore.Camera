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
namespace devMobile.Windows10IotCore.IoT.PhotoDigitalInputTriggerAzureStorage
{
	using System;
	using System.Diagnostics;

	using Microsoft.Extensions.Configuration;
	using Microsoft.WindowsAzure.Storage;
	using Microsoft.WindowsAzure.Storage.Blob;

	using Windows.ApplicationModel;
	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.Storage.Streams;
	using Windows.System;

	public sealed class StartupTask : IBackgroundTask
	{
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input Trigger Azure Storage demo", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private const string ConfigurationFilename = "appsettings.json";
		private GpioPin interruptGpioPin = null;
		private int interruptPinNumber;
		private MediaCapture mediaCapture;
		private string azureStorageConnectionString;
		private string imageFilenameLatest;
		private string imageFilenameFormat;
		private string containerNameFormat;
		private volatile bool cameraBusy = false;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			StorageFolder localFolder = ApplicationData.Current.LocalFolder;

			this.logging.LogEvent("Application starting");

			// Log the Application build, shield information etc.
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

				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(localFolder.Path + @"\" + "appsettings.json", false, true).Build();

				azureStorageConnectionString = configuration.GetSection("AzureStorageConnectionString").Value;
				startupInformation.AddString("AzureStorageConnectionString", azureStorageConnectionString);

				imageFilenameLatest = configuration.GetSection("ImageFilenameLatest").Value;
				startupInformation.AddString("ImageFilenameLatest", imageFilenameLatest);

				imageFilenameFormat = configuration.GetSection("ImageFilenameFormat").Value;
				startupInformation.AddString("ImageFilenameFormat", imageFilenameFormat);

				containerNameFormat = configuration.GetSection("ContainerNameFormat").Value;
				startupInformation.AddString("ContainerNameFormat", containerNameFormat);

				interruptPinNumber = int.Parse( configuration.GetSection("InterruptPinNumber").Value);
				startupInformation.AddInt32("Interrupt pin", interruptPinNumber);
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

			try
			{
				GpioController gpioController = GpioController.GetDefault();
				interruptGpioPin = gpioController.OpenPin(interruptPinNumber);
				interruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				interruptGpioPin.ValueChanged += InterruptGpioPin_ValueChanged;
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Digital input configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			this.logging.LogEvent("Application started", startupInformation);

			//enable task to continue running in background
			backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private async void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			DateTime currentTime = DateTime.UtcNow;
			Debug.WriteLine($"{DateTime.UtcNow.ToLongTimeString()} Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

			if (args.Edge == GpioPinEdge.RisingEdge)
			{
				return;
			}

			// Just incase - stop code being called while photo already in progress
			if (cameraBusy)
			{
				return;
			}
			cameraBusy = true;

			try
			{
				using (InMemoryRandomAccessStream captureStream = new InMemoryRandomAccessStream())
				{
					string filename = string.Format(imageFilenameFormat, Environment.MachineName.ToLower(), currentTime);

					StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
					ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
					await mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

					LoggingFields imageInformation = new LoggingFields();
					imageInformation.AddDateTime("TakenAtUTC", currentTime);
					imageInformation.AddInt32("Pin", sender.PinNumber);
					imageInformation.AddString("Path", photoFile.Path);
					imageInformation.AddString("Filename", filename);
					imageInformation.AddUInt32("Height", imageProperties.Height);
					imageInformation.AddUInt32("Width", imageProperties.Width);
					imageInformation.AddUInt64("Size", captureStream.Size);
					this.logging.LogEvent("Captured image saved to storage", imageInformation);

					CloudStorageAccount storageAccount = CloudStorageAccount.Parse(azureStorageConnectionString);
					CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

					string containername = string.Format(containerNameFormat, Environment.MachineName.ToLower(), currentTime);
					CloudBlobContainer container = blobClient.GetContainerReference(containername);
					await container.CreateIfNotExistsAsync();

					CloudBlockBlob blockBlob = container.GetBlockBlobReference(filename);
					await blockBlob.UploadFromFileAsync(photoFile);

					blockBlob = container.GetBlockBlobReference(imageFilenameLatest);
					await blockBlob.UploadFromFileAsync(photoFile);
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo or save failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				cameraBusy = false;
			}
		}
	}
}
