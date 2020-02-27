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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using ArchiSteamFarm.CMsgs;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.NLog;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.Unified.Internal;

namespace ArchiSteamFarm {
	public sealed class ArchiHandler : ClientMsgHandler {
		internal const byte MaxGamesPlayedConcurrently = 32; // This is limit introduced by Steam Network

		private readonly ArchiLogger ArchiLogger;
		private readonly SteamUnifiedMessages.UnifiedService<IChatRoom> UnifiedChatRoomService;
		private readonly SteamUnifiedMessages.UnifiedService<IClanChatRooms> UnifiedClanChatRoomsService;
		private readonly SteamUnifiedMessages.UnifiedService<IEcon> UnifiedEconService;
		private readonly SteamUnifiedMessages.UnifiedService<IFriendMessages> UnifiedFriendMessagesService;
		private readonly SteamUnifiedMessages.UnifiedService<IPlayer> UnifiedPlayerService;

		internal DateTime LastPacketReceived { get; private set; }

		internal ArchiHandler([JetBrains.Annotations.NotNull] ArchiLogger archiLogger, [JetBrains.Annotations.NotNull] SteamUnifiedMessages steamUnifiedMessages) {
			if ((archiLogger == null) || (steamUnifiedMessages == null)) {
				throw new ArgumentNullException(nameof(archiLogger) + " || " + nameof(steamUnifiedMessages));
			}

			ArchiLogger = archiLogger;
			UnifiedChatRoomService = steamUnifiedMessages.CreateService<IChatRoom>();
			UnifiedClanChatRoomsService = steamUnifiedMessages.CreateService<IClanChatRooms>();
			UnifiedEconService = steamUnifiedMessages.CreateService<IEcon>();
			UnifiedFriendMessagesService = steamUnifiedMessages.CreateService<IFriendMessages>();
			UnifiedPlayerService = steamUnifiedMessages.CreateService<IPlayer>();
		}

		public override void HandleMsg(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			LastPacketReceived = DateTime.UtcNow;

			switch (packetMsg.MsgType) {
				case EMsg.ClientItemAnnouncements:
					HandleItemAnnouncements(packetMsg);

					break;
				case EMsg.ClientPlayingSessionState:
					HandlePlayingSessionState(packetMsg);

					break;
				case EMsg.ClientPurchaseResponse:
					HandlePurchaseResponse(packetMsg);

					break;
				case EMsg.ClientRedeemGuestPassResponse:
					HandleRedeemGuestPassResponse(packetMsg);

					break;
				case EMsg.ClientSharedLibraryLockStatus:
					HandleSharedLibraryLockStatus(packetMsg);

					break;
				case EMsg.ClientUserNotifications:
					HandleUserNotifications(packetMsg);

					break;
				case EMsg.ClientVanityURLChangedNotification:
					HandleVanityURLChangedNotification(packetMsg);

					break;
			}
		}

		internal void AckChatMessage(ulong chatGroupID, ulong chatID, uint timestamp) {
			if ((chatGroupID == 0) || (chatID == 0) || (timestamp == 0)) {
				ArchiLogger.LogNullError(nameof(chatGroupID) + " || " + nameof(chatID) + " || " + nameof(timestamp));

				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			CChatRoom_AckChatMessage_Notification request = new CChatRoom_AckChatMessage_Notification {
				chat_group_id = chatGroupID,
				chat_id = chatID,
				timestamp = timestamp
			};

			UnifiedChatRoomService.SendMessage(x => x.AckChatMessage(request), true);
		}

		internal void AckMessage(ulong steamID, uint timestamp) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || (timestamp == 0)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(timestamp));

				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			CFriendMessages_AckMessage_Notification request = new CFriendMessages_AckMessage_Notification {
				steamid_partner = steamID,
				timestamp = timestamp
			};

			UnifiedFriendMessagesService.SendMessage(x => x.AckMessage(request), true);
		}

