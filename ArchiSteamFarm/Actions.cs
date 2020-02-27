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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Helpers;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using JetBrains.Annotations;
using SteamKit2;

namespace ArchiSteamFarm {
	public sealed class Actions : IDisposable {
		private static readonly SemaphoreSlim GiftCardsSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim GiftsSemaphore = new SemaphoreSlim(1, 1);

		private readonly Bot Bot;
		private readonly ConcurrentHashSet<ulong> HandledGifts = new ConcurrentHashSet<ulong>();
		private readonly SemaphoreSlim TradingSemaphore = new SemaphoreSlim(1, 1);

		private Timer CardsFarmerResumeTimer;
		private bool ProcessingGiftsScheduled;
		private bool TradingScheduled;

		internal Actions([JetBrains.Annotations.NotNull] Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public void Dispose() {
			// Those are objects that are always being created if constructor doesn't throw exception
			TradingSemaphore.Dispose();

			// Those are objects that might be null and the check should be in-place
			CardsFarmerResumeTimer?.Dispose();
		}

		[PublicAPI]
		public static (bool Success, string Message) Exit() {
			// Schedule the task after some time so user can receive response
			Utilities.InBackground(
				async () => {
					await Task.Delay(1000).ConfigureAwait(false);
					await Program.Exit().ConfigureAwait(false);
				}
			);

			return (true, Strings.Done);
		}

		[PublicAPI]
		public async Task<(bool Success, string Token, string Message)> GenerateTwoFactorAuthenticationToken() {
			if (!Bot.HasMobileAuthenticator) {
				return (false, null, Strings.BotNoASFAuthenticator);
			}

			string token = await Bot.BotDatabase.MobileAuthenticator.GenerateToken().ConfigureAwait(false);

			return (true, token, Strings.Success);
		}

		[ItemNotNull]
		[PublicAPI]
		public async Task<IDisposable> GetTradingLock() {
			await TradingSemaphore.WaitAsync().ConfigureAwait(false);

			return new SemaphoreLock(TradingSemaphore);
		}

		[PublicAPI]
		public async Task<(bool Success, string Message)> HandleTwoFactorAuthenticationConfirmations(bool accept, Steam.ConfirmationDetails.EType? acceptedType = null, IReadOnlyCollection<ulong> acceptedTradeOfferIDs = null, bool waitIfNeeded = false) {
			if (!Bot.HasMobileAuthenticator) {
				return (false, Strings.BotNoASFAuthenticator);
			}

			if (!Bot.IsConnectedAndLoggedOn) {
				return (false, Strings.BotNotConnected);
			}

			HashSet<ulong> handledTradeOfferIDs = null;

			for (byte i = 0; (i == 0) || ((i < WebBrowser.MaxTries) && waitIfNeeded); i++) {
				if (i > 0) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				HashSet<MobileAuthenticator.Confirmation> confirmations = await Bot.BotDatabase.MobileAuthenticator.GetConfirmations(acceptedType).ConfigureAwait(false);

				if ((confirmations == null) || (confirmations.Count == 0)) {
					continue;
				}

				// If we can skip asking for details, we can handle confirmations right away
				if ((acceptedTradeOfferIDs == null) || (acceptedTradeOfferIDs.Count == 0)) {
					bool result = await Bot.BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false);

					return (result, result ? string.Format(Strings.BotHandledConfirmations, confirmations.Count) : Strings.WarningFailed);
				}

				IList<Steam.ConfirmationDetails> results = await Utilities.InParallel(confirmations.Select(Bot.BotDatabase.MobileAuthenticator.GetConfirmationDetails)).ConfigureAwait(false);

				foreach (MobileAuthenticator.Confirmation confirmation in results.Where(details => (details != null) && ((acceptedType.HasValue && (acceptedType.Value != details.Type)) || ((details.TradeOfferID != 0) && !acceptedTradeOfferIDs.Contains(details.TradeOfferID)))).Select(details => details.Confirmation)) {
					confirmations.Remove(confirmation);
				}

				if (confirmations.Count == 0) {
					continue;
				}

				if (!await Bot.BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false)) {
					return (false, Strings.WarningFailed);
				}

				IEnumerable<ulong> handledTradeOfferIDsThisRound = results.Where(details => (details != null) && (details.TradeOfferID != 0)).Select(result => result.TradeOfferID);

				if (handledTradeOfferIDs != null) {
					handledTradeOfferIDs.UnionWith(handledTradeOfferIDsThisRound);
				} else {
					handledTradeOfferIDs = handledTradeOfferIDsThisRound.ToHashSet();
				}

				// Check if those are all that we were expected to confirm
				if (acceptedTradeOfferIDs.All(handledTradeOfferIDs.Contains)) {
					return (true, string.Format(Strings.BotHandledConfirmations, acceptedTradeOfferIDs.Count));
				}
			}

