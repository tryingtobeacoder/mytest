namespace Mega.Client
{
	using System;
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using System.Diagnostics;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using Api;
	using Api.Messages;
	using Newtonsoft.Json.Linq;
	using Useful;

	/// <summary>
	/// A client connection to one Mega cloud filesystem account.
	/// </summary>
	/// <remarks>
	/// Connection management is performed automatically, including reconnection and retry when possible.
	/// All InternalAsync functions assume that the lock is already held by the caller.
	/// </remarks>
	public sealed class MegaClient : IDisposable
	{
		/// <summary>
		/// Gets the ID of the Mega account used by this client.
		/// </summary>
		public OpaqueID AccountID { get; set; }

		/// <summary>
		/// Gets the email address of the Mega account used by this client.
		/// </summary>
		public string AccountEmail { get; private set; }

		/// <summary>
		/// Gets a snapshot of the cloud filesystem's current state. You can use this snapshot to operate on the filesystem. The
		/// snapshot itself is not kept up to date by the client, so you should get a new snapshot after performing operations on it.
		/// </summary>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		public async Task<FilesystemSnapshot> GetFilesystemSnapshotAsync(IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.LogMethodCall("GetFilesystemSnapshotAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			using (await AcquireLock(feedbackChannel, cancellationToken))
			{
				await EnsureFilesystemAndContactlistAvailableInternalAsync(feedbackChannel, cancellationToken);

				return _currentFilesystemSnapshot;
			}
		}

		/// <summary>
		/// Gets a snapshot of the contact list's current state. You can use this snapshot to operate with
		/// contacts (e.g. send files to them). The snapshot itself is not kept up to date
		/// by the client, so you should get a new snapshot after performing operations on it.
		/// </summary>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		public async Task<IImmutableSet<Contact>> GetContactListSnapshotAsync(IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.LogMethodCall("GetContactListSnapshotAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			using (await AcquireLock(feedbackChannel, cancellationToken))
			{
				await EnsureFilesystemAndContactlistAvailableInternalAsync(feedbackChannel, cancellationToken);

				return _currentContactListSnapshot;
			}
		}

		/// <summary>
		/// Event raised when the cloud filesystem contents have been updated from the server and a new filesystem snapshot is available.
		/// </summary>
		public event EventHandler FilesystemChanged;

		/// <summary>
		/// Event raised when the contact list has been updated from the server and a new contact list snapshot is available.
		/// </summary>
		public event EventHandler ContactListChanged;

		public MegaClient(string email, string password)
		{
			Argument.ValidateIsNotNullOrWhitespace(email, "email");
			Argument.ValidateIsNotNullOrWhitespace(password, "password");

			if (!email.Contains("@"))
				throw new ArgumentException("That does not look like an e-mail address.", "email");

			_clientInstanceID = OpaqueID.Random(Constants.ClientInstanceIdSizeInBytes);
			AccountEmail = email;
			_passwordKey = Algorithms.DeriveAesKey(password);
		}

		public void Dispose()
		{
			DestroySession();
		}

		/// <summary>
		/// Ensures that the client is connected. This is done automatically as needed
		/// but you can use it manually to verify that the login credentials are valid.
		/// </summary>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		public async Task EnsureConnectedAsync(IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.LogMethodCall("EnsureConnectedAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			using (await AcquireLock(feedbackChannel, cancellationToken))
			{
				await EnsureConnectedInternalAsync(feedbackChannel, cancellationToken);
			}
		}

		/// <summary>
		/// Adds a contact to your contact list. If the account is not registered with Mega, an invitation email is sent.
		/// The invited account will not appear in your contact list until it is registered.
		/// </summary>
		/// <param name="email">Email of the account to add to the contact list.</param>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		public async Task AddContactAsync(string email, IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Argument.ValidateIsNotNullOrWhitespace(email, "email");

			if (!email.Contains("@"))
				throw new ArgumentException("That does not look like an e-mail address.", "email");

			PatternHelper.LogMethodCall("AddContactAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			using (await AcquireLock(feedbackChannel, cancellationToken))
			{
				await EnsureConnectedInternalAsync(feedbackChannel, cancellationToken);

				await ExecuteCommandInternalAsync<Account>(feedbackChannel, cancellationToken, new SetContactStatusCommand
				{
					AccountIDOrEmail = email,
					ClientInstanceID = _clientInstanceID,
					Status = KnownContactStatuses.InContactList
				});

				InvalidateContactListInternal();
			}
		}

		#region Implementation details
		#region Key management
		/// <summary>
		/// Password key of the current account.
		/// </summary>
		private readonly byte[] _passwordKey;

		/// <summary>
		/// Private key of the current account.
		/// </summary>
		private Algorithms.RsaPrivateKey _privateKey;

		/// <summary>
		/// Map of source ID to AES key. These are user and share keys, used to encrypt/decrypt item keys.
		/// </summary>
		private readonly IDictionary<OpaqueID, byte[]> _aesKeys = new Dictionary<OpaqueID, byte[]>();

		/// <summary>
		/// Map of source ID to RSA public key. These are used to send AES keys to other users.
		/// </summary>
		private readonly IDictionary<OpaqueID, Algorithms.RsaPublicKey> _publicKeys = new Dictionary<OpaqueID, Algorithms.RsaPublicKey>();

		/// <summary>
		/// Gets the AES key of the current account.
		/// </summary>
		internal byte[] AesKey
		{
			get
			{
				byte[] masterKey;
				if (_aesKeys.TryGetValue(AccountID, out masterKey))
					return masterKey;

				throw new InvalidOperationException("The account's master key has not been loaded yet.");
			}
		}

		/// <summary>
		/// Gets the public key of the referenced account, requesting it from Mega if necessary.
		/// </summary>
		/// <param name="accountID">The account whose public key to get.</param>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		internal async Task<Algorithms.RsaPublicKey> GetAccountPublicKeyInternalAsync(OpaqueID accountID, IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.LogMethodCall("GetAccountPublicKeyInternalAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			Algorithms.RsaPublicKey key;
			if (_publicKeys.TryGetValue(accountID, out key))
				return key;

			GetAccountPublicKeyResult contactKey;

			using (var keyLoadFeedback = feedbackChannel.BeginSubOperation("Requesting public key"))
				contactKey = await ExecuteCommandInternalAsync<GetAccountPublicKeyResult>(keyLoadFeedback, cancellationToken, new GetAccountPublicKeyCommand
				{
					AccountID = accountID
				});

			key = Algorithms.MpiArrayBytesToRsaPublicKey(contactKey.PublicKeyMpi);

			_publicKeys[accountID] = key;

			return key;
		}
		#endregion

		#region Account list management
		/// <summary>
		/// Map of email to account ID for all known accounts, including the current one.
		/// </summary>
		private readonly IDictionary<string, OpaqueID> _accountIDs = new Dictionary<string, OpaqueID>();

		/// <summary>
		/// Gets the ID of an account, referenced by email. The account must be in your contact list.
		/// </summary>
		/// <param name="email">Email of the target account.</param>
		/// <param name="feedbackChannel">Allows you to receive feedback about the operation while it is running.</param>
		/// <param name="cancellationToken">Allows you to cancel the operation.</param>
		internal async Task<OpaqueID> GetAccountIDInternalAsync(string email, IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.LogMethodCall("GetAccountIDInternalAsync", feedbackChannel, cancellationToken);
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			await EnsureFilesystemAndContactlistAvailableInternalAsync(feedbackChannel, cancellationToken);

			OpaqueID id;
			if (_accountIDs.TryGetValue(email, out id))
				return id;

			throw new ArgumentException("The referenced account is not in the contact list.", "email");
		}
		#endregion

		/// <summary>
		/// Unique ID of this Mega client instance. This is used to determine where some change originated from.
		/// The point is, we can ignore change notifications from the server if the change notification carries our own ID.
		/// </summary>
		internal OpaqueID _clientInstanceID;

		/// <summary>
		/// The channel used for Mega I/O. A different channel is used for each session that the client spans.
		/// </summary>
		private Channel _channel;

		/// <summary>
		/// Finds and decrypts an item key that is encrypted with an available master key.
		/// If none of the item keys use a master key we can access, throws an exception.
		/// </summary>
		/// <param name="itemKeys">Set of candidate item keys, encrypted with a master (AES or RSA) key of an account or share.</param>
		internal byte[] DecryptItemKey(IReadOnlyCollection<EncryptedItemKey> itemKeys)
		{
			Argument.ValidateIsNotNull(itemKeys, "itemKeys");

			foreach (var candidateKey in itemKeys)
			{
				// First, determine if we need to AES-decrypt of RSA-decrypt.
				if (candidateKey.EncryptedKey.Bytes.Length == 16 || candidateKey.EncryptedKey.Bytes.Length == 32)
				{
					// AES.
					byte[] aesKey;
					if (!_aesKeys.TryGetValue(candidateKey.SourceID, out aesKey))
						continue;

					return Algorithms.AesDecryptKey(candidateKey.EncryptedKey, aesKey);
				}
				else if (candidateKey.EncryptedKey.Bytes.Length == 256)
				{
					// RSA.
					if (candidateKey.SourceID != AccountID)
						continue; // TODO: Is this a real scenario to handle?

					return Algorithms.RsaDecrypt(candidateKey.EncryptedKey, _privateKey);
				}
				else
				{
					// Unknown type of key.
					continue;
				}
			}

			throw new UnusableItemException("We do not have a key for this item.");
		}

		#region Connection and session management
		/// <summary>
		/// Executes a set of commands on the Mega channel. This function automatically wraps special
		/// situations such as reconnecting when the session expires and retrying on temporary failure.
		/// Always use this method instead of directly accessing the Channel.
		/// </summary>
		internal async Task<TResult> ExecuteCommandInternalAsync<TResult>(IFeedbackChannel feedbackChannel, CancellationToken cancellationToken, object command)
		{
			var results = await ExecuteCommandsInternalAsync(feedbackChannel, cancellationToken, command);
			return results.Single().ToObject<TResult>();
		}

		/// <summary>
		/// Executes a set of commands on the Mega channel. This function automatically wraps special
		/// situations such as reconnecting when the session expires and retrying on temporary failure.
		/// Always use this method instead of directly accessing the Channel.
		/// </summary>
		internal Task<JArray> ExecuteCommandsInternalAsync(IFeedbackChannel feedbackChannel, CancellationToken cancellationToken, params object[] commands)
		{
			var retryPolicy = new RetryPolicy(RetryPolicy.ThirdPartyFaultExceptions.Concat(new[]
			{
				typeof(SessionExpiredException)
			}).ToArray(), RetryPolicy.FastAndAggressiveIntervals);

			using (var apiCallFeedbackChannel = feedbackChannel.BeginSubOperation("Communicating with Mega API"))
			{
				return RetryHelper.ExecuteWithRetryAsync(async delegate
				{
					await EnsureConnectedInternalAsync(feedbackChannel, cancellationToken);

					try
					{
						return await _channel.CreateAndExecuteTransactionAsync(cancellationToken, commands);
					}
					catch (SessionExpiredException)
					{
						DestroySession();

						throw;
					}
				}, retryPolicy, apiCallFeedbackChannel, cancellationToken);
			}
		}

		/// <summary>
		/// Forgets everything about the current session. A new session will be opened for the next operation.
		/// </summary>
		private void DestroySession()
		{
			if (_channel == null)
				return;

			Debug.WriteLine("DestroySession() called.");

			_currentFilesystemSnapshot = null;

			_channel.IncomingNotification -= OnIncomingNotification;
			_channel.Dispose();
			_channel = null;

			// We do not know the current state of the filesystem so it might have changed.
			Task.Run(delegate
			{
				#region Raise FilesystemChanged(this, EventArgs.Empty)
				{
					var eventHandler = FilesystemChanged;
					if (eventHandler != null)
						eventHandler(this, EventArgs.Empty);
				}
				#endregion
			});
		}

		private async Task EnsureConnectedInternalAsync(IFeedbackChannel feedbackChannel, CancellationToken cancellationToken)
		{
			if (_channel != null)
				return;

			var channel = new Channel();

			using (var connecting = feedbackChannel.BeginSubOperation("Establishing Mega session"))
			{
				connecting.Status = "Authenticating";

				var emailHash = Algorithms.Stringhash(AccountEmail.ToLowerInvariant(), _passwordKey);

				var sessionInfo = (await channel.CreateAndExecuteTransactionAsync(cancellationToken, new OpenUserSessionCommand
				{
					Email = AccountEmail,
					EmailHash = emailHash
				})).Single().ToObject<OpenUserSessionResult>();

				// Now do the cryptography we need to get the session ID and load the profile.
				connecting.Status = "Loading user profile";

				var aesKey = Algorithms.AesDecryptKey(sessionInfo.AesKey, _passwordKey);

				var privateKeyComponents = Algorithms.AesDecryptKey(sessionInfo.PrivateKeyComponents, aesKey);
				_privateKey = Algorithms.MpiArrayBytesToRsaPrivateKey(privateKeyComponents);

				var sessionIDEncrypted = Algorithms.MpiToBytes(sessionInfo.SessionIDData);
				var sessionIDRaw = Algorithms.RsaDecrypt(sessionIDEncrypted, _privateKey);

				// First 43 bytes is the session ID. There is more data but it seems to not be used.
				channel.SessionID = sessionIDRaw.Take(43).ToArray();

				// Now load the user profile.
				var profile = (await channel.CreateAndExecuteTransactionAsync(cancellationToken, new GetUserProfileCommand()))
					.Single().ToObject<GetUserProfileResult>();

				AccountID = profile.UserID;
				_aesKeys.Clear();
				_publicKeys.Clear();

				_publicKeys[AccountID] = Algorithms.MpiArrayBytesToRsaPublicKey(profile.PublicKeyComponents);
				_aesKeys[AccountID] = aesKey;
			}

			channel.IncomingNotification += OnIncomingNotification;
			_channel = channel;
		}
		#endregion

		#region Server-to-client notification handling
		private void OnIncomingNotification(object sender, IncomingNotificationEventArgs e)
		{
			string notificationName = e.Command.Value<string>("a");

			if (notificationName == ItemAddedNotification.NotificationNameConst
				|| notificationName == ItemDeletedNotification.NotificationNameConst)
			{
				e.Handled = true;

				using (AcquireLock().GetAwaiter().GetResult())
					InvalidateFilesystemInternal();
			}
			else if (notificationName == AccountUpdatedNotification.NotificationNameConst)
			{
				e.Handled = true;

				using (AcquireLock().GetAwaiter().GetResult())
					InvalidateContactListInternal();
			}
		}
		#endregion

		#region Filesystem management and operations
		internal void InvalidateFilesystemInternal()
		{
			if (_currentFilesystemSnapshot == null)
				return;

			// Just reset the whole thing. Only raise FilesystemChanged once after each snapshot is acuired.
			_currentFilesystemSnapshot = null;

			Task.Run(delegate
			{
				#region Raise FilesystemChanged(this, EventArgs.Empty)
				{
					var eventHandler = FilesystemChanged;
					if (eventHandler != null)
						eventHandler(this, EventArgs.Empty);
				}
				#endregion
			});
		}

		internal void InvalidateContactListInternal()
		{
			if (_currentContactListSnapshot == null)
				return;

			// Just reset the whole thing. Only raise ContactListChanged once after the list is acuired.
			_currentContactListSnapshot = null;

			Task.Run(delegate
			{
				#region Raise ContactListChanged(this, EventArgs.Empty)
				{
					var eventHandler = ContactListChanged;
					if (eventHandler != null)
						eventHandler(this, EventArgs.Empty);
				}
				#endregion
			});
		}

		private FilesystemSnapshot _currentFilesystemSnapshot;
		private ImmutableHashSet<Contact> _currentContactListSnapshot;

		internal async Task EnsureFilesystemAndContactlistAvailableInternalAsync(IFeedbackChannel feedbackChannel, CancellationToken cancellationToken)
		{
			if (_currentFilesystemSnapshot != null && _currentContactListSnapshot != null)
				return;

			using (var loadingFilesystem = feedbackChannel.BeginSubOperation("Loading account data"))
			{
				loadingFilesystem.Status = "Loading encrypted filesystem";

				var filesystem = await ExecuteCommandInternalAsync<GetItemsResult>(loadingFilesystem, cancellationToken, new GetItemsCommand
				{
					C = 1
				});

				// This enables the channel to start change tracking.
				if (_channel.IncomingSequenceReference == null)
					_channel.IncomingSequenceReference = filesystem.IncomingSequenceReference;

				loadingFilesystem.Status = "Loading share keys";

				foreach (var item in filesystem.Items
					.Where(i => i.ShareKey.HasValue))
				{
					// Share keys are either RSA-encrypted or AES-encrypted.

					if (item.ShareKey.Value.Bytes.Length == 258)
					{
						// RSA.
						var shareKey = Algorithms.MpiToBytes(item.ShareKey.Value);
						var shareAesKey = Algorithms.RsaDecrypt(shareKey, _privateKey);
						_aesKeys[item.ID] = shareAesKey.Take(16).ToArray();
					}
					else if (item.ShareKey.Value.Bytes.Length == 16)
					{
						// AES
						_aesKeys[item.ID] = Algorithms.AesDecryptKey(item.ShareKey.Value, AesKey);
					}
					else
					{
						// ???
						loadingFilesystem.WriteWarning("Unable to determine share key type. ID_{0}, length {1} parent ID_{2}", item.ID, item.ShareKey.Value.Bytes.Length, item.ParentID);
						continue;
					}

					loadingFilesystem.WriteVerbose("Got share key for {0}.", item.ID);
				}

				loadingFilesystem.Status = "Decrypting filesystem";

				var snapshot = CreateFilesystemSnapshot(filesystem, loadingFilesystem, cancellationToken);

				feedbackChannel.Status = "Performing integrity check";

				CheckSnapshotIntegrity(snapshot, loadingFilesystem, cancellationToken);

				feedbackChannel.Status = "Loading contact list";

				// We only keep accounts that are marked as contacts.
				_currentContactListSnapshot = filesystem.KnownAccounts
					.Where(a => a.AccountType == KnownAccountTypes.InContactList)
					.Select(a => Contact.FromTemplate(a, this))
					.ToImmutableHashSet();

				_currentFilesystemSnapshot = snapshot;

				foreach (var item in snapshot.AllItems)
				{
					loadingFilesystem.WriteVerbose("Got {0}, ID_{1}, {2}", item.Name, item.ID, item.Type);
				}
			}
		}

		private FilesystemSnapshot CreateFilesystemSnapshot(GetItemsResult filesystem, IFeedbackChannel feedbackChannel, CancellationToken cancellationToken)
		{
			var snapshot = new FilesystemSnapshot(this);

			long processedItems = 0;

			foreach (var nodeInfo in filesystem.Items)
			{
				try
				{
					snapshot.AddItem(nodeInfo);
				}
				catch (Exception ex)
				{
					// Ignore any items that fail to materialize.
					feedbackChannel.WriteWarning(ex.Message);
				}

				feedbackChannel.Progress = ++processedItems * 1.0 / filesystem.Items.Length;
				cancellationToken.ThrowIfCancellationRequested();
			}

			return snapshot;
		}

		private void CheckSnapshotIntegrity(FilesystemSnapshot snapshot, IFeedbackChannel feedbackChannel, CancellationToken cancellationToken)
		{
			foreach (var orphan in snapshot._orphans)
				feedbackChannel.WriteWarning("Found orphaned item: {0} (ID {1})", orphan.Name, orphan.ID);

			if (snapshot.Files == null)
				throw new ContractException("Mega did not provide us with a Files folder.");

			if (snapshot.Inbox == null)
				throw new ContractException("Mega did not provide us with an Inbox folder.");

			if (snapshot.Trash == null)
				throw new ContractException("Mega did not provide us with a Trash folder.");
		}
		#endregion

		#region Locking and synchronization
		private readonly SemaphoreSlim _syncSemaphore = new SemaphoreSlim(1);

		internal async Task<IDisposable> AcquireLock(IFeedbackChannel feedbackChannel = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			PatternHelper.EnsureFeedbackChannel(ref feedbackChannel);

			// First try - if lock is free, we just grab it immediately.
			var firstAttempt = SemaphoreLock.TryTake(_syncSemaphore, TimeSpan.Zero);

			if (firstAttempt != null)
				return firstAttempt;

			feedbackChannel.Status = "Waiting for pending operations to complete";

			// Wait in a loop, checking for cancellation every now and then.
			while (true)
			{
				var lockHolder = await SemaphoreLock.TryTakeAsync(_syncSemaphore, TimeSpan.FromSeconds(0.1));

				if (lockHolder != null)
					return lockHolder;

				cancellationToken.ThrowIfCancellationRequested();
			}
		}
		#endregion

		#endregion
	}
}