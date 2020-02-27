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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace ArchiSteamFarm {
	[SuppressMessage("ReSharper", "ClassCannotBeInstantiated")]
	public sealed class GlobalConfig {
		internal const byte DefaultConnectionTimeout = 90;
		internal const byte DefaultLoginLimiterDelay = 10;

		private const bool DefaultAutoRestart = true;
		private const string DefaultCommandPrefix = "!";
		private const byte DefaultConfirmationsLimiterDelay = 10;
		private const string DefaultCurrentCulture = null;
		private const bool DefaultDebug = false;
		private const byte DefaultFarmingDelay = 15;
		private const byte DefaultGiftsLimiterDelay = 1;
		private const bool DefaultHeadless = false;
		private const byte DefaultIdleFarmingPeriod = 8;
		private const byte DefaultInventoryLimiterDelay = 3;
		private const bool DefaultIPC = false;
		private const string DefaultIPCPassword = null;
		private const byte DefaultMaxFarmingTime = 10;
		private const byte DefaultMaxTradeHoldDuration = 15;
		private const EOptimizationMode DefaultOptimizationMode = EOptimizationMode.MaxPerformance;
		private const bool DefaultStatistics = true;
		private const string DefaultSteamMessagePrefix = "/me ";
		private const ulong DefaultSteamOwnerID = 0;
		private const ProtocolTypes DefaultSteamProtocols = ProtocolTypes.All;
		private const EUpdateChannel DefaultUpdateChannel = EUpdateChannel.Stable;
		private const byte DefaultUpdatePeriod = 24;
		private const ushort DefaultWebLimiterDelay = 300;
		private const string DefaultWebProxyPassword = null;
		private const string DefaultWebProxyText = null;
		private const string DefaultWebProxyUsername = null;

		private static readonly ImmutableHashSet<uint> DefaultBlacklist = ImmutableHashSet.Create<uint>();
		private static readonly SemaphoreSlim WriteSemaphore = new SemaphoreSlim(1, 1);

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly bool AutoRestart = DefaultAutoRestart;

		[JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace, Required = Required.DisallowNull)]
		public readonly ImmutableHashSet<uint> Blacklist = DefaultBlacklist;

		[JsonProperty]
		public readonly string CommandPrefix = DefaultCommandPrefix;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte ConfirmationsLimiterDelay = DefaultConfirmationsLimiterDelay;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte ConnectionTimeout = DefaultConnectionTimeout;

		[JsonProperty]
		public readonly string CurrentCulture = DefaultCurrentCulture;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly bool Debug = DefaultDebug;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte FarmingDelay = DefaultFarmingDelay;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte GiftsLimiterDelay = DefaultGiftsLimiterDelay;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly bool Headless = DefaultHeadless;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte IdleFarmingPeriod = DefaultIdleFarmingPeriod;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte InventoryLimiterDelay = DefaultInventoryLimiterDelay;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly bool IPC = DefaultIPC;

		[JsonProperty]
		public readonly string IPCPassword = DefaultIPCPassword;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte LoginLimiterDelay = DefaultLoginLimiterDelay;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte MaxFarmingTime = DefaultMaxFarmingTime;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte MaxTradeHoldDuration = DefaultMaxTradeHoldDuration;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly EOptimizationMode OptimizationMode = DefaultOptimizationMode;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly bool Statistics = DefaultStatistics;

		[JsonProperty]
		public readonly string SteamMessagePrefix = DefaultSteamMessagePrefix;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly EUpdateChannel UpdateChannel = DefaultUpdateChannel;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly byte UpdatePeriod = DefaultUpdatePeriod;

		[JsonProperty(Required = Required.DisallowNull)]
		public readonly ushort WebLimiterDelay = DefaultWebLimiterDelay;

		[JsonProperty(PropertyName = nameof(WebProxy))]
		public readonly string WebProxyText = DefaultWebProxyText;

		[JsonProperty]
		public readonly string WebProxyUsername = DefaultWebProxyUsername;

		[JsonIgnore]
		[PublicAPI]
		public WebProxy WebProxy {
			get {
				if (BackingWebProxy != null) {
					return BackingWebProxy;
				}

				if (string.IsNullOrEmpty(WebProxyText)) {
					return null;
				}

				Uri uri;

				try {
					uri = new Uri(WebProxyText);
				} catch (UriFormatException e) {
					ASF.ArchiLogger.LogGenericException(e);

					return null;
				}

				WebProxy proxy = new WebProxy {
					Address = uri,
					BypassProxyOnLocal = true
				};

				if (!string.IsNullOrEmpty(WebProxyUsername) || !string.IsNullOrEmpty(WebProxyPassword)) {
					NetworkCredential credentials = new NetworkCredential();

					if (!string.IsNullOrEmpty(WebProxyUsername)) {
						credentials.UserName = WebProxyUsername;
					}

					if (!string.IsNullOrEmpty(WebProxyPassword)) {
						credentials.Password = WebProxyPassword;
					}

					proxy.Credentials = credentials;
				}

				BackingWebProxy = proxy;

				return proxy;
			}
		}

		[JsonProperty(Required = Required.DisallowNull)]
		public ulong SteamOwnerID { get; private set; } = DefaultSteamOwnerID;

		[JsonProperty(Required = Required.DisallowNull)]
		public ProtocolTypes SteamProtocols { get; private set; } = DefaultSteamProtocols;

		[JsonExtensionData]
		internal Dictionary<string, JToken> AdditionalProperties { get; set; }

		internal bool IsWebProxyPasswordSet { get; private set; }
		internal bool ShouldSerializeEverything { private get; set; } = true;
		internal bool ShouldSerializeHelperProperties { private get; set; } = true;

		[JsonProperty]
		internal string WebProxyPassword {
			get => BackingWebProxyPassword;

			set {
				IsWebProxyPasswordSet = true;
				BackingWebProxyPassword = value;
			}
		}

		private WebProxy BackingWebProxy;
		private string BackingWebProxyPassword = DefaultWebProxyPassword;
		private bool ShouldSerializeSensitiveDetails = true;

		[JsonProperty(PropertyName = SharedInfo.UlongCompatibilityStringPrefix + nameof(SteamOwnerID), Required = Required.DisallowNull)]
		[JetBrains.Annotations.NotNull]
		private string SSteamOwnerID {
			get => SteamOwnerID.ToString();

			set {
				if (string.IsNullOrEmpty(value) || !ulong.TryParse(value, out ulong result)) {
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(SSteamOwnerID)));

					return;
				}

				SteamOwnerID = result;
			}
		}

		[JsonConstructor]
		private GlobalConfig() { }

		internal (bool Valid, string ErrorMessage) CheckValidation() {
			if (ConnectionTimeout == 0) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(ConnectionTimeout), ConnectionTimeout));
			}

			if (FarmingDelay == 0) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(FarmingDelay), FarmingDelay));
			}

			if (MaxFarmingTime == 0) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(MaxFarmingTime), MaxFarmingTime));
			}

			if (!Enum.IsDefined(typeof(EOptimizationMode), OptimizationMode)) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(OptimizationMode), OptimizationMode));
			}

			if (!string.IsNullOrEmpty(SteamMessagePrefix) && (SteamMessagePrefix.Length > Bot.MaxMessagePrefixLength)) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(SteamMessagePrefix), SteamMessagePrefix));
			}

			if ((SteamOwnerID != 0) && !new SteamID(SteamOwnerID).IsIndividualAccount) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(SteamOwnerID), SteamOwnerID));
			}

			if ((SteamProtocols <= 0) || (SteamProtocols > ProtocolTypes.All)) {
				return (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(SteamProtocols), SteamProtocols));
			}

			return Enum.IsDefined(typeof(EUpdateChannel), UpdateChannel) ? (true, null) : (false, string.Format(Strings.ErrorConfigPropertyInvalid, nameof(UpdateChannel), UpdateChannel));
		}

		[JetBrains.Annotations.NotNull]
		internal static GlobalConfig Create() =>
			new GlobalConfig {
				ShouldSerializeEverything = false,
				ShouldSerializeSensitiveDetails = false
			};

		[ItemCanBeNull]
		internal static async Task<GlobalConfig> Load(string filePath) {
			if (string.IsNullOrEmpty(filePath)) {
				ASF.ArchiLogger.LogNullError(nameof(filePath));

				return null;
			}

			if (!File.Exists(filePath)) {
				return null;
			}

			GlobalConfig globalConfig;

			try {
				string json = await RuntimeCompatibility.File.ReadAllTextAsync(filePath).ConfigureAwait(false);

				if (string.IsNullOrEmpty(json)) {
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsEmpty, nameof(json)));

					return null;
				}

				globalConfig = JsonConvert.DeserializeObject<GlobalConfig>(json);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return null;
			}

			if (globalConfig == null) {
				ASF.ArchiLogger.LogNullError(nameof(globalConfig));

				return null;
			}

			(bool valid, string errorMessage) = globalConfig.CheckValidation();

			if (!valid) {
				ASF.ArchiLogger.LogGenericError(errorMessage);

				return null;
			}

			globalConfig.ShouldSerializeEverything = false;
			globalConfig.ShouldSerializeSensitiveDetails = false;

			return globalConfig;
		}

		internal static async Task<bool> Write(string filePath, GlobalConfig globalConfig) {
			if (string.IsNullOrEmpty(filePath) || (globalConfig == null)) {
				ASF.ArchiLogger.LogNullError(nameof(filePath) + " || " + nameof(globalConfig));

				return false;
			}

			string json = JsonConvert.SerializeObject(globalConfig, Formatting.Indented);
			string newFilePath = filePath + ".new";

			await WriteSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				await RuntimeCompatibility.File.WriteAllTextAsync(newFilePath, json).ConfigureAwait(false);

				if (File.Exists(filePath)) {
					File.Replace(newFilePath, filePath, null);
				} else {
					File.Move(newFilePath, filePath);
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return false;
			} finally {
				WriteSemaphore.Release();
			}

			return true;
		}

		public enum EOptimizationMode : byte {
			MaxPerformance,
			MinMemoryUsage
		}

		public enum EUpdateChannel : byte {
			None,
			Stable,

			[PublicAPI]
			Experimental
		}

		// ReSharper disable UnusedMember.Global
		public bool ShouldSerializeAutoRestart() => ShouldSerializeEverything || (AutoRestart != DefaultAutoRestart);
		public bool ShouldSerializeBlacklist() => ShouldSerializeEverything || ((Blacklist != DefaultBlacklist) && !Blacklist.SetEquals(DefaultBlacklist));
		public bool ShouldSerializeCommandPrefix() => ShouldSerializeEverything || (CommandPrefix != DefaultCommandPrefix);
		public bool ShouldSerializeConfirmationsLimiterDelay() => ShouldSerializeEverything || (ConfirmationsLimiterDelay != DefaultConfirmationsLimiterDelay);
		public bool ShouldSerializeConnectionTimeout() => ShouldSerializeEverything || (ConnectionTimeout != DefaultConnectionTimeout);
		public bool ShouldSerializeCurrentCulture() => ShouldSerializeEverything || (CurrentCulture != DefaultCurrentCulture);
		public bool ShouldSerializeDebug() => ShouldSerializeEverything || (Debug != DefaultDebug);
		public bool ShouldSerializeFarmingDelay() => ShouldSerializeEverything || (FarmingDelay != DefaultFarmingDelay);
		public bool ShouldSerializeGiftsLimiterDelay() => ShouldSerializeEverything || (GiftsLimiterDelay != DefaultGiftsLimiterDelay);
		public bool ShouldSerializeHeadless() => ShouldSerializeEverything || (Headless != DefaultHeadless);
		public bool ShouldSerializeIdleFarmingPeriod() => ShouldSerializeEverything || (IdleFarmingPeriod != DefaultIdleFarmingPeriod);
		public bool ShouldSerializeInventoryLimiterDelay() => ShouldSerializeEverything || (InventoryLimiterDelay != DefaultInventoryLimiterDelay);
		public bool ShouldSerializeIPC() => ShouldSerializeEverything || (IPC != DefaultIPC);
		public bool ShouldSerializeIPCPassword() => ShouldSerializeEverything || (IPCPassword != DefaultIPCPassword);
		public bool ShouldSerializeLoginLimiterDelay() => ShouldSerializeEverything || (LoginLimiterDelay != DefaultLoginLimiterDelay);
		public bool ShouldSerializeMaxFarmingTime() => ShouldSerializeEverything || (MaxFarmingTime != DefaultMaxFarmingTime);
		public bool ShouldSerializeMaxTradeHoldDuration() => ShouldSerializeEverything || (MaxTradeHoldDuration != DefaultMaxTradeHoldDuration);
		public bool ShouldSerializeOptimizationMode() => ShouldSerializeEverything || (OptimizationMode != DefaultOptimizationMode);
		public bool ShouldSerializeSSteamOwnerID() => ShouldSerializeEverything || (ShouldSerializeHelperProperties && (SteamOwnerID != DefaultSteamOwnerID));
		public bool ShouldSerializeStatistics() => ShouldSerializeEverything || (Statistics != DefaultStatistics);
		public bool ShouldSerializeSteamMessagePrefix() => ShouldSerializeEverything || (SteamMessagePrefix != DefaultSteamMessagePrefix);
		public bool ShouldSerializeSteamOwnerID() => ShouldSerializeEverything || (SteamOwnerID != DefaultSteamOwnerID);
		public bool ShouldSerializeSteamProtocols() => ShouldSerializeEverything || (SteamProtocols != DefaultSteamProtocols);
		public bool ShouldSerializeUpdateChannel() => ShouldSerializeEverything || (UpdateChannel != DefaultUpdateChannel);
		public bool ShouldSerializeUpdatePeriod() => ShouldSerializeEverything || (UpdatePeriod != DefaultUpdatePeriod);
		public bool ShouldSerializeWebLimiterDelay() => ShouldSerializeEverything || (WebLimiterDelay != DefaultWebLimiterDelay);
		public bool ShouldSerializeWebProxyPassword() => ShouldSerializeSensitiveDetails && (ShouldSerializeEverything || (WebProxyPassword != DefaultWebProxyPassword));
		public bool ShouldSerializeWebProxyText() => ShouldSerializeEverything || (WebProxyText != DefaultWebProxyText);
		public bool ShouldSerializeWebProxyUsername() => ShouldSerializeEverything || (WebProxyUsername != DefaultWebProxyUsername);

		// ReSharper restore UnusedMember.Global
	}
}