			return (!waitIfNeeded, !waitIfNeeded ? Strings.Success : string.Format(Strings.ErrorRequestFailedTooManyTimes, WebBrowser.MaxTries));
		}

		[PublicAPI]
		public async Task<(bool Success, string Message)> Pause(bool permanent, ushort resumeInSeconds = 0) {
			if (Bot.CardsFarmer.Paused) {
				return (false, Strings.BotAutomaticIdlingPausedAlready);
			}

			await Bot.CardsFarmer.Pause(permanent).ConfigureAwait(false);

			if (!permanent && (Bot.BotConfig.GamesPlayedWhileIdle.Count > 0)) {
				// We want to let family sharing users access our library, and in this case we must also stop GamesPlayedWhileIdle
				// We add extra delay because OnFarmingStopped() also executes PlayGames()
				// Despite of proper order on our end, Steam network might not respect it
				await Task.Delay(Bot.CallbackSleep).ConfigureAwait(false);
				await Bot.ArchiHandler.PlayGames(Enumerable.Empty<uint>(), Bot.BotConfig.CustomGamePlayedWhileIdle).ConfigureAwait(false);
			}

			if (resumeInSeconds > 0) {
				if (CardsFarmerResumeTimer == null) {
					CardsFarmerResumeTimer = new Timer(
						e => Resume(),
						null,
						TimeSpan.FromSeconds(resumeInSeconds), // Delay
						Timeout.InfiniteTimeSpan // Period
					);
				} else {
					CardsFarmerResumeTimer.Change(TimeSpan.FromSeconds(resumeInSeconds), Timeout.InfiniteTimeSpan);
				}
			}

			return (true, Strings.BotAutomaticIdlingNowPaused);
		}

		[PublicAPI]
		public async Task<(bool Success, string Message)> Play(IEnumerable<uint> gameIDs, string gameName = null) {
			if (gameIDs == null) {
				Bot.ArchiLogger.LogNullError(nameof(gameIDs));

				return (false, string.Format(Strings.ErrorObjectIsNull, nameof(gameIDs)));
			}

			if (!Bot.IsConnectedAndLoggedOn) {
				return (false, Strings.BotNotConnected);
			}

			if (!Bot.CardsFarmer.Paused) {
				await Bot.CardsFarmer.Pause(true).ConfigureAwait(false);
			}

			await Bot.ArchiHandler.PlayGames(gameIDs, gameName).ConfigureAwait(false);

			return (true, Strings.Done);
		}

		[PublicAPI]
		public async Task<ArchiHandler.PurchaseResponseCallback> RedeemKey(string key) {
			await LimitGiftsRequestsAsync().ConfigureAwait(false);

			return await Bot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
		}

		[PublicAPI]
		public static (bool Success, string Message) Restart() {
			// Schedule the task after some time so user can receive response
			Utilities.InBackground(
				async () => {
					await Task.Delay(1000).ConfigureAwait(false);
					await Program.Restart().ConfigureAwait(false);
				}
			);

			return (true, Strings.Done);
		}

		[PublicAPI]
		public (bool Success, string Message) Resume() {
			if (!Bot.CardsFarmer.Paused) {
				return (false, Strings.BotAutomaticIdlingResumedAlready);
			}

			Utilities.InBackground(() => Bot.CardsFarmer.Resume(true));

			return (true, Strings.BotAutomaticIdlingNowResumed);
		}

