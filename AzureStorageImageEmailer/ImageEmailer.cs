// <copyright file="ImageEmailer.cs" company="devMobile Software">
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

namespace devMobile.Azure.Storage
{
	using System.Configuration;
	using System.IO;
	using System.Threading.Tasks;
	using Microsoft.Azure.WebJobs;
	using Microsoft.Azure.WebJobs.Host;

	using SendGrid.Helpers.Mail;

	public static class ImageEmailer
	{
		[FunctionName("ImageEmailer")]
		public static async Task Run(
				[BlobTrigger("current/{name}")]
				Stream inputBlob,
				string name,
				[SendGrid(ApiKey = "")]
				IAsyncCollector<SendGridMessage> messageCollector,
				TraceWriter log)
		{
			log.Info($"C# Blob trigger function Processed blob Name:{name} Size: {inputBlob.Length} Bytes");

			SendGridMessage message = new SendGridMessage();
			message.AddTo(new EmailAddress(ConfigurationManager.AppSettings["EmailAddressTo"]));
			message.From = new EmailAddress(ConfigurationManager.AppSettings["EmailAddressFrom"]);
			message.SetSubject("RPI Web camera Image attached");
			message.AddContent("text/plain", $"{name} {inputBlob.Length} bytes");

			await message.AddAttachmentAsync(name, inputBlob, "image/jpeg");

			await messageCollector.AddAsync(message);
		}
	}
}
