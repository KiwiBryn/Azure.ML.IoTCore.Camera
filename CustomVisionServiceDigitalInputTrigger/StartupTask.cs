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
namespace devMobile.Windows10IotCore.IoT.CustomVisionServiceDigitalInputTrigger
{
	using System;
	using System.Diagnostics;
	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;

	public sealed class StartupTask : IBackgroundTask
	{
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input Trigger demo", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private GpioPin InterruptGpioPin = null;
		private const int InterruptPinNumber = 5;
		private MediaCapture mediaCapture;
		private const string ImageFilenameFormat = "Image{0:yyMMddhhmmss}.jpg";
		private volatile bool CameraBusy = false;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			Debug.WriteLine("Application startup");

			try
			{
				mediaCapture = new MediaCapture();
				mediaCapture.InitializeAsync().AsTask().Wait();
				Debug.WriteLine("Camera configuration success");

				GpioController gpioController = GpioController.GetDefault();

				InterruptGpioPin = gpioController.OpenPin(InterruptPinNumber);
				InterruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				InterruptGpioPin.ValueChanged += InterruptGpioPin_ValueChanged;
				Debug.WriteLine("Digital Input Interrupt configuration success");
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera or digital input configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			//enable task to continue running in background
			backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			DateTime currentTime = DateTime.UtcNow;
			Debug.WriteLine($"Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

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
				using (Windows.Storage.Streams.InMemoryRandomAccessStream captureStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
				{
					mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream).AsTask().Wait();
					captureStream.FlushAsync().AsTask().Wait();
					captureStream.Seek(0);

					string filename = string.Format(ImageFilenameFormat, currentTime);

					IStorageFile photoFile = KnownFolders.PicturesLibrary.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting).AsTask().Result;
					ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
					mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile).AsTask().Wait();

					LoggingFields imageInformation = new LoggingFields();

					imageInformation.AddDateTime("TakenAtUTC", currentTime);
					imageInformation.AddInt32("Pin", sender.PinNumber);
					imageInformation.AddString("Path", photoFile.Path);
					imageInformation.AddString("Filename", filename);
					imageInformation.AddUInt32("Height", imageProperties.Height);
					imageInformation.AddUInt32("Width", imageProperties.Width);
					imageInformation.AddUInt64("Size", captureStream.Size);
					this.logging.LogEvent("Captured image saved to storage", imageInformation);
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo or save failed " + ex.Message, LoggingLevel.Error);
			}

			CameraBusy = false;
		}
	}
}
