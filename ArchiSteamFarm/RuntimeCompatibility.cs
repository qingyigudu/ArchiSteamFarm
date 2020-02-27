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
using System.Diagnostics;
using System.Threading.Tasks;
using JetBrains.Annotations;

#if NETFRAMEWORK
using Microsoft.AspNetCore.Hosting;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
#endif

namespace ArchiSteamFarm {
	[PublicAPI]
	public static class RuntimeCompatibility {
#if NETFRAMEWORK
		private static readonly DateTime SavedProcessStartTime = DateTime.UtcNow;
#endif

		public static bool IsRunningOnMono => Type.GetType("Mono.Runtime") != null;

		public static DateTime ProcessStartTime {
			get {
#if NETFRAMEWORK
				if (IsRunningOnMono) {
					return SavedProcessStartTime;
				}
#endif

				using Process process = Process.GetCurrentProcess();

				return process.StartTime;
			}
		}

#pragma warning disable 1998
		[PublicAPI]
		public static class File {
			public static async Task AppendAllTextAsync([NotNull] string path, string contents) =>
#if NETFRAMEWORK
				System.IO.File.AppendAllText(path, contents);
#else
				await System.IO.File.AppendAllTextAsync(path, contents).ConfigureAwait(false);
#endif

			[ItemNotNull]
			public static async Task<byte[]> ReadAllBytesAsync([NotNull] string path) =>
#if NETFRAMEWORK
				System.IO.File.ReadAllBytes(path);
#else
				await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
#endif

			[ItemNotNull]
			public static async Task<string> ReadAllTextAsync([NotNull] string path) =>
#if NETFRAMEWORK
				System.IO.File.ReadAllText(path);
#else
				await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false);
#endif

			public static async Task WriteAllTextAsync([NotNull] string path, string contents) =>
#if NETFRAMEWORK
				System.IO.File.WriteAllText(path, contents);
#else
				await System.IO.File.WriteAllTextAsync(path, contents).ConfigureAwait(false);
#endif
		}
#pragma warning restore 1998

		[PublicAPI]
		public static class HashCode {
			public static int Combine<T1, T2, T3>(T1 value1, T2 value2, T3 value3) =>
#if NETFRAMEWORK
				(value1, value2, value3).GetHashCode();
#else
				System.HashCode.Combine(value1, value2, value3);
#endif
		}

		[PublicAPI]
		public static class Path {
			[NotNull]
			public static string GetRelativePath([NotNull] string relativeTo, [NotNull] string path) {
#if NETFRAMEWORK
				if (!path.StartsWith(relativeTo, StringComparison.Ordinal)) {
					throw new NotImplementedException();
				}

				string result = path.Substring(relativeTo.Length);

				return (result[0] == System.IO.Path.DirectorySeparatorChar) || (result[0] == System.IO.Path.AltDirectorySeparatorChar) ? result.Substring(1) : result;
#else
#pragma warning disable IDE0022
				return System.IO.Path.GetRelativePath(relativeTo, path);
#pragma warning restore IDE0022
#endif
			}
		}

#if NETFRAMEWORK
		[NotNull]
		internal static IWebHostBuilder ConfigureWebHostDefaults([NotNull] this IWebHostBuilder builder, [NotNull] Action<IWebHostBuilder> configure) {
			configure(builder);

			return builder;
		}

		public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value) {
			key = kv.Key;
			value = kv.Value;
		}

		public static async Task<WebSocketReceiveResult> ReceiveAsync([NotNull] this WebSocket webSocket, [NotNull] byte[] buffer, CancellationToken cancellationToken) => await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
		public static async Task SendAsync([NotNull] this WebSocket webSocket, [NotNull] byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => await webSocket.SendAsync(new ArraySegment<byte>(buffer), messageType, endOfMessage, cancellationToken).ConfigureAwait(false);

		[NotNull]
		public static string[] Split([NotNull] this string text, char separator, StringSplitOptions options = StringSplitOptions.None) => text.Split(new[] { separator }, options);

		public static void TrimExcess<TKey, TValue>(this Dictionary<TKey, TValue> _) { } // no-op
#endif
	}
}
