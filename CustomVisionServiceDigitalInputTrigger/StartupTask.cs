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
	using System.IO;
	using System.Linq;
	using System.Net.NetworkInformation;

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
		private BackgroundTaskDeferral backgroundTaskDeferral = null;
		private readonly LoggingChannel logging = new LoggingChannel("devMobile Photo Digital Input Trigger Custom Vision", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
		private const string ConfigurationFilename = "appsettings.json";
		private GpioPin interruptGpioPin = null;
		private GpioPinEdge interruptTriggerOn = GpioPinEdge.RisingEdge;
		private int interruptPinNumber;
		private TimeSpan debounceTimeout;
		private DateTime imageLastCapturedAtUtc = DateTime.MinValue;
		private MediaCapture mediaCapture;
		private string deviceMacAddress;
		//private string azureStorageConnectionString;
		private const string ImageFilenameFormat = "Image{0:yyMMddhhmmss}.jpg";
		private volatile bool CameraBusy = false;

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

				/*
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
				*/

				interruptPinNumber = int.Parse(configuration.GetSection("InterruptPinNumber").Value);
				startupInformation.AddInt32("Interrupt pin", interruptPinNumber);

				interruptTriggerOn = (GpioPinEdge)Enum.Parse(typeof(GpioPinEdge), configuration.GetSection("interruptTriggerOn").Value);
				startupInformation.AddString("Interrupt Trigger on", interruptTriggerOn.ToString());

				debounceTimeout = TimeSpan.Parse(configuration.GetSection("debounceTimeout").Value);
				startupInformation.AddTimeSpan("Debounce timeout", debounceTimeout);
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

		private void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			DateTime currentTime = DateTime.UtcNow;
			Debug.WriteLine($"Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");

			if (args.Edge == interruptTriggerOn)
			{
				return;
			}

			// Check that enough time has passed for picture to be taken
			if ((currentTime - imageLastCapturedAtUtc) < debounceTimeout)
			{
				return;
			}
			imageLastCapturedAtUtc = currentTime;

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
			finally
			{
				CameraBusy = false;
			}
		}
	}
}
