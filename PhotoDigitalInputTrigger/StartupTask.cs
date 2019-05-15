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

namespace devMobile.Windows10IotCore.IoT.PhotoDigitalInputTrigger
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
		private const string ImageFilenameFormat = "Image{0:yyMMddhhmmss}.jpg";
		private const int InterruptPinNumber = 115; // G2 on DB410C;
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input demo", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private GpioPin interruptGpioPin = null;
		private MediaCapture mediaCapture;
		private volatile bool cameraBusy = false;
		private BackgroundTaskDeferral backgroundTaskDeferral = null;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			LoggingFields startupInformation = new LoggingFields();

			this.logging.LogEvent("Application starting");

			try
			{
				this.mediaCapture = new MediaCapture();
				this.mediaCapture.InitializeAsync().AsTask().Wait();
				Debug.WriteLine("Camera configuration success");

				GpioController gpioController = GpioController.GetDefault();

				this.interruptGpioPin = gpioController.OpenPin(InterruptPinNumber);
				this.interruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				this.interruptGpioPin.ValueChanged += this.InterruptGpioPin_ValueChanged;
				Debug.WriteLine("Digital Input Interrupt configuration success");
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera or digital input configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			startupInformation.AddString("PrimaryUse", this.mediaCapture.VideoDeviceController.PrimaryUse.ToString());
			startupInformation.AddInt32("Interrupt pin", InterruptPinNumber);

			this.logging.LogEvent("Application started", startupInformation);

			// enable task to continue running in background
			this.backgroundTaskDeferral = taskInstance.GetDeferral();
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
			if (this.cameraBusy)
			{
				return;
			}

			this.cameraBusy = true;

			try
			{
				string filename = string.Format(ImageFilenameFormat, currentTime);

				IStorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
				ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
				await this.mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

				LoggingFields imageInformation = new LoggingFields();

				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddString("Filename", filename);
				imageInformation.AddString("Path", photoFile.Path);

				this.logging.LogEvent("Captured image saved to storage", imageInformation);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Image capture or save failed " + ex.Message, LoggingLevel.Error);
			}

			this.cameraBusy = false;
		}
	}
}
