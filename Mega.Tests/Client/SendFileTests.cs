namespace Mega.Tests.Client
{
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Threading.Tasks;
	using Mega.Client;
	using Microsoft.VisualStudio.TestTools.UnitTesting;

	[TestClass]
	public sealed class SendFileTests
	{
		[TestMethod]
		public async Task SendFileToContact_SeemsToWork()
		{
			using (var feedback = new DebugFeedbackChannel("Test"))
			{
				using (var initializing = feedback.BeginSubOperation("InitializeData"))
					await TestData.Current.BringToInitialState(initializing);

				using (var sending = feedback.BeginSubOperation("Sending"))
				{
					var client = new MegaClient(TestData.Current.Email1, TestData.Current.Password1);

					await client.AddContactAsync(TestData.Current.Email2, sending);
					var contactList = await client.GetContactListSnapshotAsync(sending);
					Assert.AreEqual(1, contactList.Count);
					var account2 = contactList.Single();

					var filesystem = await client.GetFilesystemSnapshotAsync(sending);

					var file = TestData.SmallFile.Find(filesystem);

					await file.SendToContactAsync(account2, sending);
				}

				using (var receiving = feedback.BeginSubOperation("Receiving"))
				{
					var client = new MegaClient(TestData.Current.Email2, TestData.Current.Password2);

					var filesystem = await client.GetFilesystemSnapshotAsync(receiving);

					var file = TestData.SmallFile.Find(filesystem);

					Assert.IsNotNull(file);

					var target = Path.GetTempFileName();

					// Verify contents.
					try
					{
						Debug.WriteLine("Temporary local file: " + target);

						await file.DownloadContentsAsync(target, feedback);

						using (var expectedContents = TestData.SmallFile.Open())
						using (var contents = File.OpenRead(target))
							TestHelper.AssertStreamsAreEqual(expectedContents, contents);
					}
					finally
					{
						File.Delete(target);
					}
				}
			}
		}

		[TestMethod]
		public async Task SendFolderToContact_SeemsToWork()
		{
			using (var feedback = new DebugFeedbackChannel("Test"))
			{
				using (var initializing = feedback.BeginSubOperation("InitializeData"))
					await TestData.Current.BringToInitialState(initializing);

				using (var sending = feedback.BeginSubOperation("Sending"))
				{
					var client = new MegaClient(TestData.Current.Email1, TestData.Current.Password1);

					await client.AddContactAsync(TestData.Current.Email2, sending);
					var contactList = await client.GetContactListSnapshotAsync(sending);
					Assert.AreEqual(1, contactList.Count);
					var account2 = contactList.Single();

					var filesystem = await client.GetFilesystemSnapshotAsync(sending);

					var folder = filesystem.Files.Children
						.Single(i => i.Type == ItemType.Folder && i.Name == "Folder1");

					await folder.SendToContactAsync(account2, sending);
				}

				using (var receiving = feedback.BeginSubOperation("Receiving"))
				{
					var client = new MegaClient(TestData.Current.Email2, TestData.Current.Password2);

					var filesystem = await client.GetFilesystemSnapshotAsync(receiving);

					var file = TestData.SmallFile.Find(filesystem);

					Assert.IsNotNull(file);
					Assert.IsNotNull(file.Parent);
					Assert.AreEqual("Folder1", file.Parent.Name);
				}
			}
		}
	}
}