		internal void AcknowledgeClanInvite(ulong steamID, bool acceptInvite) {
			if ((steamID == 0) || !new SteamID(steamID).IsClanAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			ClientMsg<CMsgClientAcknowledgeClanInvite> request = new ClientMsg<CMsgClientAcknowledgeClanInvite> {
				Body = {
					ClanID = steamID,
					AcceptInvite = acceptInvite
				}
			};

			Client.Send(request);
		}

		internal async Task<bool> AddFriend(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			if (!Client.IsConnected) {
				return false;
			}

			CPlayer_AddFriend_Request request = new CPlayer_AddFriend_Request { steamid = steamID };

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedPlayerService.SendMessage(x => x.AddFriend(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return false;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return false;
			}

			return response.Result == EResult.OK;
		}

		internal async Task<ulong> GetClanChatGroupID(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsClanAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return 0;
			}

			if (!Client.IsConnected) {
				return 0;
			}

			CClanChatRooms_GetClanChatRoomInfo_Request request = new CClanChatRooms_GetClanChatRoomInfo_Request {
				autocreate = true,
				steamid = steamID
			};

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedClanChatRoomsService.SendMessage(x => x.GetClanChatRoomInfo(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return 0;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return 0;
			}

			if (response.Result != EResult.OK) {
				return 0;
			}

			CClanChatRooms_GetClanChatRoomInfo_Response body = response.GetDeserializedResponse<CClanChatRooms_GetClanChatRoomInfo_Response>();

			return body.chat_group_summary.chat_group_id;
		}

		internal async Task<uint?> GetLevel() {
			if (!Client.IsConnected) {
				return null;
			}

			CPlayer_GetGameBadgeLevels_Request request = new CPlayer_GetGameBadgeLevels_Request();
			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedPlayerService.SendMessage(x => x.GetGameBadgeLevels(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return null;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return null;
			}

			if (response.Result != EResult.OK) {
				return null;
			}

			CPlayer_GetGameBadgeLevels_Response body = response.GetDeserializedResponse<CPlayer_GetGameBadgeLevels_Response>();

			return body.player_level;
		}

		internal async Task<HashSet<ulong>> GetMyChatGroupIDs() {
			if (!Client.IsConnected) {
				return null;
			}

			CChatRoom_GetMyChatRoomGroups_Request request = new CChatRoom_GetMyChatRoomGroups_Request();

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedChatRoomService.SendMessage(x => x.GetMyChatRoomGroups(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return null;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return null;
			}

			if (response.Result != EResult.OK) {
				return null;
			}

			CChatRoom_GetMyChatRoomGroups_Response body = response.GetDeserializedResponse<CChatRoom_GetMyChatRoomGroups_Response>();

			return body.chat_room_groups.Select(chatRoom => chatRoom.group_summary.chat_group_id).ToHashSet();
		}

		internal async Task<string> GetTradeToken() {
			if (!Client.IsConnected) {
				return null;
			}

			CEcon_GetTradeOfferAccessToken_Request request = new CEcon_GetTradeOfferAccessToken_Request();

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedEconService.SendMessage(x => x.GetTradeOfferAccessToken(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return null;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return null;
			}

			if (response.Result != EResult.OK) {
				return null;
			}

			CEcon_GetTradeOfferAccessToken_Response body = response.GetDeserializedResponse<CEcon_GetTradeOfferAccessToken_Response>();

			return body.trade_offer_access_token;
		}

		internal async Task<bool> JoinChatRoomGroup(ulong chatGroupID) {
			if (chatGroupID == 0) {
				ArchiLogger.LogNullError(nameof(chatGroupID));

				return false;
			}

			if (!Client.IsConnected) {
				return false;
			}

			CChatRoom_JoinChatRoomGroup_Request request = new CChatRoom_JoinChatRoomGroup_Request { chat_group_id = chatGroupID };

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedChatRoomService.SendMessage(x => x.JoinChatRoomGroup(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return false;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return false;
			}

			return response.Result == EResult.OK;
		}

		internal async Task PlayGames(IEnumerable<uint> gameIDs, string gameName = null) {
			if (gameIDs == null) {
				ArchiLogger.LogNullError(nameof(gameIDs));

				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			ClientMsgProtobuf<CMsgClientGamesPlayed> request = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob) {
				Body = {
					client_os_type = (uint) Bot.OSType
				}
			};

			byte maxGamesCount = MaxGamesPlayedConcurrently;

			if (!string.IsNullOrEmpty(gameName)) {
				// If we have custom name to display, we must workaround the Steam network broken behaviour and send request on clean non-playing session
				// This ensures that custom name will in fact display properly
				Client.Send(request);
				await Task.Delay(Bot.CallbackSleep).ConfigureAwait(false);

				request.Body.games_played.Add(
					new CMsgClientGamesPlayed.GamePlayed {
						game_extra_info = gameName,
						game_id = new GameID {
							AppType = GameID.GameType.Shortcut,
							ModID = uint.MaxValue
						}
					}
				);

				// Max games count is affected by valid AppIDs only, therefore gameName alone doesn't need exclusive slot
				maxGamesCount++;
			}

			foreach (uint gameID in gameIDs.Where(gameID => gameID != 0)) {
				request.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(gameID) });

				if (request.Body.games_played.Count >= maxGamesCount) {
					break;
				}
			}

			Client.Send(request);
		}

		internal async Task<RedeemGuestPassResponseCallback> RedeemGuestPass(ulong guestPassID) {
			if (guestPassID == 0) {
				ArchiLogger.LogNullError(nameof(guestPassID));

				return null;
			}

			if (!Client.IsConnected) {
				return null;
			}

			ClientMsgProtobuf<CMsgClientRedeemGuestPass> request = new ClientMsgProtobuf<CMsgClientRedeemGuestPass>(EMsg.ClientRedeemGuestPass) {
				SourceJobID = Client.GetNextJobID(),
				Body = { guest_pass_id = guestPassID }
			};

			Client.Send(request);

			try {
				return await new AsyncJob<RedeemGuestPassResponseCallback>(Client, request.SourceJobID);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);

				return null;
			}
		}

		internal async Task<PurchaseResponseCallback> RedeemKey(string key) {
			if (string.IsNullOrEmpty(key)) {
				ArchiLogger.LogNullError(nameof(key));

				return null;
			}

			if (!Client.IsConnected) {
				return null;
			}

			ClientMsgProtobuf<CMsgClientRegisterKey> request = new ClientMsgProtobuf<CMsgClientRegisterKey>(EMsg.ClientRegisterKey) {
				SourceJobID = Client.GetNextJobID(),
				Body = { key = key }
			};

			Client.Send(request);

			try {
				return await new AsyncJob<PurchaseResponseCallback>(Client, request.SourceJobID);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);

				return null;
			}
		}

		internal async Task<bool> RemoveFriend(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return false;
			}

			if (!Client.IsConnected) {
				return false;
			}

			CPlayer_RemoveFriend_Request request = new CPlayer_RemoveFriend_Request { steamid = steamID };

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedPlayerService.SendMessage(x => x.RemoveFriend(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return false;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return false;
			}

			return response.Result == EResult.OK;
		}

		internal void RequestItemAnnouncements() {
			if (!Client.IsConnected) {
				return;
			}

			ClientMsgProtobuf<CMsgClientRequestItemAnnouncements> request = new ClientMsgProtobuf<CMsgClientRequestItemAnnouncements>(EMsg.ClientRequestItemAnnouncements);
			Client.Send(request);
		}

		internal async Task<EResult> SendMessage(ulong steamID, string message) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));

