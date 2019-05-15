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
namespace devMobile.Windows10IotCore.IoT.DigitalInputTrigger
{
	using System;
	using System.Diagnostics;
	using Windows.ApplicationModel.Background;
	using Windows.Devices.Gpio;

	public sealed class StartupTask : IBackgroundTask
	{
		private const int InterruptPinNumber = 115; // G2 on DB410C
		private GpioPin interruptGpioPin = null;
		private BackgroundTaskDeferral backgroundTaskDeferral = null;

		public void Run(IBackgroundTaskInstance taskInstance)
		{
			Debug.WriteLine("Application startup");

			try
			{
				GpioController gpioController = GpioController.GetDefault();

				this.interruptGpioPin = gpioController.OpenPin(InterruptPinNumber);
				this.interruptGpioPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
				this.interruptGpioPin.ValueChanged += this.InterruptGpioPin_ValueChanged;

				Debug.WriteLine("Digital Input Interrupt configuration success");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Digital Input Interrupt configuration failed " + ex.Message);
				return;
			}

			// enable task to continue running in background
			this.backgroundTaskDeferral = taskInstance.GetDeferral();
		}

		private void InterruptGpioPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs args)
		{
			Debug.WriteLine($"{DateTime.UtcNow.ToLongTimeString()} Digital Input Interrupt {sender.PinNumber} triggered {args.Edge}");
		}
	}
}