/*
    Copyright ® 2019 Feb devMobile Software, All Rights Reserved
 
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

	using Microsoft.WindowsAzure.Storage;
	using Microsoft.WindowsAzure.Storage.Blob;

	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;
	using Windows.Storage.Streams;

	public sealed class StartupTask : IBackgroundTask
	{
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input Trigger Azure Storage demo", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private GpioPin InterruptGpioPin = null;
		private const int InterruptPinNumber = 5;
		private MediaCapture mediaCapture;
		private const string ImageFilenameLatest = "latest.jpg";
		private const string ImageFilenameFormat = "image{1:yyMMddhhmmss}.jpg";
		private const string ContainerNameFormat = "{0}{1:yyMMdd}";
		private volatile bool CameraBusy = false;
		private const string AzureStorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=..;EndpointSuffix=...";

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			this.logging.LogEvent("Application starting");

			try
			{
				mediaCapture = new MediaCapture();
				mediaCapture.InitializeAsync().AsTask().Wait();

				GpioController gpioController = GpioController.GetDefault();
				InterruptGpioPin = gpioController.OpenPin(InterruptPinNumber);
				InterruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				InterruptGpioPin.ValueChanged += InterruptGpioPin_ValueChanged;
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera or digital input configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			LoggingFields startupInformation = new LoggingFields();
			startupInformation.AddString("PrimaryUse", mediaCapture.VideoDeviceController.PrimaryUse.ToString());
			startupInformation.AddInt32("Interrupt pin", InterruptPinNumber);
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
			if (CameraBusy)
			{
				return;
			}
			CameraBusy = true;

			try
			{
				using (InMemoryRandomAccessStream captureStream = new InMemoryRandomAccessStream())
				{
					string filename = string.Format(ImageFilenameFormat, Environment.MachineName.ToLower(), currentTime);

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

					CloudStorageAccount storageAccount = CloudStorageAccount.Parse(AzureStorageConnectionString);
					CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

					string containername = string.Format(ContainerNameFormat, Environment.MachineName.ToLower(), currentTime);
					CloudBlobContainer container = blobClient.GetContainerReference(containername);
					await container.CreateIfNotExistsAsync();

					CloudBlockBlob blockBlob = container.GetBlockBlobReference(filename);
					await blockBlob.UploadFromFileAsync(photoFile);

					blockBlob = container.GetBlockBlobReference(ImageFilenameLatest);
					await blockBlob.UploadFromFileAsync(photoFile);
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo or save failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				CameraBusy = false;
			}
		}
	}
}