				return EResult.Fail;
			}

			if (!Client.IsConnected) {
				return EResult.NoConnection;
			}

			CFriendMessages_SendMessage_Request request = new CFriendMessages_SendMessage_Request {
				chat_entry_type = (int) EChatEntryType.ChatMsg,
				contains_bbcode = true,
				message = message,
				steamid = steamID
			};

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedFriendMessagesService.SendMessage(x => x.SendMessage(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return EResult.Timeout;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return EResult.Fail;
			}

			return response.Result;
		}

		internal async Task<EResult> SendMessage(ulong chatGroupID, ulong chatID, string message) {
			if ((chatGroupID == 0) || (chatID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(chatGroupID) + " || " + nameof(chatID) + " || " + nameof(message));

				return EResult.Fail;
			}

			if (!Client.IsConnected) {
				return EResult.NoConnection;
			}

			CChatRoom_SendChatMessage_Request request = new CChatRoom_SendChatMessage_Request {
				chat_group_id = chatGroupID,
				chat_id = chatID,
				message = message
			};

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedChatRoomService.SendMessage(x => x.SendChatMessage(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return EResult.Timeout;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return EResult.Fail;
			}

			return response.Result;
		}

		internal async Task<EResult> SendTypingStatus(ulong steamID) {
			if ((steamID == 0) || !new SteamID(steamID).IsIndividualAccount) {
				ArchiLogger.LogNullError(nameof(steamID));

				return EResult.Fail;
			}

			if (!Client.IsConnected) {
				return EResult.NoConnection;
			}

			CFriendMessages_SendMessage_Request request = new CFriendMessages_SendMessage_Request {
				chat_entry_type = (int) EChatEntryType.Typing,
				steamid = steamID
			};

			SteamUnifiedMessages.ServiceMethodResponse response;

			try {
				response = await UnifiedFriendMessagesService.SendMessage(x => x.SendMessage(request));
			} catch (Exception e) {
				ArchiLogger.LogGenericWarningException(e);

				return EResult.Timeout;
			}

			if (response == null) {
				ArchiLogger.LogNullError(nameof(response));

				return EResult.Fail;
			}

			return response.Result;
		}

		internal void SetCurrentMode(uint chatMode) {
			if (chatMode == 0) {
				ArchiLogger.LogNullError(nameof(chatMode));

				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			ClientMsgProtobuf<CMsgClientUIMode> request = new ClientMsgProtobuf<CMsgClientUIMode>(EMsg.ClientCurrentUIMode) { Body = { chat_mode = chatMode } };
			Client.Send(request);
		}

		private void HandleItemAnnouncements(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientItemAnnouncements> response = new ClientMsgProtobuf<CMsgClientItemAnnouncements>(packetMsg);
			Client.PostCallback(new UserNotificationsCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandlePlayingSessionState(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientPlayingSessionState> response = new ClientMsgProtobuf<CMsgClientPlayingSessionState>(packetMsg);
			Client.PostCallback(new PlayingSessionStateCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandlePurchaseResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientPurchaseResponse> response = new ClientMsgProtobuf<CMsgClientPurchaseResponse>(packetMsg);
			Client.PostCallback(new PurchaseResponseCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleRedeemGuestPassResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientRedeemGuestPassResponse> response = new ClientMsgProtobuf<CMsgClientRedeemGuestPassResponse>(packetMsg);
			Client.PostCallback(new RedeemGuestPassResponseCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleSharedLibraryLockStatus(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus> response = new ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus>(packetMsg);
			Client.PostCallback(new SharedLibraryLockStatusCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleUserNotifications(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientUserNotifications> response = new ClientMsgProtobuf<CMsgClientUserNotifications>(packetMsg);
			Client.PostCallback(new UserNotificationsCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleVanityURLChangedNotification(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			ClientMsgProtobuf<CMsgClientVanityURLChangedNotification> response = new ClientMsgProtobuf<CMsgClientVanityURLChangedNotification>(packetMsg);
			Client.PostCallback(new VanityURLChangedCallback(packetMsg.TargetJobID, response.Body));
		}

		[SuppressMessage("ReSharper", "MemberCanBeInternal")]
		public sealed class PurchaseResponseCallback : CallbackMsg {
			public readonly Dictionary<uint, string> Items;

			public EPurchaseResultDetail PurchaseResultDetail { get; internal set; }
			public EResult Result { get; internal set; }

			internal PurchaseResponseCallback(EResult result, EPurchaseResultDetail purchaseResult) {
				if (!Enum.IsDefined(typeof(EResult), result) || !Enum.IsDefined(typeof(EPurchaseResultDetail), purchaseResult)) {
					throw new ArgumentNullException(nameof(result) + " || " + nameof(purchaseResult));
				}

				Result = result;
				PurchaseResultDetail = purchaseResult;
			}

			internal PurchaseResponseCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientPurchaseResponse msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				PurchaseResultDetail = (EPurchaseResultDetail) msg.purchase_result_details;
				Result = (EResult) msg.eresult;

				if (msg.purchase_receipt_info == null) {
					ASF.ArchiLogger.LogNullError(nameof(msg.purchase_receipt_info));

					return;
				}

				KeyValue receiptInfo = new KeyValue();

				using (MemoryStream ms = new MemoryStream(msg.purchase_receipt_info)) {
					if (!receiptInfo.TryReadAsBinary(ms)) {
						ASF.ArchiLogger.LogNullError(nameof(ms));

						return;
					}
				}

				List<KeyValue> lineItems = receiptInfo["lineitems"].Children;

				if (lineItems.Count == 0) {
					return;
				}

				Items = new Dictionary<uint, string>(lineItems.Count);

				foreach (KeyValue lineItem in lineItems) {
					uint packageID = lineItem["PackageID"].AsUnsignedInteger();

					if (packageID == 0) {
						// Coupons have PackageID of -1 (don't ask me why)
						// We'll use ItemAppID in this case
						packageID = lineItem["ItemAppID"].AsUnsignedInteger();

						if (packageID == 0) {
							ASF.ArchiLogger.LogNullError(nameof(packageID));

							return;
						}
					}

					string gameName = lineItem["ItemDescription"].Value;

					if (string.IsNullOrEmpty(gameName)) {
						ASF.ArchiLogger.LogNullError(nameof(gameName));

						return;
					}

					// Apparently steam expects client to decode sent HTML
					gameName = WebUtility.HtmlDecode(gameName);
					Items[packageID] = gameName;
				}
			}
		}

		public sealed class UserNotificationsCallback : CallbackMsg {
			internal readonly Dictionary<EUserNotification, uint> Notifications;

			internal UserNotificationsCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientUserNotifications msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;

				// We might get null body here, and that means there are no notifications related to trading
				// TODO: Check if this workaround is still needed
				Notifications = new Dictionary<EUserNotification, uint> { { EUserNotification.Trading, 0 } };

				if (msg.notifications == null) {
					return;
				}

				foreach (CMsgClientUserNotifications.Notification notification in msg.notifications) {
					EUserNotification type = (EUserNotification) notification.user_notification_type;

					switch (type) {
						case EUserNotification.AccountAlerts:
						case EUserNotification.Chat:
						case EUserNotification.Comments:
						case EUserNotification.GameTurns:
						case EUserNotification.Gifts:
						case EUserNotification.HelpRequestReplies:
						case EUserNotification.Invites:
						case EUserNotification.Items:
						case EUserNotification.ModeratorMessages:
						case EUserNotification.Trading:
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(type), type));

							break;
					}

					Notifications[type] = notification.count;
				}
			}

			internal UserNotificationsCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientItemAnnouncements msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				Notifications = new Dictionary<EUserNotification, uint>(1) { { EUserNotification.Items, msg.count_new_items } };
			}

			[PublicAPI]
			public enum EUserNotification : byte {
				Unknown,
				Trading,
				GameTurns,
				ModeratorMessages,
				Comments,
				Items,
				Invites,
				Unknown7, // No clue what 7 stands for, and I doubt we can find out
				Gifts,
				Chat,
				HelpRequestReplies,
				AccountAlerts
			}
		}

		internal sealed class PlayingSessionStateCallback : CallbackMsg {
			internal readonly bool PlayingBlocked;

			internal PlayingSessionStateCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientPlayingSessionState msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				PlayingBlocked = msg.playing_blocked;
			}
		}

		internal sealed class RedeemGuestPassResponseCallback : CallbackMsg {
			internal readonly EResult Result;

			internal RedeemGuestPassResponseCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientRedeemGuestPassResponse msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				Result = (EResult) msg.eresult;
			}
		}

		internal sealed class SharedLibraryLockStatusCallback : CallbackMsg {
			internal readonly ulong LibraryLockedBySteamID;

			internal SharedLibraryLockStatusCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientSharedLibraryLockStatus msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;

				if (msg.own_library_locked_by == 0) {
					return;
				}

				LibraryLockedBySteamID = new SteamID(msg.own_library_locked_by, EUniverse.Public, EAccountType.Individual);
			}
		}

		internal sealed class VanityURLChangedCallback : CallbackMsg {
			internal readonly string VanityURL;

			internal VanityURLChangedCallback([JetBrains.Annotations.NotNull] JobID jobID, [JetBrains.Annotations.NotNull] CMsgClientVanityURLChangedNotification msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				VanityURL = msg.vanity_url;
			}
		}
	}
}
