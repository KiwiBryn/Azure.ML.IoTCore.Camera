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

namespace devMobile.Windows10IotCore.IoT.PhotoDigitalInputTriggerLocalStorage
{
	using System;
	using System.Diagnostics;
	using System.IO;

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
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input Local Storage", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private GpioPin interruptGpioPin = null;
		private GpioPinEdge interruptTriggerOn = GpioPinEdge.RisingEdge;
		private int interruptPinNumber;
		private TimeSpan debounceTimeout;
		private DateTime imageLastCapturedAtUtc = DateTime.MinValue;
		private MediaCapture mediaCapture;
		private string localStorageimageFilenameLatestFormat;
		private string localStorageImageFilenameHistoryFormat;
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

					this.logging.LogMessage("JSON configuration file missing, templated created", LoggingLevel.Warning);
					return;
				}

				IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(localFolder.Path, ConfigurationFilename), false, true).Build();

				this.localStorageimageFilenameLatestFormat = configuration.GetSection("LocalImageFilenameFormatLatest").Value;
				startupInformation.AddString("ImageFilenameLatestFormat", this.localStorageimageFilenameLatestFormat);

				this.localStorageImageFilenameHistoryFormat = configuration.GetSection("LocalImageFilenameFormatHistoric").Value;
				startupInformation.AddString("ImageFilenameLatestFormat", this.localStorageImageFilenameHistoryFormat);

				this.interruptPinNumber = int.Parse(configuration.GetSection("InterruptPinNumber").Value);
				startupInformation.AddInt32("Interrupt pin", this.interruptPinNumber);

				this.interruptTriggerOn = (GpioPinEdge)Enum.Parse(typeof(GpioPinEdge), configuration.GetSection("interruptTriggerOn").Value);
				startupInformation.AddString("Interrupt Trigger on", this.interruptTriggerOn.ToString());

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
				this.mediaCapture = new MediaCapture();
				this.mediaCapture.InitializeAsync().AsTask().Wait();
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera configuration failed " + ex.Message, LoggingLevel.Error);
				return;
			}

			try
			{
				GpioController gpioController = GpioController.GetDefault();
				this.interruptGpioPin = gpioController.OpenPin(this.interruptPinNumber);
				this.interruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				this.interruptGpioPin.ValueChanged += this.InterruptGpioPin_ValueChanged;
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
			Debug.WriteLine($"{DateTime.UtcNow.ToLongTimeString()} Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

			if (args.Edge == this.interruptTriggerOn)
			{
				return;
			}

			// Check that enough time has passed for picture to be taken
			if ((currentTime - this.imageLastCapturedAtUtc) < this.debounceTimeout)
			{
				return;
			}

			this.imageLastCapturedAtUtc = currentTime;

			// Just incase - stop code being called while photo already in progress
			if (this.cameraBusy)
			{
				return;
			}

			this.cameraBusy = true;

			try
			{
				string localFilenameLatest = string.Format(this.localStorageimageFilenameLatestFormat, Environment.MachineName.ToLower(), currentTime);
				string localFilenameHistory = string.Format(this.localStorageImageFilenameHistoryFormat, Environment.MachineName.ToLower(), currentTime);

				StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(localFilenameLatest, CreationCollisionOption.ReplaceExisting);
				ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
				await this.mediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

				LoggingFields imageInformation = new LoggingFields();
				imageInformation.AddDateTime("TakenAtUTC", currentTime);
				imageInformation.AddString("LocalFilename", photoFile.Path);
				imageInformation.AddString("LocalFilenameHistory", localFilenameHistory);
				this.logging.LogEvent("Saving image(s) to local storage", imageInformation);

				// copy the historic image to storage
				if (!string.IsNullOrWhiteSpace(localFilenameHistory))
				{
					await photoFile.CopyAsync(KnownFolders.PicturesLibrary, localFilenameHistory);
					this.logging.LogEvent("Image history saved to local storage");
				}
			}
			catch (Exception ex)
			{
				this.logging.LogMessage("Camera photo save or upload failed " + ex.Message, LoggingLevel.Error);
			}
			finally
			{
				this.cameraBusy = false;
			}
		}
	}
}