		[Obsolete]
		[PublicAPI]
		public async Task<(bool Success, string Message)> SendTradeOffer(uint appID = Steam.Asset.SteamAppID, ulong contextID = Steam.Asset.SteamCommunityContextID, ulong targetSteamID = 0, string tradeToken = null, IReadOnlyCollection<uint> wantedRealAppIDs = null, IReadOnlyCollection<uint> unwantedRealAppIDs = null, IReadOnlyCollection<Steam.Asset.EType> wantedTypes = null) {
			if ((appID == 0) || (contextID == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(contextID));

				return (false, string.Format(Strings.ErrorObjectIsNull, nameof(appID) + " || " + nameof(contextID)));
			}

			if ((wantedRealAppIDs?.Count == 0) || (unwantedRealAppIDs?.Count == 0) || (wantedTypes?.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(wantedRealAppIDs) + " || " + nameof(unwantedRealAppIDs) + " || " + nameof(wantedTypes));

				return (false, string.Format(Strings.ErrorObjectIsNull, nameof(wantedRealAppIDs) + " || " + nameof(unwantedRealAppIDs) + " || " + nameof(wantedTypes)));
			}

			return await SendInventory(appID, contextID, targetSteamID, tradeToken, item => (wantedRealAppIDs?.Contains(item.RealAppID) != false) && (unwantedRealAppIDs?.Contains(item.RealAppID) != true) && (wantedTypes?.Contains(item.Type) != false)).ConfigureAwait(false);
		}

		[PublicAPI]
		[SuppressMessage("ReSharper", "FunctionComplexityOverflow")]
		public async Task<(bool Success, string Message)> SendInventory(uint appID = Steam.Asset.SteamAppID, ulong contextID = Steam.Asset.SteamCommunityContextID, ulong targetSteamID = 0, string tradeToken = null, Func<Steam.Asset, bool> filterFunction = null) {
			if ((appID == 0) || (contextID == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(contextID));

				return (false, string.Format(Strings.ErrorObjectIsNull, nameof(appID) + " || " + nameof(contextID)));
			}

			if (!Bot.IsConnectedAndLoggedOn) {
				return (false, Strings.BotNotConnected);
			}

			if (targetSteamID == 0) {
				targetSteamID = GetFirstSteamMasterID();

				if (targetSteamID == 0) {
					return (false, Strings.BotLootingMasterNotDefined);
				}

				if (string.IsNullOrEmpty(tradeToken) && !string.IsNullOrEmpty(Bot.BotConfig.SteamTradeToken)) {
					tradeToken = Bot.BotConfig.SteamTradeToken;
				}
			}

			if (targetSteamID == Bot.SteamID) {
				return (false, Strings.BotSendingTradeToYourself);
			}

			lock (TradingSemaphore) {
				if (TradingScheduled) {
					return (false, Strings.ErrorAborted);
				}

				TradingScheduled = true;
			}

			filterFunction ??= item => true;
			await TradingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				lock (TradingSemaphore) {
					TradingScheduled = false;
				}

				HashSet<Steam.Asset> inventory;

				try {
					inventory = await Bot.ArchiWebHandler.GetInventoryAsync(Bot.SteamID, appID, contextID).Where(item => item.Tradable && filterFunction(item)).ToHashSetAsync().ConfigureAwait(false);
				} catch (HttpRequestException e) {
					return (false, string.Format(Strings.WarningFailedWithError, e.Message));
				} catch (Exception e) {
					Bot.ArchiLogger.LogGenericException(e);

					return (false, string.Format(Strings.WarningFailedWithError, e.Message));
				}

				if (inventory.Count == 0) {
					return (false, string.Format(Strings.ErrorIsEmpty, nameof(inventory)));
				}

				if (!await Bot.ArchiWebHandler.MarkSentTrades().ConfigureAwait(false)) {
					return (false, Strings.BotLootingFailed);
				}

				if (string.IsNullOrEmpty(tradeToken) && (Bot.SteamFriends.GetFriendRelationship(targetSteamID) != EFriendRelationship.Friend)) {
					Bot targetBot = Bot.Bots.Values.FirstOrDefault(bot => bot.SteamID == targetSteamID);

					if (targetBot?.IsConnectedAndLoggedOn == true) {
						tradeToken = await targetBot.ArchiHandler.GetTradeToken().ConfigureAwait(false);
					}
				}

				(bool success, HashSet<ulong> mobileTradeOfferIDs) = await Bot.ArchiWebHandler.SendTradeOffer(targetSteamID, inventory, token: tradeToken).ConfigureAwait(false);

				if ((mobileTradeOfferIDs != null) && (mobileTradeOfferIDs.Count > 0) && Bot.HasMobileAuthenticator) {
					(bool twoFactorSuccess, _) = await HandleTwoFactorAuthenticationConfirmations(true, Steam.ConfirmationDetails.EType.Trade, mobileTradeOfferIDs, true).ConfigureAwait(false);

					if (!twoFactorSuccess) {
						return (false, Strings.BotLootingFailed);
					}
				}

				if (!success) {
					return (false, Strings.BotLootingFailed);
				}
			} finally {
				TradingSemaphore.Release();
			}

