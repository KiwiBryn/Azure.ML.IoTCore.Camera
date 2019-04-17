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
namespace devMobile.Windows10IotCore.IoT.PhotoTimer
{
	using System;
	using System.Threading;
	using Windows.ApplicationModel.Background;
	using Windows.Foundation.Diagnostics;
	using Windows.Media.Capture;
	using Windows.Media.MediaProperties;
	using Windows.Storage;

	public sealed class StartupTask : IBackgroundTask
	{
		private const string ImageFilenameFormat = "Image{0:yyMMddhhmmss}.jpg";
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Timer Photo demo", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private readonly TimeSpan imageUpdateDueDefault = new TimeSpan(0, 0, 15);
		private readonly TimeSpan imageUpdatePeriodDefault = new TimeSpan(0, 5, 0);
		private MediaCapture mediaCapture;
		private Timer imageUpdatetimer;
		private BackgroundTaskDeferral backgroundTaskDeferral = null;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			LoggingFields startupInformation = new LoggingFields();

			this.logging.LogEvent("Application starting");

			try
			{
				this.mediaCapture = new MediaCapture();
				this.mediaCapture.InitializeAsync().AsTask().Wait();

				this.imageUpdatetimer = new Timer(this.ImageUpdateTimerCallback, null, this.imageUpdateDueDefault, this.imageUpdatePeriodDefault);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			startupInformation.AddString("PrimaryUse", this.mediaCapture.VideoDeviceController.PrimaryUse.ToString());
			startupInformation.AddTimeSpan("Due", this.imageUpdateDueDefault);
			startupInformation.AddTimeSpan("Period", this.imageUpdatePeriodDefault);

			this.logging.LogEvent("Application started", startupInformation);

			// enable task to continue running in background
			this.backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private void ImageUpdateTimerCallback(object state)
		{
			DateTime currentTime = DateTime.UtcNow;

			try
			{
				string filename = string.Format(ImageFilenameFormat, currentTime);

				IStorageFile photoFile = KnownFolders.PicturesLibrary.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting).AsTask().Result;
				ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
				this.mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile).AsTask().Wait();

				LoggingFields imageInformation = new LoggingFields();
				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddString("Filename", filename);
				imageInformation.AddString("Path", photoFile.Path);
				this.logging.LogEvent("Image saved to storage", imageInformation);
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Image capture or save to local storage failed " + ex.Message, LoggingLevel.Error);
			}
		}
	}
}
