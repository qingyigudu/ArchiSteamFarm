//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2020 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.NLog;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace ArchiSteamFarm {
	public sealed class Bot : IDisposable {
		internal const ushort CallbackSleep = 500; // In milliseconds
		internal const ushort MaxMessagePrefixLength = MaxMessageLength - ReservedMessageLength - 2; // 2 for a minimum of 2 characters (escape one and real one)
		internal const byte MinPlayingBlockedTTL = 60; // Delay in seconds added when account was occupied during our disconnect, to not disconnect other Steam client session too soon

		private const char DefaultBackgroundKeysRedeemerSeparator = '\t';
		private const byte LoginCooldownInMinutes = 25; // Captcha disappears after around 20 minutes, so we make it 25
		private const uint LoginID = 1242; // This must be the same for all ASF bots and all ASF processes
		private const ushort MaxMessageLength = 5000; // This is a limitation enforced by Steam
		private const byte MaxTwoFactorCodeFailures = WebBrowser.MaxTries; // Max TwoFactorCodeMismatch failures in a row before we determine that our 2FA credentials are invalid (because Steam wrongly returns those, of course)
		private const byte RedeemCooldownInHours = 1; // 1 hour since first redeem attempt, this is a limitation enforced by Steam
		private const byte ReservedMessageLength = 2; // 2 for 2x optional …

		[PublicAPI]
		public static IReadOnlyDictionary<string, Bot> BotsReadOnly => Bots;

		internal static ConcurrentDictionary<string, Bot> Bots { get; private set; }
		internal static StringComparer BotsComparer { get; private set; }
		internal static EOSType OSType { get; private set; } = EOSType.Unknown;

		private static readonly SemaphoreSlim BotsSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim LoginRateLimitingSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim LoginSemaphore = new SemaphoreSlim(1, 1);

		[JsonIgnore]
		[PublicAPI]
		public readonly Actions Actions;

		[JsonIgnore]
		[PublicAPI]
		public readonly ArchiLogger ArchiLogger;

		[JsonIgnore]
		[PublicAPI]
		public readonly ArchiWebHandler ArchiWebHandler;

		[JsonProperty]
		[PublicAPI]
		public readonly string BotName;

		[JsonProperty]
		[PublicAPI]
		public readonly CardsFarmer CardsFarmer;

		[JsonIgnore]
		[PublicAPI]
		public readonly Commands Commands;

		[JsonIgnore]
		[PublicAPI]
		public readonly SteamConfiguration SteamConfiguration;

		[JsonProperty]
		[PublicAPI]
		public uint GamesToRedeemInBackgroundCount => BotDatabase?.GamesToRedeemInBackgroundCount ?? 0;

		[JsonProperty]
		[PublicAPI]
		public bool IsConnectedAndLoggedOn => SteamClient?.SteamID != null;

		[JsonProperty]
		[PublicAPI]
		public bool IsPlayingPossible => !PlayingBlocked && !LibraryLocked;

		internal readonly ArchiHandler ArchiHandler;
		internal readonly BotDatabase BotDatabase;

		internal readonly ConcurrentDictionary<uint, (EPaymentMethod PaymentMethod, DateTime TimeCreated)> OwnedPackageIDs = new ConcurrentDictionary<uint, (EPaymentMethod PaymentMethod, DateTime TimeCreated)>();
		internal readonly SteamApps SteamApps;
		internal readonly SteamFriends SteamFriends;

		internal bool CanReceiveSteamCards => !IsAccountLimited && !IsAccountLocked;
		internal bool HasMobileAuthenticator => BotDatabase?.MobileAuthenticator != null;
		internal bool IsAccountLimited => AccountFlags.HasFlag(EAccountFlags.LimitedUser) || AccountFlags.HasFlag(EAccountFlags.LimitedUserForce);
		internal bool IsAccountLocked => AccountFlags.HasFlag(EAccountFlags.Lockdown);

		private readonly CallbackManager CallbackManager;
		private readonly SemaphoreSlim CallbackSemaphore = new SemaphoreSlim(1, 1);
		private readonly SemaphoreSlim GamesRedeemerInBackgroundSemaphore = new SemaphoreSlim(1, 1);
		private readonly Timer HeartBeatTimer;
		private readonly SemaphoreSlim InitializationSemaphore = new SemaphoreSlim(1, 1);
		private readonly SemaphoreSlim MessagingSemaphore = new SemaphoreSlim(1, 1);
		private readonly ConcurrentDictionary<ArchiHandler.UserNotificationsCallback.EUserNotification, uint> PastNotifications = new ConcurrentDictionary<ArchiHandler.UserNotificationsCallback.EUserNotification, uint>();
		private readonly SemaphoreSlim PICSSemaphore = new SemaphoreSlim(1, 1);
		private readonly Statistics Statistics;
		private readonly SteamClient SteamClient;
		private readonly ConcurrentHashSet<ulong> SteamFamilySharingIDs = new ConcurrentHashSet<ulong>();
		private readonly SteamUser SteamUser;
		private readonly Trading Trading;

		private IEnumerable<(string FilePath, EFileType FileType)> RelatedFiles {
			get {
				foreach (EFileType fileType in Enum.GetValues(typeof(EFileType))) {
					string filePath = GetFilePath(fileType);

					if (string.IsNullOrEmpty(filePath)) {
						ArchiLogger.LogNullError(nameof(filePath));

						yield break;
					}

					yield return (filePath, fileType);
				}
			}
		}

#pragma warning disable IDE0051
		[JsonProperty(PropertyName = SharedInfo.UlongCompatibilityStringPrefix + nameof(SteamID))]
		[JetBrains.Annotations.NotNull]
		private string SSteamID => SteamID.ToString();
#pragma warning restore IDE0051

		[JsonProperty]
		public EAccountFlags AccountFlags { get; private set; }

		[JsonProperty]
		public BotConfig BotConfig { get; private set; }

		[JsonProperty]
		public bool KeepRunning { get; private set; }

		[JsonProperty]
		public string Nickname { get; private set; }

		[PublicAPI]
		public ulong SteamID { get; private set; }

		[PublicAPI]
		public long WalletBalance { get; private set; }

		[PublicAPI]
		public ECurrencyCode WalletCurrency { get; private set; }

		internal bool PlayingBlocked { get; private set; }
		internal bool PlayingWasBlocked { get; private set; }

		private string AuthCode;

#pragma warning disable IDE0052
		[JsonProperty]
		private string AvatarHash;
#pragma warning restore IDE0052

		private Timer ConnectionFailureTimer;
		private string DeviceID;
		private bool FirstTradeSent;
		private Timer GamesRedeemerInBackgroundTimer;
		private byte HeartBeatFailures;
		private EResult LastLogOnResult;
		private DateTime LastLogonSessionReplaced;
		private bool LibraryLocked;
		private ulong MasterChatGroupID;
		private Timer PlayingWasBlockedTimer;
		private bool ReconnectOnUserInitiated;
		private Timer SendItemsTimer;
		private bool SteamParentalActive = true;
		private SteamSaleEvent SteamSaleEvent;
		private string TwoFactorCode;
		private byte TwoFactorCodeFailures;

		private Bot([JetBrains.Annotations.NotNull] string botName, [JetBrains.Annotations.NotNull] BotConfig botConfig, [JetBrains.Annotations.NotNull] BotDatabase botDatabase) {
			if (string.IsNullOrEmpty(botName) || (botConfig == null) || (botDatabase == null)) {
				throw new ArgumentNullException(nameof(botName) + " || " + nameof(botConfig) + " || " + nameof(botDatabase));
			}

			BotName = botName;
			BotConfig = botConfig;
			BotDatabase = botDatabase;

			ArchiLogger = new ArchiLogger(botName);

			if (HasMobileAuthenticator) {
				BotDatabase.MobileAuthenticator.Init(this);
			}

			ArchiWebHandler = new ArchiWebHandler(this);

			SteamConfiguration = SteamConfiguration.Create(builder => builder.WithProtocolTypes(ASF.GlobalConfig.SteamProtocols).WithCellID(ASF.GlobalDatabase.CellID).WithServerListProvider(ASF.GlobalDatabase.ServerListProvider).WithHttpClientFactory(ArchiWebHandler.GenerateDisposableHttpClient));

			// Initialize
			SteamClient = new SteamClient(SteamConfiguration);

			if (Debugging.IsUserDebugging && Directory.Exists(SharedInfo.DebugDirectory)) {
				string debugListenerPath = Path.Combine(SharedInfo.DebugDirectory, botName);

				try {
					Directory.CreateDirectory(debugListenerPath);
					SteamClient.DebugNetworkListener = new NetHookNetworkListener(debugListenerPath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
				}
			}

			SteamUnifiedMessages steamUnifiedMessages = SteamClient.GetHandler<SteamUnifiedMessages>();

			ArchiHandler = new ArchiHandler(ArchiLogger, steamUnifiedMessages);
			SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamApps = SteamClient.GetHandler<SteamApps>();
			CallbackManager.Subscribe<SteamApps.GuestPassListCallback>(OnGuestPassList);
			CallbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

			SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);

			CallbackManager.Subscribe<SteamUnifiedMessages.ServiceMethodNotification>(OnServiceMethod);

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
			CallbackManager.Subscribe<SteamUser.WalletInfoCallback>(OnWalletUpdate);

			CallbackManager.Subscribe<ArchiHandler.PlayingSessionStateCallback>(OnPlayingSessionState);
			CallbackManager.Subscribe<ArchiHandler.SharedLibraryLockStatusCallback>(OnSharedLibraryLockStatus);
			CallbackManager.Subscribe<ArchiHandler.UserNotificationsCallback>(OnUserNotifications);
			CallbackManager.Subscribe<ArchiHandler.VanityURLChangedCallback>(OnVanityURLChangedCallback);

			Actions = new Actions(this);
			CardsFarmer = new CardsFarmer(this);
			Commands = new Commands(this);
			Trading = new Trading(this);

			if (!Debugging.IsDebugBuild && ASF.GlobalConfig.Statistics) {
				Statistics = new Statistics(this);
			}

			HeartBeatTimer = new Timer(
				async e => await HeartBeat().ConfigureAwait(false),
				null,
				TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(ASF.LoadBalancingDelay * Bots.Count), // Delay
				TimeSpan.FromMinutes(1) // Period
			);
		}

		public void Dispose() {
			// Those are objects that are always being created if constructor doesn't throw exception
			Actions.Dispose();
			CallbackSemaphore.Dispose();
			GamesRedeemerInBackgroundSemaphore.Dispose();
			InitializationSemaphore.Dispose();
			MessagingSemaphore.Dispose();
			PICSSemaphore.Dispose();

			// Those are objects that might be null and the check should be in-place
			ArchiWebHandler?.Dispose();
			BotDatabase?.Dispose();
			CardsFarmer?.Dispose();
			ConnectionFailureTimer?.Dispose();
			GamesRedeemerInBackgroundTimer?.Dispose();
			HeartBeatTimer?.Dispose();
			PlayingWasBlockedTimer?.Dispose();
			SendItemsTimer?.Dispose();
			Statistics?.Dispose();
			SteamSaleEvent?.Dispose();
			Trading?.Dispose();
		}

		[PublicAPI]
		public static Bot GetBot(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				ASF.ArchiLogger.LogNullError(nameof(botName));

				return null;
			}

			if (Bots.TryGetValue(botName, out Bot targetBot)) {
				return targetBot;
			}

			if (!ulong.TryParse(botName, out ulong steamID) || (steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				return null;
			}

			return Bots.Values.FirstOrDefault(bot => bot.SteamID == steamID);
		}

		[PublicAPI]
		public static HashSet<Bot> GetBots(string args) {
			if (string.IsNullOrEmpty(args)) {
				ASF.ArchiLogger.LogNullError(nameof(args));

				return null;
			}

			string[] botNames = args.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

			HashSet<Bot> result = new HashSet<Bot>();

			foreach (string botName in botNames) {
				if (botName.Equals(SharedInfo.ASF, StringComparison.OrdinalIgnoreCase)) {
					IEnumerable<Bot> allBots = Bots.OrderBy(bot => bot.Key, BotsComparer).Select(bot => bot.Value);
					result.UnionWith(allBots);

					return result;
				}

				if (botName.Contains("..")) {
					string[] botRange = botName.Split(new[] { ".." }, StringSplitOptions.RemoveEmptyEntries);

					if (botRange.Length == 2) {
						Bot firstBot = GetBot(botRange[0]);

						if (firstBot != null) {
							Bot lastBot = GetBot(botRange[1]);

							if (lastBot != null) {
								foreach (Bot bot in Bots.OrderBy(bot => bot.Key, BotsComparer).Select(bot => bot.Value).SkipWhile(bot => bot != firstBot)) {
									result.Add(bot);

									if (bot == lastBot) {
										break;
									}
								}

								continue;
							}
						}
					}
				}

				if (botName.StartsWith("r!", StringComparison.OrdinalIgnoreCase)) {
					string botsPattern = botName.Substring(2);

					RegexOptions botsRegex = RegexOptions.None;

					if ((BotsComparer == StringComparer.InvariantCulture) || (BotsComparer == StringComparer.Ordinal)) {
						botsRegex |= RegexOptions.CultureInvariant;
					} else if ((BotsComparer == StringComparer.InvariantCultureIgnoreCase) || (BotsComparer == StringComparer.OrdinalIgnoreCase)) {
						botsRegex |= RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
					}

					Regex regex;

					try {
						regex = new Regex(botsPattern, botsRegex);
					} catch (ArgumentException e) {
						ASF.ArchiLogger.LogGenericWarningException(e);

						return null;
					}

					IEnumerable<Bot> regexMatches = Bots.Where(kvp => regex.IsMatch(kvp.Key)).Select(kvp => kvp.Value);
					result.UnionWith(regexMatches);

					continue;
				}

				Bot singleBot = GetBot(botName);

				if (singleBot == null) {
					continue;
				}

				result.Add(singleBot);
			}

			return result;
		}

		[PublicAPI]
		public async Task<byte?> GetTradeHoldDuration(ulong steamID, ulong tradeID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || (tradeID == 0)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(tradeID));

				return null;
			}

			if (SteamFriends.GetFriendRelationship(steamID) == EFriendRelationship.Friend) {
				byte? tradeHoldDurationForUser = await ArchiWebHandler.GetTradeHoldDurationForUser(steamID).ConfigureAwait(false);

				if (tradeHoldDurationForUser.HasValue) {
					return tradeHoldDurationForUser;
				}
			}

			Bot targetBot = Bots.Values.FirstOrDefault(bot => bot.SteamID == steamID);

			if (targetBot?.IsConnectedAndLoggedOn == true) {
				string targetTradeToken = await targetBot.ArchiHandler.GetTradeToken().ConfigureAwait(false);

				if (!string.IsNullOrEmpty(targetTradeToken)) {
					byte? tradeHoldDurationForUser = await ArchiWebHandler.GetTradeHoldDurationForUser(steamID, targetTradeToken).ConfigureAwait(false);

					if (tradeHoldDurationForUser.HasValue) {
						return tradeHoldDurationForUser;
					}
				}
			}

			return await ArchiWebHandler.GetTradeHoldDurationForTrade(tradeID).ConfigureAwait(false);
		}

		[PublicAPI]
		public bool HasPermission(ulong steamID, BotConfig.EPermission permission) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || (permission == BotConfig.EPermission.None)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(permission));

				return false;
			}

			if (ASF.IsOwner(steamID)) {
				return true;
			}

			return permission switch {
				BotConfig.EPermission.FamilySharing when SteamFamilySharingIDs.Contains(steamID) => true,
				_ => BotConfig.SteamUserPermissions.TryGetValue(steamID, out BotConfig.EPermission realPermission) && (realPermission >= permission),
			};
		}

		[PublicAPI]
		public void SetUserInput(ASF.EUserInputType inputType, string inputValue) {
			if ((inputType == ASF.EUserInputType.Unknown) || !Enum.IsDefined(typeof(ASF.EUserInputType), inputType) || string.IsNullOrEmpty(inputValue)) {
				ArchiLogger.LogNullError(nameof(inputType) + " || " + nameof(inputValue));

				return;
			}

			// This switch should cover ONLY bot properties
			switch (inputType) {
				case ASF.EUserInputType.DeviceID:
					DeviceID = inputValue;

					break;
				case ASF.EUserInputType.Login:
					if (BotConfig != null) {
						BotConfig.SteamLogin = inputValue;
					}

					break;
				case ASF.EUserInputType.Password:
					if (BotConfig != null) {
						BotConfig.DecryptedSteamPassword = inputValue;
					}

					break;
				case ASF.EUserInputType.SteamGuard:
					AuthCode = inputValue;

					break;
				case ASF.EUserInputType.SteamParentalCode:
					if (BotConfig != null) {
						BotConfig.SteamParentalCode = inputValue;
					}

					break;
				case ASF.EUserInputType.TwoFactorAuthentication:
					TwoFactorCode = inputValue;

					break;
				default:
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(inputType), inputType));

					break;
			}
		}

		internal void AddGamesToRedeemInBackground(IOrderedDictionary gamesToRedeemInBackground) {
			if ((gamesToRedeemInBackground == null) || (gamesToRedeemInBackground.Count == 0)) {
				ArchiLogger.LogNullError(nameof(gamesToRedeemInBackground));

				return;
			}

			BotDatabase.AddGamesToRedeemInBackground(gamesToRedeemInBackground);

			if ((GamesRedeemerInBackgroundTimer == null) && BotDatabase.HasGamesToRedeemInBackground && IsConnectedAndLoggedOn) {
				Utilities.InBackground(RedeemGamesInBackground);
			}
		}

		internal async Task<bool> DeleteAllRelatedFiles() {
			await BotDatabase.MakeReadOnly().ConfigureAwait(false);

			foreach (string filePath in RelatedFiles.Select(file => file.FilePath).Where(File.Exists)) {
				try {
					File.Delete(filePath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);

					return false;
				}
			}

			return true;
		}

		internal bool DeleteRedeemedKeysFiles() {
			string unusedKeysFilePath = GetFilePath(EFileType.KeysToRedeemUnused);

			if (string.IsNullOrEmpty(unusedKeysFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(unusedKeysFilePath));

				return false;
			}

			if (File.Exists(unusedKeysFilePath)) {
				try {
					File.Delete(unusedKeysFilePath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);

					return false;
				}
			}

			string usedKeysFilePath = GetFilePath(EFileType.KeysToRedeemUsed);

			if (string.IsNullOrEmpty(usedKeysFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(usedKeysFilePath));

				return false;
			}

			if (File.Exists(usedKeysFilePath)) {
				try {
					File.Delete(usedKeysFilePath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);

					return false;
				}
			}

			return true;
		}

		internal static string FormatBotResponse(string response, string botName) {
			if (string.IsNullOrEmpty(response) || string.IsNullOrEmpty(botName)) {
				ASF.ArchiLogger.LogNullError(nameof(response) + " || " + nameof(botName));

				return null;
			}

			return Environment.NewLine + "<" + botName + "> " + response;
		}

		internal async Task<(uint PlayableAppID, DateTime IgnoredUntil, bool IgnoredGlobally)> GetAppDataForIdling(uint appID, float hoursPlayed, bool allowRecursiveDiscovery = true, bool optimisticDiscovery = true) {
			if ((appID == 0) || (hoursPlayed < 0)) {
				ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(hoursPlayed));

				return (0, DateTime.MaxValue, true);
			}

			HashSet<uint> packageIDs = ASF.GlobalDatabase.GetPackageIDs(appID, OwnedPackageIDs.Keys);

			if ((packageIDs == null) || (packageIDs.Count == 0)) {
				return (0, DateTime.MaxValue, true);
			}

			if ((hoursPlayed < CardsFarmer.HoursForRefund) && !BotConfig.IdleRefundableGames) {
				DateTime mostRecent = DateTime.MinValue;

				foreach (uint packageID in packageIDs) {
					if (!OwnedPackageIDs.TryGetValue(packageID, out (EPaymentMethod PaymentMethod, DateTime TimeCreated) packageData)) {
						continue;
					}

					if (IsRefundable(packageData.PaymentMethod) && (packageData.TimeCreated > mostRecent)) {
						mostRecent = packageData.TimeCreated;
					}
				}

				if (mostRecent > DateTime.MinValue) {
					DateTime playableIn = mostRecent.AddDays(CardsFarmer.DaysForRefund);

					if (playableIn > DateTime.UtcNow) {
						return (0, playableIn, false);
					}
				}
			}

			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet productInfoResultSet = null;

			for (byte i = 0; (i < WebBrowser.MaxTries) && (productInfoResultSet == null) && IsConnectedAndLoggedOn; i++) {
				await PICSSemaphore.WaitAsync().ConfigureAwait(false);

				try {
					productInfoResultSet = await SteamApps.PICSGetProductInfo(appID, null, false);
				} catch (Exception e) {
					ArchiLogger.LogGenericWarningException(e);
				} finally {
					PICSSemaphore.Release();
				}
			}

			if (productInfoResultSet == null) {
				return (optimisticDiscovery ? appID : 0, DateTime.MinValue, true);
			}

			foreach (Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> productInfoApps in productInfoResultSet.Results.Select(result => result.Apps)) {
				if (!productInfoApps.TryGetValue(appID, out SteamApps.PICSProductInfoCallback.PICSProductInfo productInfoApp)) {
					continue;
				}

				KeyValue productInfo = productInfoApp.KeyValues;

				if (productInfo == KeyValue.Invalid) {
					ArchiLogger.LogNullError(nameof(productInfo));

					break;
				}

				KeyValue commonProductInfo = productInfo["common"];

				if (commonProductInfo == KeyValue.Invalid) {
					continue;
				}

				string releaseState = commonProductInfo["ReleaseState"].Value;

				if (!string.IsNullOrEmpty(releaseState)) {
					// We must convert this to uppercase, since Valve doesn't stick to any convention and we can have a case mismatch
					switch (releaseState.ToUpperInvariant()) {
						case "RELEASED":
							break;
						case "PRELOADONLY":
						case "PRERELEASE":
							return (0, DateTime.MaxValue, true);
						default:
							ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(releaseState), releaseState));

							break;
					}
				}

				string type = commonProductInfo["type"].Value;

				if (string.IsNullOrEmpty(type)) {
					return (appID, DateTime.MinValue, true);
				}

				// We must convert this to uppercase, since Valve doesn't stick to any convention and we can have a case mismatch
				switch (type.ToUpperInvariant()) {
					case "APPLICATION":
					case "EPISODE":
					case "GAME":
					case "MOD":
					case "MOVIE":
					case "SERIES":
					case "TOOL":
					case "VIDEO":
						// Types that can be idled
						return (appID, DateTime.MinValue, true);
					case "ADVERTISING":
					case "DEMO":
					case "DLC":
					case "GUIDE":
					case "HARDWARE":
					case "MUSIC":
						// Types that can't be idled
						break;
					default:
						ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(type), type));

						break;
				}

				if (!allowRecursiveDiscovery) {
					return (0, DateTime.MinValue, true);
				}

				string listOfDlc = productInfo["extended"]["listofdlc"].Value;

				if (string.IsNullOrEmpty(listOfDlc)) {
					return (appID, DateTime.MinValue, true);
				}

				string[] dlcAppIDsTexts = listOfDlc.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

				foreach (string dlcAppIDsText in dlcAppIDsTexts) {
					if (!uint.TryParse(dlcAppIDsText, out uint dlcAppID) || (dlcAppID == 0)) {
						ArchiLogger.LogNullError(nameof(dlcAppID));

						break;
					}

					(uint playableAppID, _, _) = await GetAppDataForIdling(dlcAppID, hoursPlayed, false, false).ConfigureAwait(false);

					if (playableAppID != 0) {
						return (playableAppID, DateTime.MinValue, true);
					}
				}

				return (appID, DateTime.MinValue, true);
			}

			return ((productInfoResultSet.Complete && !productInfoResultSet.Failed) || optimisticDiscovery ? appID : 0, DateTime.MinValue, true);
		}

		internal static string GetFilePath(string botName, EFileType fileType) {
			if (string.IsNullOrEmpty(botName) || !Enum.IsDefined(typeof(EFileType), fileType)) {
				ASF.ArchiLogger.LogNullError(nameof(botName) + " || " + nameof(fileType));

				return null;
			}

			string botPath = Path.Combine(SharedInfo.ConfigDirectory, botName);

			switch (fileType) {
				case EFileType.Config:
					return botPath + SharedInfo.JsonConfigExtension;
				case EFileType.Database:
					return botPath + SharedInfo.DatabaseExtension;
				case EFileType.KeysToRedeem:
					return botPath + SharedInfo.KeysExtension;
				case EFileType.KeysToRedeemUnused:
					return botPath + SharedInfo.KeysExtension + SharedInfo.KeysUnusedExtension;
				case EFileType.KeysToRedeemUsed:
					return botPath + SharedInfo.KeysExtension + SharedInfo.KeysUsedExtension;
				case EFileType.MobileAuthenticator:
					return botPath + SharedInfo.MobileAuthenticatorExtension;
				case EFileType.SentryFile:
					return botPath + SharedInfo.SentryHashExtension;
				default:
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(fileType), fileType));

					return null;
			}
		}

		[ItemCanBeNull]
		internal async Task<HashSet<uint>> GetMarketableAppIDs() => await ArchiWebHandler.GetAppList().ConfigureAwait(false);

		[ItemCanBeNull]
		internal async Task<Dictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)>> GetPackagesData(IReadOnlyCollection<uint> packageIDs) {
			if ((packageIDs == null) || (packageIDs.Count == 0)) {
				ArchiLogger.LogNullError(nameof(packageIDs));

				return null;
			}

			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet productInfoResultSet = null;

			for (byte i = 0; (i < WebBrowser.MaxTries) && (productInfoResultSet == null) && IsConnectedAndLoggedOn; i++) {
				await PICSSemaphore.WaitAsync().ConfigureAwait(false);

				try {
					productInfoResultSet = await SteamApps.PICSGetProductInfo(Enumerable.Empty<uint>(), packageIDs);
				} catch (Exception e) {
					ArchiLogger.LogGenericWarningException(e);
				} finally {
					PICSSemaphore.Release();
				}
			}

			if (productInfoResultSet == null) {
				return null;
			}

			Dictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)> result = new Dictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)>();

			foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo in productInfoResultSet.Results.SelectMany(productInfoResult => productInfoResult.Packages).Where(productInfoPackages => productInfoPackages.Key != 0).Select(productInfoPackages => productInfoPackages.Value)) {
				if (productInfo.KeyValues == KeyValue.Invalid) {
					ArchiLogger.LogNullError(nameof(productInfo));

					return null;
				}

				(uint ChangeNumber, HashSet<uint> AppIDs) value = (productInfo.ChangeNumber, null);

				try {
					KeyValue appIDs = productInfo.KeyValues["appids"];

					if (appIDs == KeyValue.Invalid) {
						continue;
					}

					value.AppIDs = new HashSet<uint>();

					foreach (string appIDText in appIDs.Children.Select(app => app.Value)) {
						if (!uint.TryParse(appIDText, out uint appID) || (appID == 0)) {
							ArchiLogger.LogNullError(nameof(appID));

							return null;
						}

						value.AppIDs.Add(appID);
					}
				} finally {
					result[productInfo.ID] = value;
				}
			}

			return result;
		}

		internal async Task<(Dictionary<string, string> UnusedKeys, Dictionary<string, string> UsedKeys)> GetUsedAndUnusedKeys() {
			string unusedKeysFilePath = GetFilePath(EFileType.KeysToRedeemUnused);

			if (string.IsNullOrEmpty(unusedKeysFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(unusedKeysFilePath));

				return (null, null);
			}

			string usedKeysFilePath = GetFilePath(EFileType.KeysToRedeemUsed);

			if (string.IsNullOrEmpty(usedKeysFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(usedKeysFilePath));

				return (null, null);
			}

			string[] files = { unusedKeysFilePath, usedKeysFilePath };

			IList<Dictionary<string, string>> results = await Utilities.InParallel(files.Select(GetKeysFromFile)).ConfigureAwait(false);

			return (results[0], results[1]);
		}

		internal async Task IdleGame(CardsFarmer.Game game) {
			if (game == null) {
				ArchiLogger.LogNullError(nameof(game));

				return;
			}

			await ArchiHandler.PlayGames(game.PlayableAppID.ToEnumerable(), BotConfig.CustomGamePlayedWhileFarming).ConfigureAwait(false);
		}

		internal async Task IdleGames(IReadOnlyCollection<CardsFarmer.Game> games) {
			if ((games == null) || (games.Count == 0)) {
				ArchiLogger.LogNullError(nameof(games));

				return;
			}

			await ArchiHandler.PlayGames(games.Select(game => game.PlayableAppID), BotConfig.CustomGamePlayedWhileFarming).ConfigureAwait(false);
		}

		internal async Task ImportKeysToRedeem(string filePath) {
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
				ArchiLogger.LogNullError(nameof(filePath));

				return;
			}

			try {
				OrderedDictionary gamesToRedeemInBackground = new OrderedDictionary();

				using (StreamReader reader = new StreamReader(filePath)) {
					string line;

					while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
						if (line.Length == 0) {
							continue;
						}

						// Valid formats:
						// Key (name will be the same as key and replaced from redemption result, if possible)
						// Name + Key (user provides both, if name is equal to key, above logic is used, otherwise name is kept)
						// Name + <Ignored> + Key (BGR output format, we include extra properties in the middle, those are ignored during import)
						string[] parsedArgs = line.Split(DefaultBackgroundKeysRedeemerSeparator, StringSplitOptions.RemoveEmptyEntries);

						if (parsedArgs.Length < 1) {
							ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, line));

							continue;
						}

						string name = parsedArgs[0];
						string key = parsedArgs[parsedArgs.Length - 1];

						gamesToRedeemInBackground[key] = name;
					}
				}

				if (gamesToRedeemInBackground.Count > 0) {
					IOrderedDictionary validGamesToRedeemInBackground = ValidateGamesToRedeemInBackground(gamesToRedeemInBackground);

					if ((validGamesToRedeemInBackground != null) && (validGamesToRedeemInBackground.Count > 0)) {
						AddGamesToRedeemInBackground(validGamesToRedeemInBackground);
					}
				}

				File.Delete(filePath);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
			}
		}

		internal static void Init(StringComparer botsComparer) {
			if (botsComparer == null) {
				ASF.ArchiLogger.LogNullError(nameof(botsComparer));

				return;
			}

			if (Bots != null) {
				ASF.ArchiLogger.LogGenericError(Strings.WarningFailed);

				return;
			}

			BotsComparer = botsComparer;
			Bots = new ConcurrentDictionary<string, Bot>(botsComparer);
		}

		internal bool IsBlacklistedFromIdling(uint appID) {
			if (appID == 0) {
				ArchiLogger.LogNullError(nameof(appID));

				return false;
			}

			return BotDatabase.IsBlacklistedFromIdling(appID);
		}

		internal bool IsBlacklistedFromTrades(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			return BotDatabase.IsBlacklistedFromTrades(steamID);
		}

		internal bool IsPriorityIdling(uint appID) {
			if (appID == 0) {
				ArchiLogger.LogNullError(nameof(appID));

				return false;
			}

			return BotDatabase.IsPriorityIdling(appID);
		}

		internal async Task OnConfigChanged(bool deleted) {
			if (deleted) {
				await Destroy().ConfigureAwait(false);

				return;
			}

			string configFile = GetFilePath(EFileType.Config);

			if (string.IsNullOrEmpty(configFile)) {
				ArchiLogger.LogNullError(nameof(configFile));

				return;
			}

			BotConfig botConfig = await BotConfig.Load(configFile).ConfigureAwait(false);

			if (botConfig == null) {
				await Destroy().ConfigureAwait(false);

				return;
			}

			if (botConfig == BotConfig) {
				return;
			}

			await InitializationSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (botConfig == BotConfig) {
					return;
				}

				Stop(botConfig.Enabled);
				BotConfig = botConfig;

				await InitModules().ConfigureAwait(false);
				InitStart();
			} finally {
				InitializationSemaphore.Release();
			}
		}

		internal async Task OnFarmingFinished(bool farmedSomething) {
			await OnFarmingStopped().ConfigureAwait(false);

			if (BotConfig.SendOnFarmingFinished && (BotConfig.LootableTypes.Count > 0) && (farmedSomething || !FirstTradeSent)) {
				FirstTradeSent = true;

				await Actions.SendInventory(filterFunction: item => BotConfig.LootableTypes.Contains(item.Type)).ConfigureAwait(false);
			}

			if (BotConfig.ShutdownOnFarmingFinished) {
				Stop();
			}

			await PluginsCore.OnBotFarmingFinished(this, farmedSomething).ConfigureAwait(false);
		}

		internal async Task OnFarmingStopped() {
			await ResetGamesPlayed().ConfigureAwait(false);
			await PluginsCore.OnBotFarmingStopped(this).ConfigureAwait(false);
		}

		internal async Task<bool> RefreshSession() {
			if (!IsConnectedAndLoggedOn) {
				return false;
			}

			SteamUser.WebAPIUserNonceCallback callback;

			try {
				callback = await SteamUser.RequestWebAPIUserNonce();
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);
				await Connect(true).ConfigureAwait(false);

				return false;
			}

			if (string.IsNullOrEmpty(callback?.Nonce)) {
				await Connect(true).ConfigureAwait(false);

				return false;
			}

			if (await ArchiWebHandler.Init(SteamID, SteamClient.Universe, callback.Nonce, SteamParentalActive ? BotConfig.SteamParentalCode : null).ConfigureAwait(false)) {
				return true;
			}

			await Connect(true).ConfigureAwait(false);

			return false;
		}

		internal static async Task RegisterBot(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				ASF.ArchiLogger.LogNullError(nameof(botName));

				return;
			}

			if (Bots.ContainsKey(botName)) {
				return;
			}

			string configFilePath = GetFilePath(botName, EFileType.Config);

			if (string.IsNullOrEmpty(configFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(configFilePath));

				return;
			}

			BotConfig botConfig = await BotConfig.Load(configFilePath).ConfigureAwait(false);

			if (botConfig == null) {
				ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorBotConfigInvalid, configFilePath));

				return;
			}

			if (Debugging.IsDebugConfigured) {
				ASF.ArchiLogger.LogGenericDebug(configFilePath + ": " + JsonConvert.SerializeObject(botConfig, Formatting.Indented));
			}

			string databaseFilePath = GetFilePath(botName, EFileType.Database);

			if (string.IsNullOrEmpty(databaseFilePath)) {
				ASF.ArchiLogger.LogNullError(nameof(databaseFilePath));

				return;
			}

			BotDatabase botDatabase = await BotDatabase.CreateOrLoad(databaseFilePath).ConfigureAwait(false);

			if (botDatabase == null) {
				ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorDatabaseInvalid, databaseFilePath));

				return;
			}

			if (Debugging.IsDebugConfigured) {
				ASF.ArchiLogger.LogGenericDebug(databaseFilePath + ": " + JsonConvert.SerializeObject(botDatabase, Formatting.Indented));
			}

			Bot bot;

			await BotsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (Bots.ContainsKey(botName)) {
					return;
				}

				bot = new Bot(botName, botConfig, botDatabase);

				if (!Bots.TryAdd(botName, bot)) {
					ASF.ArchiLogger.LogNullError(nameof(bot));
					bot.Dispose();

					return;
				}
			} finally {
				BotsSemaphore.Release();
			}

			await PluginsCore.OnBotInit(bot).ConfigureAwait(false);

			HashSet<ClientMsgHandler> customHandlers = await PluginsCore.OnBotSteamHandlersInit(bot).ConfigureAwait(false);

			if ((customHandlers != null) && (customHandlers.Count > 0)) {
				foreach (ClientMsgHandler customHandler in customHandlers) {
					bot.SteamClient.AddHandler(customHandler);
				}
			}

			await PluginsCore.OnBotSteamCallbacksInit(bot, bot.CallbackManager).ConfigureAwait(false);

			await bot.InitModules().ConfigureAwait(false);

			bot.InitStart();
		}

		internal async Task<bool> Rename(string newBotName) {
			if (string.IsNullOrEmpty(newBotName)) {
				ArchiLogger.LogNullError(nameof(newBotName));

				return false;
			}

			if (newBotName.Equals(SharedInfo.ASF) || Bots.ContainsKey(newBotName)) {
				return false;
			}

			if (KeepRunning) {
				Stop(true);
			}

			await BotDatabase.MakeReadOnly().ConfigureAwait(false);

			// We handle the config file last as it'll trigger new bot creation
			foreach ((string filePath, EFileType fileType) in RelatedFiles.Where(file => File.Exists(file.FilePath)).OrderByDescending(file => file.FileType != EFileType.Config)) {
				string newFilePath = GetFilePath(newBotName, fileType);

				if (string.IsNullOrEmpty(newFilePath)) {
					ArchiLogger.LogNullError(nameof(newFilePath));

					return false;
				}

				try {
					File.Move(filePath, newFilePath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);

					return false;
				}
			}

			return true;
		}

		internal void RequestPersonaStateUpdate() {
			if (!IsConnectedAndLoggedOn) {
				return;
			}

			SteamFriends.RequestFriendInfo(SteamID, EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence);
		}

		internal async Task<bool> SendMessage(ulong steamID, string message) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));

				return false;
			}

			if (!IsConnectedAndLoggedOn) {
				return false;
			}

			ArchiLogger.LogChatMessage(true, message, steamID: steamID);

			ushort maxMessageLength = (ushort) (MaxMessageLength - ReservedMessageLength - (ASF.GlobalConfig.SteamMessagePrefix?.Length ?? 0));

			// We must escape our message prior to sending it
			message = Escape(message);

			int i = 0;

			while (i < message.Length) {
				int partLength;
				bool copyNewline = false;

				// ReSharper disable ArrangeMissingParentheses - conflict with Roslyn
				if (message.Length - i > maxMessageLength) {
					int lastNewLine = message.LastIndexOf(Environment.NewLine, i + maxMessageLength - Environment.NewLine.Length, maxMessageLength - Environment.NewLine.Length, StringComparison.Ordinal);

					if (lastNewLine > i) {
						partLength = lastNewLine - i + Environment.NewLine.Length;
						copyNewline = true;
					} else {
						partLength = maxMessageLength;
					}
				} else {
					partLength = message.Length - i;
				}

				// If our message is of max length and ends with a single '\' then we can't split it here, it escapes the next character
				if ((partLength >= maxMessageLength) && (message[i + partLength - 1] == '\\') && (message[i + partLength - 2] != '\\')) {
					// Instead, we'll cut this message one char short and include the rest in next iteration
					partLength--;
				}

				// ReSharper restore ArrangeMissingParentheses
				string messagePart = message.Substring(i, partLength);

				messagePart = ASF.GlobalConfig.SteamMessagePrefix + (i > 0 ? "…" : "") + messagePart + (maxMessageLength < message.Length - i ? "…" : "");

				await MessagingSemaphore.WaitAsync().ConfigureAwait(false);

				try {
					bool sent = false;

					for (byte j = 0; (j < WebBrowser.MaxTries) && !sent && IsConnectedAndLoggedOn; j++) {
						// TODO: Determine if this dirty workaround fixes "ghost notification" bug
						// Theory: Perhaps Steam is confused when dealing with more than 1 message per second from the same user, check if this helps
						await Task.Delay(1000).ConfigureAwait(false);

						EResult result = await ArchiHandler.SendMessage(steamID, messagePart).ConfigureAwait(false);

						switch (result) {
							case EResult.Fail:
							case EResult.RateLimitExceeded:
							case EResult.Timeout:
								await Task.Delay(5000).ConfigureAwait(false);

								continue;
							case EResult.OK:
								sent = true;

								break;
							default:
								ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(result), result));

								return false;
						}
					}

					if (!sent) {
						ArchiLogger.LogGenericWarning(Strings.WarningFailed);

						return false;
					}
				} finally {
					MessagingSemaphore.Release();
				}

				i += partLength - (copyNewline ? Environment.NewLine.Length : 0);
			}

			return true;
		}

		internal async Task<bool> SendMessage(ulong chatGroupID, ulong chatID, string message) {
			if ((chatGroupID == 0) || (chatID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(chatGroupID) + " || " + nameof(chatID) + " || " + nameof(message));

				return false;
			}

			if (!IsConnectedAndLoggedOn) {
				return false;
			}

			ArchiLogger.LogChatMessage(true, message, chatGroupID, chatID);

			ushort maxMessageLength = (ushort) (MaxMessageLength - ReservedMessageLength - (ASF.GlobalConfig.SteamMessagePrefix?.Length ?? 0));

			// We must escape our message prior to sending it
			message = Escape(message);

			int i = 0;

			// ReSharper disable ArrangeMissingParentheses - conflict with Roslyn
			while (i < message.Length) {
				int partLength;
				bool copyNewline = false;

				if (message.Length - i > maxMessageLength) {
					int lastNewLine = message.LastIndexOf(Environment.NewLine, i + maxMessageLength - Environment.NewLine.Length, maxMessageLength - Environment.NewLine.Length, StringComparison.Ordinal);

					if (lastNewLine > i) {
						partLength = lastNewLine - i + Environment.NewLine.Length;
						copyNewline = true;
					} else {
						partLength = maxMessageLength;
					}
				} else {
					partLength = message.Length - i;
				}

				// If our message is of max length and ends with a single '\' then we can't split it here, it escapes the next character
				if ((partLength >= maxMessageLength) && (message[i + partLength - 1] == '\\') && (message[i + partLength - 2] != '\\')) {
					// Instead, we'll cut this message one char short and include the rest in next iteration
					partLength--;
				}

				// ReSharper restore ArrangeMissingParentheses
				string messagePart = message.Substring(i, partLength);

				messagePart = ASF.GlobalConfig.SteamMessagePrefix + (i > 0 ? "…" : "") + messagePart + (maxMessageLength < message.Length - i ? "…" : "");

				await MessagingSemaphore.WaitAsync().ConfigureAwait(false);

				try {
					bool sent = false;

					for (byte j = 0; (j < WebBrowser.MaxTries) && !sent && IsConnectedAndLoggedOn; j++) {
						EResult result = await ArchiHandler.SendMessage(chatGroupID, chatID, messagePart).ConfigureAwait(false);

						switch (result) {
							case EResult.Fail:
							case EResult.RateLimitExceeded:
							case EResult.Timeout:
								await Task.Delay(5000).ConfigureAwait(false);

								continue;
							case EResult.OK:
								sent = true;

								break;
							default:
								ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(result), result));

								return false;
						}
					}

					if (!sent) {
						ArchiLogger.LogGenericWarning(Strings.WarningFailed);

						return false;
					}
				} finally {
					MessagingSemaphore.Release();
				}

				i += partLength - (copyNewline ? Environment.NewLine.Length : 0);
			}

			return true;
		}

		internal async Task<bool> SendTypingMessage(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			if (!IsConnectedAndLoggedOn) {
				return false;
			}

			return await ArchiHandler.SendTypingStatus(steamID).ConfigureAwait(false) == EResult.OK;
		}

		internal async Task Start() {
			if (KeepRunning) {
				return;
			}

			KeepRunning = true;
			Utilities.InBackground(HandleCallbacks, true);
			ArchiLogger.LogGenericInfo(Strings.Starting);

			// Support and convert 2FA files
			if (!HasMobileAuthenticator) {
				string mobileAuthenticatorFilePath = GetFilePath(EFileType.MobileAuthenticator);

				if (string.IsNullOrEmpty(mobileAuthenticatorFilePath)) {
					ArchiLogger.LogNullError(nameof(mobileAuthenticatorFilePath));

					return;
				}

				if (File.Exists(mobileAuthenticatorFilePath)) {
					await ImportAuthenticator(mobileAuthenticatorFilePath).ConfigureAwait(false);
				}
			}

			string keysToRedeemFilePath = GetFilePath(EFileType.KeysToRedeem);

			if (string.IsNullOrEmpty(keysToRedeemFilePath)) {
				ArchiLogger.LogNullError(nameof(keysToRedeemFilePath));

				return;
			}

			if (File.Exists(keysToRedeemFilePath)) {
				await ImportKeysToRedeem(keysToRedeemFilePath).ConfigureAwait(false);
			}

			await Connect().ConfigureAwait(false);
		}

		internal void Stop(bool skipShutdownEvent = false) {
			if (!KeepRunning) {
				return;
			}

			KeepRunning = false;
			ArchiLogger.LogGenericInfo(Strings.BotStopping);

			if (SteamClient.IsConnected) {
				Disconnect();
			}

			if (!skipShutdownEvent) {
				Utilities.InBackground(Events.OnBotShutdown);
			}
		}

		internal static IOrderedDictionary ValidateGamesToRedeemInBackground(IOrderedDictionary gamesToRedeemInBackground) {
			if ((gamesToRedeemInBackground == null) || (gamesToRedeemInBackground.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(gamesToRedeemInBackground));

				return null;
			}

			HashSet<object> invalidKeys = new HashSet<object>();

			foreach (DictionaryEntry game in gamesToRedeemInBackground) {
				bool invalid = false;

				string key = game.Key as string;

				if (string.IsNullOrEmpty(key)) {
					invalid = true;
					ASF.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, nameof(key)));
				} else if (!Utilities.IsValidCdKey(key)) {
					invalid = true;
					ASF.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, key));
				}

				string name = game.Value as string;

				if (string.IsNullOrEmpty(name)) {
					invalid = true;
					ASF.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, nameof(name)));
				}

				if (invalid) {
					invalidKeys.Add(game.Key);
				}
			}

			if (invalidKeys.Count > 0) {
				foreach (string invalidKey in invalidKeys) {
					gamesToRedeemInBackground.Remove(invalidKey);
				}
			}

			return gamesToRedeemInBackground;
		}

		private async Task CheckOccupationStatus() {
			StopPlayingWasBlockedTimer();

			if (!IsPlayingPossible) {
				PlayingWasBlocked = true;
				ArchiLogger.LogGenericInfo(Strings.BotAccountOccupied);

				return;
			}

			if (PlayingWasBlocked && (PlayingWasBlockedTimer == null)) {
				InitPlayingWasBlockedTimer();
			}

			ArchiLogger.LogGenericInfo(Strings.BotAccountFree);

			if (!await CardsFarmer.Resume(false).ConfigureAwait(false)) {
				await ResetGamesPlayed().ConfigureAwait(false);
			}
		}

		private async Task Connect(bool force = false) {
			if (!force && (!KeepRunning || SteamClient.IsConnected)) {
				return;
			}

			await LimitLoginRequestsAsync().ConfigureAwait(false);

			if (!force && (!KeepRunning || SteamClient.IsConnected)) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotConnecting);
			InitConnectionFailureTimer();
			SteamClient.Connect();
		}

		private async Task Destroy(bool force = false) {
			if (KeepRunning) {
				if (!force) {
					Stop();
				} else {
					// Stop() will most likely block due to connection freeze, don't wait for it
					Utilities.InBackground(() => Stop());
				}
			}

			Bots.TryRemove(BotName, out _);
			await PluginsCore.OnBotDestroy(this).ConfigureAwait(false);
		}

		private void Disconnect() {
			StopConnectionFailureTimer();
			SteamClient.Disconnect();
		}

		private static string Escape(string message) {
			if (string.IsNullOrEmpty(message)) {
				ASF.ArchiLogger.LogNullError(nameof(message));

				return null;
			}

			return message.Replace("\\", "\\\\").Replace("[", "\\[");
		}

		private string GetFilePath(EFileType fileType) {
			if (!Enum.IsDefined(typeof(EFileType), fileType)) {
				ASF.ArchiLogger.LogNullError(nameof(fileType));

				return null;
			}

			return GetFilePath(BotName, fileType);
		}

		[ItemCanBeNull]
		private async Task<Dictionary<string, string>> GetKeysFromFile(string filePath) {
			if (string.IsNullOrEmpty(filePath)) {
				ArchiLogger.LogNullError(nameof(filePath));

				return null;
			}

			if (!File.Exists(filePath)) {
				return new Dictionary<string, string>(0, StringComparer.Ordinal);
			}

			Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);

			try {
				using StreamReader reader = new StreamReader(filePath);

				string line;

				while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
					if (line.Length == 0) {
						continue;
					}

					string[] parsedArgs = line.Split(DefaultBackgroundKeysRedeemerSeparator, StringSplitOptions.RemoveEmptyEntries);

					if (parsedArgs.Length < 3) {
						ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, line));

						continue;
					}

					string key = parsedArgs[parsedArgs.Length - 1];

					if (!Utilities.IsValidCdKey(key)) {
						ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorIsInvalid, key));

						continue;
					}

					string name = parsedArgs[0];
					keys[key] = name;
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);

				return null;
			}

			return keys;
		}

		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);

			while (KeepRunning || SteamClient.IsConnected) {
				if (!CallbackSemaphore.Wait(0)) {
					if (Debugging.IsUserDebugging) {
						ArchiLogger.LogGenericDebug(string.Format(Strings.WarningFailedWithError, nameof(CallbackSemaphore)));
					}

					return;
				}

				try {
					CallbackManager.RunWaitAllCallbacks(timeSpan);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
				} finally {
					CallbackSemaphore.Release();
				}
			}
		}

		private async Task HeartBeat() {
			if (!KeepRunning || !IsConnectedAndLoggedOn || (HeartBeatFailures == byte.MaxValue)) {
				return;
			}

			try {
				if (DateTime.UtcNow.Subtract(ArchiHandler.LastPacketReceived).TotalSeconds > ASF.GlobalConfig.ConnectionTimeout) {
					await SteamFriends.RequestProfileInfo(SteamID);
				}

				HeartBeatFailures = 0;

				if (Statistics != null) {
					Utilities.InBackground(Statistics.OnHeartBeat);
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericDebuggingException(e);

				if (!KeepRunning || !IsConnectedAndLoggedOn || (HeartBeatFailures == byte.MaxValue)) {
					return;
				}

				if (++HeartBeatFailures >= (byte) Math.Ceiling(ASF.GlobalConfig.ConnectionTimeout / 10.0)) {
					HeartBeatFailures = byte.MaxValue;
					ArchiLogger.LogGenericWarning(Strings.BotConnectionLost);
					Utilities.InBackground(() => Connect(true));
				}
			}
		}

		private async Task ImportAuthenticator(string maFilePath) {
			if (HasMobileAuthenticator || !File.Exists(maFilePath)) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotAuthenticatorConverting);

			try {
				string json = await RuntimeCompatibility.File.ReadAllTextAsync(maFilePath).ConfigureAwait(false);

				if (string.IsNullOrEmpty(json)) {
					ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsEmpty, nameof(json)));

					return;
				}

				MobileAuthenticator authenticator = JsonConvert.DeserializeObject<MobileAuthenticator>(json);

				if (authenticator == null) {
					ArchiLogger.LogNullError(nameof(authenticator));

					return;
				}

				if (!authenticator.HasValidDeviceID) {
					ArchiLogger.LogGenericWarning(Strings.BotAuthenticatorInvalidDeviceID);

					if (string.IsNullOrEmpty(DeviceID)) {
						string deviceID = await Logging.GetUserInput(ASF.EUserInputType.DeviceID, BotName).ConfigureAwait(false);

						if (string.IsNullOrEmpty(deviceID)) {
							return;
						}

						SetUserInput(ASF.EUserInputType.DeviceID, deviceID);
					}

					if (!MobileAuthenticator.IsValidDeviceID(DeviceID)) {
						ArchiLogger.LogGenericWarning(Strings.BotAuthenticatorInvalidDeviceID);

						return;
					}

					authenticator.CorrectDeviceID(DeviceID);
				}

				authenticator.Init(this);
				BotDatabase.MobileAuthenticator = authenticator;

				File.Delete(maFilePath);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);

				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotAuthenticatorImportFinished);
		}

		private void InitConnectionFailureTimer() {
			if (ConnectionFailureTimer != null) {
				return;
			}

			ConnectionFailureTimer = new Timer(
				async e => await InitPermanentConnectionFailure().ConfigureAwait(false),
				null,
				TimeSpan.FromMinutes(Math.Ceiling(ASF.GlobalConfig.ConnectionTimeout / 30.0)), // Delay
				Timeout.InfiniteTimeSpan // Period
			);
		}

		private async Task InitializeFamilySharing() {
			HashSet<ulong> steamIDs = await ArchiWebHandler.GetFamilySharingSteamIDs().ConfigureAwait(false);

			if (steamIDs == null) {
				return;
			}

			SteamFamilySharingIDs.ReplaceWith(steamIDs);
		}

		private async Task<bool> InitLoginAndPassword(bool requiresPassword) {
			if (string.IsNullOrEmpty(BotConfig.SteamLogin)) {
				string steamLogin = await Logging.GetUserInput(ASF.EUserInputType.Login, BotName).ConfigureAwait(false);

				if (string.IsNullOrEmpty(steamLogin)) {
					return false;
				}

				SetUserInput(ASF.EUserInputType.Login, steamLogin);
			}

			if (requiresPassword && string.IsNullOrEmpty(BotConfig.DecryptedSteamPassword)) {
				string steamPassword = await Logging.GetUserInput(ASF.EUserInputType.Password, BotName).ConfigureAwait(false);

				if (string.IsNullOrEmpty(steamPassword)) {
					return false;
				}

				SetUserInput(ASF.EUserInputType.Password, steamPassword);
			}

			return true;
		}

		private async Task InitModules() {
			AccountFlags = EAccountFlags.NormalUser;
			AvatarHash = Nickname = null;
			MasterChatGroupID = 0;
			WalletBalance = 0;
			WalletCurrency = ECurrencyCode.Invalid;

			CardsFarmer.SetInitialState(BotConfig.Paused);

			if (SendItemsTimer != null) {
				SendItemsTimer.Dispose();
				SendItemsTimer = null;
			}

			if ((BotConfig.SendTradePeriod > 0) && (BotConfig.LootableTypes.Count > 0) && BotConfig.SteamUserPermissions.Values.Any(permission => permission >= BotConfig.EPermission.Master)) {
				SendItemsTimer = new Timer(
					async e => await Actions.SendInventory(filterFunction: item => BotConfig.LootableTypes.Contains(item.Type)).ConfigureAwait(false),
					null,
					TimeSpan.FromHours(BotConfig.SendTradePeriod) + TimeSpan.FromSeconds(ASF.LoadBalancingDelay * Bots.Count), // Delay
					TimeSpan.FromHours(BotConfig.SendTradePeriod) // Period
				);
			}

			if (SteamSaleEvent != null) {
				SteamSaleEvent.Dispose();
				SteamSaleEvent = null;
			}

			if (BotConfig.AutoSteamSaleEvent) {
				SteamSaleEvent = new SteamSaleEvent(this);
			}

			await PluginsCore.OnBotInitModules(this, BotConfig.AdditionalProperties).ConfigureAwait(false);
		}

		private async Task InitPermanentConnectionFailure() {
			if (!KeepRunning) {
				return;
			}

			ArchiLogger.LogGenericWarning(Strings.BotHeartBeatFailed);
			await Destroy(true).ConfigureAwait(false);
			await RegisterBot(BotName).ConfigureAwait(false);
		}

		private void InitPlayingWasBlockedTimer() {
			if (PlayingWasBlockedTimer != null) {
				return;
			}

			PlayingWasBlockedTimer = new Timer(
				e => ResetPlayingWasBlockedWithTimer(),
				null,
				TimeSpan.FromSeconds(MinPlayingBlockedTTL), // Delay
				Timeout.InfiniteTimeSpan // Period
			);
		}

		private void InitStart() {
			if (!BotConfig.Enabled) {
				ArchiLogger.LogGenericInfo(Strings.BotInstanceNotStartingBecauseDisabled);

				return;
			}

			// Start
			Utilities.InBackground(Start);
		}

		private bool IsMasterClanID(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsClanAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			return steamID == BotConfig.SteamMasterClanID;
		}

		private static bool IsRefundable(EPaymentMethod paymentMethod) {
			if (paymentMethod == EPaymentMethod.None) {
				ASF.ArchiLogger.LogNullError(nameof(paymentMethod));

				return false;
			}

			// Complimentary is also a flag
			return paymentMethod switch {
				EPaymentMethod.ActivationCode => false,
				EPaymentMethod.Complimentary => false,
				EPaymentMethod.GuestPass => false,
				EPaymentMethod.HardwarePromo => false,
				_ => !paymentMethod.HasFlag(EPaymentMethod.Complimentary)
			};
		}

		private async Task JoinMasterChatGroupID() {
			if (BotConfig.SteamMasterClanID == 0) {
				return;
			}

			if (MasterChatGroupID == 0) {
				ulong chatGroupID = await ArchiHandler.GetClanChatGroupID(BotConfig.SteamMasterClanID).ConfigureAwait(false);

				if (chatGroupID == 0) {
					return;
				}

				MasterChatGroupID = chatGroupID;
			}

			HashSet<ulong> chatGroupIDs = await ArchiHandler.GetMyChatGroupIDs().ConfigureAwait(false);

			if (chatGroupIDs?.Contains(MasterChatGroupID) != false) {
				return;
			}

			if (!await ArchiHandler.JoinChatRoomGroup(MasterChatGroupID).ConfigureAwait(false)) {
				ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiHandler.JoinChatRoomGroup)));
			}
		}

		private static async Task LimitLoginRequestsAsync() {
			if (ASF.GlobalConfig.LoginLimiterDelay == 0) {
				await LoginRateLimitingSemaphore.WaitAsync().ConfigureAwait(false);
				LoginRateLimitingSemaphore.Release();

				return;
			}

			await LoginSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				await LoginRateLimitingSemaphore.WaitAsync().ConfigureAwait(false);
				LoginRateLimitingSemaphore.Release();
			} finally {
				Utilities.InBackground(
					async () => {
						await Task.Delay(ASF.GlobalConfig.LoginLimiterDelay * 1000).ConfigureAwait(false);
						LoginSemaphore.Release();
					}
				);
			}
		}

		private async void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			HeartBeatFailures = 0;
			ReconnectOnUserInitiated = false;
			StopConnectionFailureTimer();

			ArchiLogger.LogGenericInfo(Strings.BotConnected);

			if (!KeepRunning) {
				ArchiLogger.LogGenericInfo(Strings.BotDisconnecting);
				Disconnect();

				return;
			}

			string sentryFilePath = GetFilePath(EFileType.SentryFile);

			if (string.IsNullOrEmpty(sentryFilePath)) {
				ArchiLogger.LogNullError(nameof(sentryFilePath));

				return;
			}

			byte[] sentryFileHash = null;

			if (File.Exists(sentryFilePath)) {
				try {
					byte[] sentryFileContent = await RuntimeCompatibility.File.ReadAllBytesAsync(sentryFilePath).ConfigureAwait(false);
					sentryFileHash = CryptoHelper.SHAHash(sentryFileContent);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);

					try {
						File.Delete(sentryFilePath);
					} catch {
						// Ignored, we can only try to delete faulted file at best
					}
				}
			}

			string loginKey = null;

			if (BotConfig.UseLoginKeys) {
				// Login keys are not guaranteed to be valid, we should use them only if we don't have full details available from the user
				if (string.IsNullOrEmpty(BotConfig.DecryptedSteamPassword) || (string.IsNullOrEmpty(AuthCode) && string.IsNullOrEmpty(TwoFactorCode) && !HasMobileAuthenticator)) {
					loginKey = BotDatabase.LoginKey;

					// Decrypt login key if needed
					if (!string.IsNullOrEmpty(loginKey) && (loginKey.Length > 19) && (BotConfig.PasswordFormat != ArchiCryptoHelper.ECryptoMethod.PlainText)) {
						loginKey = ArchiCryptoHelper.Decrypt(BotConfig.PasswordFormat, loginKey);
					}
				}
			} else {
				// If we're not using login keys, ensure we don't have any saved
				BotDatabase.LoginKey = null;
			}

			if (!await InitLoginAndPassword(string.IsNullOrEmpty(loginKey)).ConfigureAwait(false)) {
				Stop();

				return;
			}

			// Steam login and password fields can contain ASCII characters only, including spaces
			const string nonAsciiPattern = @"[^\u0000-\u007F]+";

			string username = Regex.Replace(BotConfig.SteamLogin, nonAsciiPattern, "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
			string password = BotConfig.DecryptedSteamPassword;

			if (!string.IsNullOrEmpty(password)) {
				password = Regex.Replace(password, nonAsciiPattern, "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
			}

			ArchiLogger.LogGenericInfo(Strings.BotLoggingIn);

			if (string.IsNullOrEmpty(TwoFactorCode) && HasMobileAuthenticator) {
				// We should always include 2FA token, even if it's not required
				TwoFactorCode = await BotDatabase.MobileAuthenticator.GenerateToken().ConfigureAwait(false);
			}

			InitConnectionFailureTimer();

			SteamUser.LogOnDetails logOnDetails = new SteamUser.LogOnDetails {
				AuthCode = AuthCode,
				CellID = ASF.GlobalDatabase.CellID,
				LoginID = LoginID,
				LoginKey = loginKey,
				Password = password,
				SentryFileHash = sentryFileHash,
				ShouldRememberPassword = BotConfig.UseLoginKeys,
				TwoFactorCode = TwoFactorCode,
				Username = username
			};

			if (OSType == EOSType.Unknown) {
				OSType = logOnDetails.ClientOSType;
			}

			SteamUser.LogOn(logOnDetails);
		}

		private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			EResult lastLogOnResult = LastLogOnResult;
			LastLogOnResult = EResult.Invalid;
			HeartBeatFailures = 0;
			SteamParentalActive = true;
			StopConnectionFailureTimer();
			StopPlayingWasBlockedTimer();

			ArchiLogger.LogGenericInfo(Strings.BotDisconnected);

			OwnedPackageIDs.Clear();
			PastNotifications.Clear();

			Actions.OnDisconnected();
			ArchiWebHandler.OnDisconnected();
			CardsFarmer.OnDisconnected();
			Trading.OnDisconnected();

			FirstTradeSent = false;

			await PluginsCore.OnBotDisconnected(this, callback.UserInitiated ? EResult.OK : lastLogOnResult).ConfigureAwait(false);

			// If we initiated disconnect, do not attempt to reconnect
			if (callback.UserInitiated && !ReconnectOnUserInitiated) {
				return;
			}

			switch (lastLogOnResult) {
				case EResult.AccountDisabled:
				case EResult.InvalidPassword when string.IsNullOrEmpty(BotDatabase.LoginKey):
					// Do not attempt to reconnect, those failures are permanent
					return;
				case EResult.InvalidPassword:
					BotDatabase.LoginKey = null;
					ArchiLogger.LogGenericInfo(Strings.BotRemovedExpiredLoginKey);

					break;
				case EResult.NoConnection:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
					await Task.Delay(5000).ConfigureAwait(false);

					break;
				case EResult.RateLimitExceeded:
					ArchiLogger.LogGenericInfo(string.Format(Strings.BotRateLimitExceeded, TimeSpan.FromMinutes(LoginCooldownInMinutes).ToHumanReadable()));

					if (!await LoginRateLimitingSemaphore.WaitAsync(1000 * WebBrowser.MaxTries).ConfigureAwait(false)) {
						break;
					}

					try {
						await Task.Delay(LoginCooldownInMinutes * 60 * 1000).ConfigureAwait(false);
					} finally {
						LoginRateLimitingSemaphore.Release();
					}

					break;
			}

			if (!KeepRunning || SteamClient.IsConnected) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotReconnecting);
			await Connect().ConfigureAwait(false);
		}

		private async void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback?.FriendList == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.FriendList));

				return;
			}

			foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList.Where(friend => friend.Relationship == EFriendRelationship.RequestRecipient)) {
				switch (friend.SteamID.AccountType) {
					case EAccountType.Clan when IsMasterClanID(friend.SteamID):
						ArchiHandler.AcknowledgeClanInvite(friend.SteamID, true);
						await JoinMasterChatGroupID().ConfigureAwait(false);

						break;
					case EAccountType.Clan:
						bool acceptGroupRequest = await PluginsCore.OnBotFriendRequest(this, friend.SteamID).ConfigureAwait(false);

						if (acceptGroupRequest) {
							ArchiHandler.AcknowledgeClanInvite(friend.SteamID, true);
							await JoinMasterChatGroupID().ConfigureAwait(false);

							break;
						}

						if (BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.RejectInvalidGroupInvites)) {
							ArchiHandler.AcknowledgeClanInvite(friend.SteamID, false);
						}

						break;
					default:
						if (HasPermission(friend.SteamID, BotConfig.EPermission.FamilySharing)) {
							if (!await ArchiHandler.AddFriend(friend.SteamID).ConfigureAwait(false)) {
								ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiHandler.AddFriend)));
							}

							break;
						}

						bool acceptFriendRequest = await PluginsCore.OnBotFriendRequest(this, friend.SteamID).ConfigureAwait(false);

						if (acceptFriendRequest) {
							if (!await ArchiHandler.AddFriend(friend.SteamID).ConfigureAwait(false)) {
								ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiHandler.AddFriend)));
							}

							break;
						}

						if (BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.RejectInvalidFriendInvites)) {
							if (!await ArchiHandler.RemoveFriend(friend.SteamID).ConfigureAwait(false)) {
								ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiHandler.RemoveFriend)));
							}
						}

						break;
				}
			}
		}

		private async void OnGuestPassList(SteamApps.GuestPassListCallback callback) {
			if (callback?.GuestPasses == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.GuestPasses));

				return;
			}

			if ((callback.CountGuestPassesToRedeem == 0) || (callback.GuestPasses.Count == 0) || !BotConfig.AcceptGifts) {
				return;
			}

			HashSet<ulong> guestPassIDs = callback.GuestPasses.Select(guestPass => guestPass["gid"].AsUnsignedLong()).Where(gid => gid != 0).ToHashSet();

			if (guestPassIDs.Count == 0) {
				return;
			}

			await Actions.AcceptGuestPasses(guestPassIDs).ConfigureAwait(false);
		}

		private async Task OnIncomingChatMessage(CChatRoom_IncomingChatMessage_Notification notification) {
			if (notification == null) {
				ArchiLogger.LogNullError(nameof(notification));

				return;
			}

			// Under normal circumstances, timestamp must always be greater than 0, but Steam already proved that it's capable of going against the logic
			if ((notification.steamid_sender != SteamID) && (notification.timestamp > 0)) {
				if (ShouldAckChatMessage(notification.steamid_sender)) {
					Utilities.InBackground(() => ArchiHandler.AckChatMessage(notification.chat_group_id, notification.chat_id, notification.timestamp));
				}
			}

			string message;

			// Prefer to use message without bbcode, but only if it's available
			if (!string.IsNullOrEmpty(notification.message_no_bbcode)) {
				message = notification.message_no_bbcode;
			} else if (!string.IsNullOrEmpty(notification.message)) {
				message = UnEscape(notification.message);
			} else {
				return;
			}

			ArchiLogger.LogChatMessage(false, message, notification.chat_group_id, notification.chat_id, notification.steamid_sender);

			// Steam network broadcasts chat events also when we don't explicitly sign into Steam community
			// We'll explicitly ignore those messages when using offline mode, as it was done in the first version of Steam chat when no messages were broadcasted at all before signing in
			// Handling messages will still work correctly in invisible mode, which is how it should work in the first place
			// This goes in addition to usual logic that ignores irrelevant messages from being parsed further
			if ((notification.chat_group_id != MasterChatGroupID) || (BotConfig.OnlineStatus == EPersonaState.Offline)) {
				return;
			}

			await Commands.HandleMessage(notification.chat_group_id, notification.chat_id, notification.steamid_sender, message).ConfigureAwait(false);
		}

		private async Task OnIncomingMessage(CFriendMessages_IncomingMessage_Notification notification) {
			if (notification == null) {
				ArchiLogger.LogNullError(nameof(notification));

				return;
			}

			if ((EChatEntryType) notification.chat_entry_type != EChatEntryType.ChatMsg) {
				return;
			}

			// Under normal circumstances, timestamp must always be greater than 0, but Steam already proved that it's capable of going against the logic
			if (!notification.local_echo && (notification.rtime32_server_timestamp > 0)) {
				if (ShouldAckChatMessage(notification.steamid_friend)) {
					Utilities.InBackground(() => ArchiHandler.AckMessage(notification.steamid_friend, notification.rtime32_server_timestamp));
				}
			}

			string message;

			// Prefer to use message without bbcode, but only if it's available
			if (!string.IsNullOrEmpty(notification.message_no_bbcode)) {
				message = notification.message_no_bbcode;
			} else if (!string.IsNullOrEmpty(notification.message)) {
				message = UnEscape(notification.message);
			} else {
				return;
			}

			ArchiLogger.LogChatMessage(notification.local_echo, message, steamID: notification.steamid_friend);

			// Steam network broadcasts chat events also when we don't explicitly sign into Steam community
			// We'll explicitly ignore those messages when using offline mode, as it was done in the first version of Steam chat when no messages were broadcasted at all before signing in
			// Handling messages will still work correctly in invisible mode, which is how it should work in the first place
			// This goes in addition to usual logic that ignores irrelevant messages from being parsed further
			if (notification.local_echo || (BotConfig.OnlineStatus == EPersonaState.Offline)) {
				return;
			}

			await Commands.HandleMessage(notification.steamid_friend, message).ConfigureAwait(false);
		}

		private async void OnLicenseList(SteamApps.LicenseListCallback callback) {
			if (callback?.LicenseList == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.LicenseList));

				return;
			}

			bool initialLogin = OwnedPackageIDs.Count == 0;

			Commands.OnNewLicenseList();
			OwnedPackageIDs.Clear();

			Dictionary<uint, uint> packagesToRefresh = new Dictionary<uint, uint>();

			foreach (SteamApps.LicenseListCallback.License license in callback.LicenseList.Where(license => license.PackageID != 0)) {
				OwnedPackageIDs[license.PackageID] = (license.PaymentMethod, license.TimeCreated);

				if (!ASF.GlobalDatabase.PackagesData.TryGetValue(license.PackageID, out (uint ChangeNumber, HashSet<uint> _) packageData) || (packageData.ChangeNumber < license.LastChangeNumber)) {
					packagesToRefresh[license.PackageID] = (uint) license.LastChangeNumber;
				}
			}

			if (packagesToRefresh.Count > 0) {
				ArchiLogger.LogGenericTrace(Strings.BotRefreshingPackagesData);
				await ASF.GlobalDatabase.RefreshPackages(this, packagesToRefresh).ConfigureAwait(false);
				ArchiLogger.LogGenericTrace(Strings.Done);
			}

			if (initialLogin && CardsFarmer.Paused) {
				// Emit initial game playing status in this case
				await ResetGamesPlayed().ConfigureAwait(false);
			}

			await CardsFarmer.OnNewGameAdded().ConfigureAwait(false);
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			LastLogOnResult = callback.Result;

			ArchiLogger.LogGenericInfo(string.Format(Strings.BotLoggedOff, callback.Result));

			switch (callback.Result) {
				case EResult.LoggedInElsewhere:
					// This result directly indicates that playing was blocked when we got (forcefully) disconnected
					PlayingWasBlocked = true;

					break;
				case EResult.LogonSessionReplaced:
					DateTime now = DateTime.UtcNow;

					if (now.Subtract(LastLogonSessionReplaced).TotalHours < 1) {
						ArchiLogger.LogGenericError(Strings.BotLogonSessionReplaced);
						Stop();

						return;
					}

					LastLogonSessionReplaced = now;

					break;
			}

			ReconnectOnUserInitiated = true;
			SteamClient.Disconnect();
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			// Always reset one-time-only access tokens when we get OnLoggedOn() response
			AuthCode = TwoFactorCode = null;

			// Keep LastLogOnResult for OnDisconnected()
			LastLogOnResult = callback.Result;

			HeartBeatFailures = 0;
			StopConnectionFailureTimer();

			switch (callback.Result) {
				case EResult.AccountDisabled:
				case EResult.InvalidPassword when string.IsNullOrEmpty(BotDatabase.LoginKey):
					// Those failures are permanent, we should Stop() the bot if any of those happen
					ArchiLogger.LogGenericWarning(string.Format(Strings.BotUnableToLogin, callback.Result, callback.ExtendedResult));
					Stop();

					break;
				case EResult.AccountLogonDenied:
					string authCode = await Logging.GetUserInput(ASF.EUserInputType.SteamGuard, BotName).ConfigureAwait(false);

					if (string.IsNullOrEmpty(authCode)) {
						Stop();

						break;
					}

					SetUserInput(ASF.EUserInputType.SteamGuard, authCode);

					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					if (!HasMobileAuthenticator) {
						string twoFactorCode = await Logging.GetUserInput(ASF.EUserInputType.TwoFactorAuthentication, BotName).ConfigureAwait(false);

						if (string.IsNullOrEmpty(twoFactorCode)) {
							Stop();

							break;
						}

						SetUserInput(ASF.EUserInputType.TwoFactorAuthentication, twoFactorCode);
					}

					break;
				case EResult.OK:
					AccountFlags = callback.AccountFlags;
					SteamID = callback.ClientSteamID;

					ArchiLogger.LogGenericInfo(string.Format(Strings.BotLoggedOn, SteamID + (!string.IsNullOrEmpty(callback.VanityURL) ? "/" + callback.VanityURL : "")));

					// Old status for these doesn't matter, we'll update them if needed
					TwoFactorCodeFailures = 0;
					LibraryLocked = PlayingBlocked = false;

					if (PlayingWasBlocked && (PlayingWasBlockedTimer == null)) {
						InitPlayingWasBlockedTimer();
					}

					if (IsAccountLimited) {
						ArchiLogger.LogGenericWarning(Strings.BotAccountLimited);
					}

					if (IsAccountLocked) {
						ArchiLogger.LogGenericWarning(Strings.BotAccountLocked);
					}

					if ((callback.CellID != 0) && (callback.CellID != ASF.GlobalDatabase.CellID)) {
						ASF.GlobalDatabase.CellID = callback.CellID;
					}

					// Handle steamID-based maFile
					if (!HasMobileAuthenticator) {
						string maFilePath = Path.Combine(SharedInfo.ConfigDirectory, SteamID + SharedInfo.MobileAuthenticatorExtension);

						if (File.Exists(maFilePath)) {
							await ImportAuthenticator(maFilePath).ConfigureAwait(false);
						}
					}

					if (callback.ParentalSettings != null) {
						(bool isSteamParentalEnabled, string steamParentalCode) = ValidateSteamParental(callback.ParentalSettings, BotConfig.SteamParentalCode);

						if (isSteamParentalEnabled) {
							SteamParentalActive = true;

							if (!string.IsNullOrEmpty(steamParentalCode)) {
								if (BotConfig.SteamParentalCode != steamParentalCode) {
									SetUserInput(ASF.EUserInputType.SteamParentalCode, steamParentalCode);
								}
							} else if (string.IsNullOrEmpty(BotConfig.SteamParentalCode) || (BotConfig.SteamParentalCode.Length != BotConfig.SteamParentalCodeLength)) {
								steamParentalCode = await Logging.GetUserInput(ASF.EUserInputType.SteamParentalCode, BotName).ConfigureAwait(false);

								if (string.IsNullOrEmpty(steamParentalCode) || (steamParentalCode.Length != BotConfig.SteamParentalCodeLength)) {
									Stop();

									break;
								}

								SetUserInput(ASF.EUserInputType.SteamParentalCode, steamParentalCode);
							}
						} else {
							SteamParentalActive = false;
						}
					} else if (SteamParentalActive && !string.IsNullOrEmpty(BotConfig.SteamParentalCode) && (BotConfig.SteamParentalCode.Length != BotConfig.SteamParentalCodeLength)) {
						string steamParentalCode = await Logging.GetUserInput(ASF.EUserInputType.SteamParentalCode, BotName).ConfigureAwait(false);

						if (string.IsNullOrEmpty(steamParentalCode) || (steamParentalCode.Length != BotConfig.SteamParentalCodeLength)) {
							Stop();

							break;
						}

						SetUserInput(ASF.EUserInputType.SteamParentalCode, steamParentalCode);
					}

					ArchiWebHandler.OnVanityURLChanged(callback.VanityURL);

					if (!await ArchiWebHandler.Init(SteamID, SteamClient.Universe, callback.WebAPIUserNonce, SteamParentalActive ? BotConfig.SteamParentalCode : null).ConfigureAwait(false)) {
						if (!await RefreshSession().ConfigureAwait(false)) {
							break;
						}
					}

					// Pre-fetch API key for future usage if possible
					Utilities.InBackground(ArchiWebHandler.HasValidApiKey);

					if ((GamesRedeemerInBackgroundTimer == null) && BotDatabase.HasGamesToRedeemInBackground) {
						Utilities.InBackground(RedeemGamesInBackground);
					}

					ArchiHandler.SetCurrentMode(2);
					ArchiHandler.RequestItemAnnouncements();

					// Sometimes Steam won't send us our own PersonaStateCallback, so request it explicitly
					RequestPersonaStateUpdate();

					Utilities.InBackground(InitializeFamilySharing);

					if (Statistics != null) {
						Utilities.InBackground(Statistics.OnLoggedOn);
					}

					if (BotConfig.OnlineStatus != EPersonaState.Offline) {
						SteamFriends.SetPersonaState(BotConfig.OnlineStatus);
					}

					if (BotConfig.SteamMasterClanID != 0) {
						Utilities.InBackground(
							async () => {
								if (!await ArchiWebHandler.JoinGroup(BotConfig.SteamMasterClanID).ConfigureAwait(false)) {
									ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, nameof(ArchiWebHandler.JoinGroup)));
								}

								await JoinMasterChatGroupID().ConfigureAwait(false);
							}
						);
					}

					await PluginsCore.OnBotLoggedOn(this).ConfigureAwait(false);

					break;
				case EResult.InvalidPassword:
				case EResult.NoConnection:
				case EResult.PasswordRequiredToKickSession: // Not sure about this one, it seems to be just generic "try again"? #694
				case EResult.RateLimitExceeded:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
				case EResult.TwoFactorCodeMismatch:
					ArchiLogger.LogGenericWarning(string.Format(Strings.BotUnableToLogin, callback.Result, callback.ExtendedResult));

					if ((callback.Result == EResult.TwoFactorCodeMismatch) && HasMobileAuthenticator) {
						if (++TwoFactorCodeFailures >= MaxTwoFactorCodeFailures) {
							TwoFactorCodeFailures = 0;
							ArchiLogger.LogGenericError(string.Format(Strings.BotInvalidAuthenticatorDuringLogin, MaxTwoFactorCodeFailures));
							Stop();
						}
					}

					break;
				default:
					// Unexpected result, shutdown immediately
					ArchiLogger.LogGenericError(string.Format(Strings.BotUnableToLogin, callback.Result, callback.ExtendedResult));
					Stop();

					break;
			}
		}

		private void OnLoginKey(SteamUser.LoginKeyCallback callback) {
			if (string.IsNullOrEmpty(callback?.LoginKey)) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.LoginKey));

				return;
			}

			if (!BotConfig.UseLoginKeys) {
				return;
			}

			string loginKey = callback.LoginKey;

			if (BotConfig.PasswordFormat != ArchiCryptoHelper.ECryptoMethod.PlainText) {
				loginKey = ArchiCryptoHelper.Encrypt(BotConfig.PasswordFormat, loginKey);
			}

			BotDatabase.LoginKey = loginKey;
			SteamUser.AcceptNewLoginKey(callback);
		}

		private async void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			string sentryFilePath = GetFilePath(EFileType.SentryFile);

			if (string.IsNullOrEmpty(sentryFilePath)) {
				ArchiLogger.LogNullError(nameof(sentryFilePath));

				return;
			}

			long fileSize;
			byte[] sentryHash;

			try {
				using FileStream fileStream = File.Open(sentryFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

				fileStream.Seek(callback.Offset, SeekOrigin.Begin);

				await fileStream.WriteAsync(callback.Data, 0, callback.BytesToWrite).ConfigureAwait(false);

				fileSize = fileStream.Length;
				fileStream.Seek(0, SeekOrigin.Begin);

				using SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider();

				sentryHash = sha.ComputeHash(fileStream);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);

				try {
					File.Delete(sentryFilePath);
				} catch {
					// Ignored, we can only try to delete faulted file at best
				}

				return;
			}

			// Inform the steam servers that we're accepting this sentry file
			SteamUser.SendMachineAuthResponse(
				new SteamUser.MachineAuthDetails {
					BytesWritten = callback.BytesToWrite,
					FileName = callback.FileName,
					FileSize = (int) fileSize,
					JobID = callback.JobID,
					LastError = 0,
					Offset = callback.Offset,
					OneTimePassword = callback.OneTimePassword,
					Result = EResult.OK,
					SentryFileHash = sentryHash
				}
			);
		}

		private void OnPersonaState(SteamFriends.PersonaStateCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			if (callback.FriendID != SteamID) {
				return;
			}

			string avatarHash = null;

			if ((callback.AvatarHash != null) && (callback.AvatarHash.Length > 0) && callback.AvatarHash.Any(singleByte => singleByte != 0)) {
				avatarHash = BitConverter.ToString(callback.AvatarHash).Replace("-", "").ToLowerInvariant();

				if (string.IsNullOrEmpty(avatarHash) || avatarHash.All(singleChar => singleChar == '0')) {
					avatarHash = null;
				}
			}

			AvatarHash = avatarHash;
			Nickname = callback.Name;

			if (Statistics != null) {
				Utilities.InBackground(() => Statistics.OnPersonaState(callback.Name, avatarHash));
			}
		}

		private async void OnPlayingSessionState(ArchiHandler.PlayingSessionStateCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			if (callback.PlayingBlocked == PlayingBlocked) {
				return; // No status update, we're not interested
			}

			PlayingBlocked = callback.PlayingBlocked;
			await CheckOccupationStatus().ConfigureAwait(false);
		}

		private async void OnServiceMethod(SteamUnifiedMessages.ServiceMethodNotification notification) {
			if (notification == null) {
				ArchiLogger.LogNullError(nameof(notification));

				return;
			}

			switch (notification.MethodName) {
				case "ChatRoomClient.NotifyIncomingChatMessage#1":
					await OnIncomingChatMessage((CChatRoom_IncomingChatMessage_Notification) notification.Body).ConfigureAwait(false);

					break;
				case "FriendMessagesClient.IncomingMessage#1":
					await OnIncomingMessage((CFriendMessages_IncomingMessage_Notification) notification.Body).ConfigureAwait(false);

					break;
			}
		}

		private async void OnSharedLibraryLockStatus(ArchiHandler.SharedLibraryLockStatusCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			// Ignore no status updates
			if (LibraryLocked) {
				if ((callback.LibraryLockedBySteamID != 0) && (callback.LibraryLockedBySteamID != SteamID)) {
					return;
				}

				LibraryLocked = false;
			} else {
				if ((callback.LibraryLockedBySteamID == 0) || (callback.LibraryLockedBySteamID == SteamID)) {
					return;
				}

				LibraryLocked = true;
			}

			await CheckOccupationStatus().ConfigureAwait(false);
		}

		private void OnUserNotifications(ArchiHandler.UserNotificationsCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			if ((callback.Notifications == null) || (callback.Notifications.Count == 0)) {
				return;
			}

			HashSet<ArchiHandler.UserNotificationsCallback.EUserNotification> newPluginNotifications = new HashSet<ArchiHandler.UserNotificationsCallback.EUserNotification>();

			foreach ((ArchiHandler.UserNotificationsCallback.EUserNotification notification, uint count) in callback.Notifications) {
				bool newNotification;

				if (count > 0) {
					newNotification = !PastNotifications.TryGetValue(notification, out uint previousCount) || (count > previousCount);
					PastNotifications[notification] = count;

					if (newNotification) {
						newPluginNotifications.Add(notification);
					}
				} else {
					newNotification = false;
					PastNotifications.TryRemove(notification, out _);
				}

				ArchiLogger.LogGenericTrace(notification + " = " + count);

				switch (notification) {
					case ArchiHandler.UserNotificationsCallback.EUserNotification.Gifts when newNotification && BotConfig.AcceptGifts:
						Utilities.InBackground(Actions.AcceptDigitalGiftCards);

						break;
					case ArchiHandler.UserNotificationsCallback.EUserNotification.Items when newNotification:
						Utilities.InBackground(CardsFarmer.OnNewItemsNotification);

						if (BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.DismissInventoryNotifications)) {
							Utilities.InBackground(ArchiWebHandler.MarkInventory);
						}

						break;
					case ArchiHandler.UserNotificationsCallback.EUserNotification.Trading when newNotification:
						Utilities.InBackground(Trading.OnNewTrade);

						break;
				}
			}

			if (newPluginNotifications.Count > 0) {
				Utilities.InBackground(() => PluginsCore.OnBotUserNotifications(this, newPluginNotifications));
			}
		}

		private void OnVanityURLChangedCallback(ArchiHandler.VanityURLChangedCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			ArchiWebHandler.OnVanityURLChanged(callback.VanityURL);
		}

		private void OnWalletUpdate(SteamUser.WalletInfoCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));

				return;
			}

			WalletBalance = callback.LongBalance;
			WalletCurrency = callback.Currency;
		}

		[SuppressMessage("ReSharper", "FunctionComplexityOverflow")]
		private async Task RedeemGamesInBackground() {
			if (!await GamesRedeemerInBackgroundSemaphore.WaitAsync(0).ConfigureAwait(false)) {
				return;
			}

			try {
				if (GamesRedeemerInBackgroundTimer != null) {
					GamesRedeemerInBackgroundTimer.Dispose();
					GamesRedeemerInBackgroundTimer = null;
				}

				ArchiLogger.LogGenericInfo(Strings.Starting);

				bool assumeWalletKeyOnBadActivationCode = BotConfig.RedeemingPreferences.HasFlag(BotConfig.ERedeemingPreferences.AssumeWalletKeyOnBadActivationCode);

				while (IsConnectedAndLoggedOn && BotDatabase.HasGamesToRedeemInBackground) {
					(string key, string name) = BotDatabase.GetGameToRedeemInBackground();

					if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name)) {
						ArchiLogger.LogNullError(nameof(key) + " || " + nameof(name));

						break;
					}

					ArchiHandler.PurchaseResponseCallback result = await Actions.RedeemKey(key).ConfigureAwait(false);

					if (result == null) {
						continue;
					}

					if (((result.PurchaseResultDetail == EPurchaseResultDetail.CannotRedeemCodeFromClient) || ((result.PurchaseResultDetail == EPurchaseResultDetail.BadActivationCode) && assumeWalletKeyOnBadActivationCode)) && (WalletCurrency != ECurrencyCode.Invalid)) {
						// If it's a wallet code, we try to redeem it first, then handle the inner result as our primary one
						(EResult Result, EPurchaseResultDetail? PurchaseResult)? walletResult = await ArchiWebHandler.RedeemWalletKey(key).ConfigureAwait(false);

						if (walletResult != null) {
							result.Result = walletResult.Value.Result;
							result.PurchaseResultDetail = walletResult.Value.PurchaseResult.GetValueOrDefault(walletResult.Value.Result == EResult.OK ? EPurchaseResultDetail.NoDetail : EPurchaseResultDetail.BadActivationCode); // BadActivationCode is our smart guess in this case
						} else {
							result.Result = EResult.Timeout;
							result.PurchaseResultDetail = EPurchaseResultDetail.Timeout;
						}
					}

					ArchiLogger.LogGenericDebug(string.Format(Strings.BotRedeem, key, result.Result + "/" + result.PurchaseResultDetail));

					bool rateLimited = false;
					bool redeemed = false;

					switch (result.PurchaseResultDetail) {
						case EPurchaseResultDetail.AccountLocked:
						case EPurchaseResultDetail.AlreadyPurchased:
						case EPurchaseResultDetail.CannotRedeemCodeFromClient:
						case EPurchaseResultDetail.DoesNotOwnRequiredApp:
						case EPurchaseResultDetail.RestrictedCountry:
						case EPurchaseResultDetail.Timeout:
							break;
						case EPurchaseResultDetail.BadActivationCode:
						case EPurchaseResultDetail.DuplicateActivationCode:
						case EPurchaseResultDetail.NoDetail: // OK
							redeemed = true;

							break;
						case EPurchaseResultDetail.RateLimited:
							rateLimited = true;

							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(result.PurchaseResultDetail), result.PurchaseResultDetail));

							break;
					}

					if (rateLimited) {
						break;
					}

					BotDatabase.RemoveGameToRedeemInBackground(key);

					// If user omitted the name or intentionally provided the same name as key, replace it with the Steam result
					if (name.Equals(key) && (result.Items != null) && (result.Items.Count > 0)) {
						name = string.Join(", ", result.Items.Values);
					}

					string logEntry = name + DefaultBackgroundKeysRedeemerSeparator + "[" + result.PurchaseResultDetail + "]" + ((result.Items != null) && (result.Items.Count > 0) ? DefaultBackgroundKeysRedeemerSeparator + string.Join(", ", result.Items) : "") + DefaultBackgroundKeysRedeemerSeparator + key;

					string filePath = GetFilePath(redeemed ? EFileType.KeysToRedeemUsed : EFileType.KeysToRedeemUnused);

					if (string.IsNullOrEmpty(filePath)) {
						ArchiLogger.LogNullError(nameof(filePath));

						return;
					}

					try {
						await RuntimeCompatibility.File.AppendAllTextAsync(filePath, logEntry + Environment.NewLine).ConfigureAwait(false);
					} catch (Exception e) {
						ArchiLogger.LogGenericException(e);
						ArchiLogger.LogGenericError(string.Format(Strings.Content, logEntry));

						break;
					}
				}

				if (IsConnectedAndLoggedOn && BotDatabase.HasGamesToRedeemInBackground) {
					ArchiLogger.LogGenericInfo(string.Format(Strings.BotRateLimitExceeded, TimeSpan.FromHours(RedeemCooldownInHours).ToHumanReadable()));

					GamesRedeemerInBackgroundTimer = new Timer(
						async e => await RedeemGamesInBackground().ConfigureAwait(false),
						null,
						TimeSpan.FromHours(RedeemCooldownInHours), // Delay
						Timeout.InfiniteTimeSpan // Period
					);
				}

				ArchiLogger.LogGenericInfo(Strings.Done);
			} finally {
				GamesRedeemerInBackgroundSemaphore.Release();
			}
		}

		private async Task ResetGamesPlayed() {
			if (CardsFarmer.NowFarming) {
				return;
			}

			if (BotConfig.GamesPlayedWhileIdle.Count > 0) {
				if (!IsPlayingPossible) {
					return;
				}

				// This function might be executed before PlayingSessionStateCallback/SharedLibraryLockStatusCallback, ensure proper delay in this case
				await Task.Delay(2000).ConfigureAwait(false);

				if (CardsFarmer.NowFarming || !IsPlayingPossible) {
					return;
				}
			}

			await ArchiHandler.PlayGames(BotConfig.GamesPlayedWhileIdle, BotConfig.CustomGamePlayedWhileIdle).ConfigureAwait(false);
		}

		private void ResetPlayingWasBlockedWithTimer() {
			PlayingWasBlocked = false;
			StopPlayingWasBlockedTimer();
		}

		private bool ShouldAckChatMessage(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			if (BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.MarkReceivedMessagesAsRead)) {
				return true;
			}

			return BotConfig.BotBehaviour.HasFlag(BotConfig.EBotBehaviour.MarkBotMessagesAsRead) && Bots.Values.Any(bot => bot.SteamID == steamID);
		}

		private void StopConnectionFailureTimer() {
			if (ConnectionFailureTimer == null) {
				return;
			}

			ConnectionFailureTimer.Dispose();
			ConnectionFailureTimer = null;
		}

		private void StopPlayingWasBlockedTimer() {
			if (PlayingWasBlockedTimer == null) {
				return;
			}

			PlayingWasBlockedTimer.Dispose();
			PlayingWasBlockedTimer = null;
		}

		private static string UnEscape(string message) {
			if (string.IsNullOrEmpty(message)) {
				ASF.ArchiLogger.LogNullError(nameof(message));

				return null;
			}

			return message.Replace("\\[", "[").Replace("\\\\", "\\");
		}

		private (bool IsSteamParentalEnabled, string SteamParentalCode) ValidateSteamParental(ParentalSettings settings, string steamParentalCode = null) {
			if (settings == null) {
				ArchiLogger.LogNullError(nameof(settings));

				return (false, null);
			}

			if (!settings.is_enabled) {
				return (false, null);
			}

			ArchiCryptoHelper.ESteamParentalAlgorithm steamParentalAlgorithm;

			switch (settings.passwordhashtype) {
				case 4:
					steamParentalAlgorithm = ArchiCryptoHelper.ESteamParentalAlgorithm.Pbkdf2;

					break;
				case 6:
					steamParentalAlgorithm = ArchiCryptoHelper.ESteamParentalAlgorithm.SCrypt;

					break;
				default:
					ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(settings.passwordhashtype), settings.passwordhashtype));

					return (true, null);
			}

			if ((steamParentalCode != null) && (steamParentalCode.Length == BotConfig.SteamParentalCodeLength)) {
				byte i = 0;
				byte[] password = new byte[steamParentalCode.Length];

				foreach (char character in steamParentalCode.TakeWhile(character => (character >= '0') && (character <= '9'))) {
					password[i++] = (byte) character;
				}

				if (i >= steamParentalCode.Length) {
					IEnumerable<byte> passwordHash = ArchiCryptoHelper.GenerateSteamParentalHash(password, settings.salt, (byte) settings.passwordhash.Length, steamParentalAlgorithm);

					if (passwordHash?.SequenceEqual(settings.passwordhash) == true) {
						return (true, steamParentalCode);
					}
				}
			}

			ArchiLogger.LogGenericInfo(Strings.BotGeneratingSteamParentalCode);

			steamParentalCode = ArchiCryptoHelper.RecoverSteamParentalCode(settings.passwordhash, settings.salt, steamParentalAlgorithm);

			ArchiLogger.LogGenericInfo(Strings.Done);

			return (true, steamParentalCode);
		}

		internal enum EFileType : byte {
			Config,
			Database,
			KeysToRedeem,
			KeysToRedeemUnused,
			KeysToRedeemUsed,
			MobileAuthenticator,
			SentryFile
		}
	}
}