			return (true, Strings.BotLootingSuccess);
		}

		[PublicAPI]
		public (bool Success, string Message) Start() {
			if (Bot.KeepRunning) {
				return (false, Strings.BotAlreadyRunning);
			}

			Utilities.InBackground(Bot.Start);

			return (true, Strings.Done);
		}

		[PublicAPI]
		public (bool Success, string Message) Stop() {
			if (!Bot.KeepRunning) {
				return (false, Strings.BotAlreadyStopped);
			}

			Bot.Stop();

			return (true, Strings.Done);
		}

		[PublicAPI]
		public static async Task<(bool Success, string Message, Version Version)> Update() {
			Version version = await ASF.Update(true).ConfigureAwait(false);

			if (version == null) {
				return (false, null, null);
			}

			if (SharedInfo.Version >= version) {
				return (false, "V" + SharedInfo.Version + " ≥ V" + version, version);
			}

			Utilities.InBackground(ASF.RestartOrExit);

			return (true, null, version);
		}

		internal async Task AcceptDigitalGiftCards() {
			lock (GiftCardsSemaphore) {
				if (ProcessingGiftsScheduled) {
					return;
				}

				ProcessingGiftsScheduled = true;
			}

			await GiftCardsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				lock (GiftCardsSemaphore) {
					ProcessingGiftsScheduled = false;
				}

				HashSet<ulong> giftCardIDs = await Bot.ArchiWebHandler.GetDigitalGiftCards().ConfigureAwait(false);

				if ((giftCardIDs == null) || (giftCardIDs.Count == 0)) {
					return;
				}

				foreach (ulong giftCardID in giftCardIDs.Where(gid => !HandledGifts.Contains(gid))) {
					HandledGifts.Add(giftCardID);

					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.BotAcceptingGift, giftCardID));
					await LimitGiftsRequestsAsync().ConfigureAwait(false);

					bool result = await Bot.ArchiWebHandler.AcceptDigitalGiftCard(giftCardID).ConfigureAwait(false);

					if (result) {
						Bot.ArchiLogger.LogGenericInfo(Strings.Success);
					} else {
						Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
					}
				}
			} finally {
				GiftCardsSemaphore.Release();
			}
		}

		internal async Task AcceptGuestPasses(IReadOnlyCollection<ulong> guestPassIDs) {
			if ((guestPassIDs == null) || (guestPassIDs.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(guestPassIDs));

				return;
			}

			foreach (ulong guestPassID in guestPassIDs.Where(guestPassID => !HandledGifts.Contains(guestPassID))) {
				HandledGifts.Add(guestPassID);

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.BotAcceptingGift, guestPassID));
				await LimitGiftsRequestsAsync().ConfigureAwait(false);

				ArchiHandler.RedeemGuestPassResponseCallback response = await Bot.ArchiHandler.RedeemGuestPass(guestPassID).ConfigureAwait(false);

				if (response != null) {
					if (response.Result == EResult.OK) {
						Bot.ArchiLogger.LogGenericInfo(Strings.Success);
					} else {
						Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, response.Result));
					}
				} else {
					Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
				}
			}
		}

		internal void OnDisconnected() => HandledGifts.Clear();

		private ulong GetFirstSteamMasterID() => Bot.BotConfig.SteamUserPermissions.Where(kv => (kv.Key != 0) && (kv.Value == BotConfig.EPermission.Master)).Select(kv => kv.Key).OrderByDescending(steamID => steamID != Bot.SteamID).ThenBy(steamID => steamID).FirstOrDefault();

		private static async Task LimitGiftsRequestsAsync() {
			if (ASF.GlobalConfig.GiftsLimiterDelay == 0) {
				return;
			}

			await GiftsSemaphore.WaitAsync().ConfigureAwait(false);

			Utilities.InBackground(
				async () => {
					await Task.Delay(ASF.GlobalConfig.GiftsLimiterDelay * 1000).ConfigureAwait(false);
					GiftsSemaphore.Release();
				}
			);
		}
	}
}
