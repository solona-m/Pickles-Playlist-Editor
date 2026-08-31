using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    internal static class PenumbraApi
    {
        // A reload is not a ping: Penumbra re-reads every group file for the mod on the framework
        // thread before it answers, so this has to allow for a large mod on a busy frame. The old
        // 500ms constant here was never actually in effect — it was set on a client nothing used,
        // while requests went through one left on HttpClient's 100s default.
        //
        // Kept modest because the caller holds the mod-folder gate for the whole call: this value is
        // the worst case an edit can wait behind an unresponsive Penumbra.
        private const int TIMEOUT_MS = 5_000;

        // "Penumbra isn't running" is a normal state for someone editing with the game closed, and it
        // recurs on every debounced reload. Report it once per session instead of once per save, so a
        // genuine API error stays visible in the log rather than buried in identical noise.
        private static int s_offlineReported;

        // Separate from the offline flag so a busy Penumbra and a closed one are each reported once,
        // rather than the first to occur silencing the other.
        private static int s_timeoutReported;

        // Penumbra 1.7 split its single config file into a folder, so both locations have to be
        // tried — reading only the old one meant every 1.7 user with no saved path got "" and had to
        // find their own mod folder by hand.
        //
        // Newest first, and deliberately not "first file that exists": a machine that has been
        // through the move still has the old Penumbra.json sitting there, so preferring it would
        // hand back whichever mod directory was configured before the upgrade.
        private static readonly string[] s_configPaths =
        {
            Path.Combine("Penumbra", "config", "penumbra.json"),   // Penumbra 1.7 and later
            "Penumbra.json",                                       // earlier releases
        };

        /// <summary>
        /// The mod directory Penumbra is configured with, or "" when it cannot be read. Only used to
        /// guess a folder for a user who has never set one, so an empty answer costs a prompt rather
        /// than a failure.
        /// </summary>
        public static string GetPenumbraDirectory()
        {
            foreach (string relative in s_configPaths)
            {
                string path = Path.Combine(PenumbraInstall.DalamudRoot, "pluginConfigs", relative);
                if (!File.Exists(path))
                    continue;

                try
                {
                    string? modDirectory = (string?)JObject.Parse(File.ReadAllText(path))["ModDirectory"];
                    if (!string.IsNullOrWhiteSpace(modDirectory))
                        return modDirectory;
                }
                catch (Exception ex)
                {
                    // Keep looking rather than give up: a damaged config in one layout says nothing
                    // about whether the other one is readable.
                    Logger.LogWarn("Could not read Penumbra's config at '{Path}': {Error}", path, ex.Message);
                }
            }

            return "";
        }

        /// <summary>
        /// How a call to Penumbra's local API ended.
        ///
        /// "No answer" and "answered with an error" are not the same event and must not collapse into
        /// one bool. The first is the ordinary state of editing with the game closed, or of Penumbra
        /// being mid-zone-load, and it repeats on every debounced reload; reporting it like a fault
        /// buries the genuinely actionable case under one line of noise per save.
        ///
        /// Note what this canNOT tell you: whether Penumbra actually knows the mod. Its HTTP handler
        /// answers 200 with an empty body whether or not the mod is registered — verified against
        /// 1.7 by reloading a name that exists nowhere — so the ModMissing code its IPC layer returns
        /// never reaches us. <see cref="Failed"/> means a non-2xx or a transport error, never
        /// "unknown mod".
        /// </summary>
        internal enum ApiResult
        {
            Success,

            /// <summary>No usable answer: Penumbra is closed, its HTTP API is off, or it did not
            /// respond in time (its handler runs on the game's framework thread, which stalls for
            /// seconds during zone loads). Reported once per session per cause by
            /// <see cref="Request"/>; callers should stay quiet about it.</summary>
            NoAnswer,

            /// <summary>Penumbra answered and the call did not succeed. Always worth surfacing.</summary>
            Failed,
        }

        /// <summary>
        /// Calls /reloadmod on the Penumbra API.
        /// </summary>
        public static async Task<ApiResult> ReloadMod(string path, string name = null)
        {
            Dictionary<string, string> args = new Dictionary<string, string>();

            if (name != null)
            {
                args.Add("Name", name);
            }
            if (path != null)
            {
                args.Add("Path", path);
            }

            return await Request("/reloadmod", args).ConfigureAwait(false);
        }

        private static readonly HttpClient _Client = new()
        {
            BaseAddress = new System.Uri("http://localhost:42069"),
            Timeout = TimeSpan.FromMilliseconds(TIMEOUT_MS),
        };

        /// <summary>
        /// Posts to Penumbra's local HTTP API, reporting how the call ended.
        ///
        /// Every failure used to be swallowed and returned as a bare false that no caller looked at,
        /// so a log full of "reloading mod ..." lines was no evidence that any reload had happened —
        /// a timeout, a refused connection and a rejection were indistinguishable from success.
        ///
        /// What this still cannot tell you is whether Penumbra knows the mod: its handler answers 200
        /// with an empty body either way (verified against 1.7 by reloading a name that exists
        /// nowhere), so the ModMissing code its IPC layer returns never reaches HTTP callers.
        /// </summary>
        private static async Task<ApiResult> Request(string urlPath, object data = null)
        {
            data ??= new object();
            try
            {
                using StringContent jsonContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

                // ConfigureAwait(false) everywhere in this file: callers block on these tasks, and
                // resuming on a captured UI SynchronizationContext would deadlock the thread that is
                // waiting. Task.Run at the call site already removes the context, but this must not
                // depend on every future caller remembering to do that.
                using HttpResponseMessage response =
                    await _Client.PostAsync("api/" + urlPath, jsonContent).ConfigureAwait(false);

                // Reached Penumbra and it answered, so any outage or stall is over: re-arm both
                // notices so the next one is reported rather than silently swallowed.
                Interlocked.Exchange(ref s_offlineReported, 0);
                Interlocked.Exchange(ref s_timeoutReported, 0);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarn("Penumbra API {Path} returned {Code} ({Reason}).",
                        urlPath, (int)response.StatusCode, response.ReasonPhrase);
                    return ApiResult.Failed;
                }

                return ApiResult.Success;
            }
            catch (TaskCanceledException)
            {
                // Penumbra accepted the connection but did not answer in time. Its handler runs on the
                // game's framework thread, which stalls for seconds during a zone load — so this is a
                // transient "not now", not a fault, and warning per save would recreate exactly the
                // noise the offline suppression exists to remove.
                if (Interlocked.Exchange(ref s_timeoutReported, 1) == 0)
                    Logger.LogWarn("Penumbra API {Path} did not answer within {Timeout}ms — it is " +
                        "running but busy (this is normal during a zone load). The edit is saved to " +
                        "disk regardless. Further timeouts this session will not be logged.",
                        urlPath, TIMEOUT_MS);
                return ApiResult.NoAnswer;
            }
            catch (HttpRequestException ex)
            {
                // Connection refused: Penumbra is closed or its HTTP API is off. Expected, and it
                // repeats on every debounced reload — so say it once, not once per save.
                if (Interlocked.Exchange(ref s_offlineReported, 1) == 0)
                    Logger.LogInfo("Penumbra API unreachable ({Error}) — is Penumbra running with its " +
                        "HTTP API enabled? Edits are still saved to disk; this is only about the live " +
                        "reload. Further occurrences this session will not be logged.", ex.Message);
                return ApiResult.NoAnswer;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Penumbra API {Path} failed: {Error}", urlPath, ex.Message);
                return ApiResult.Failed;
            }
        }
    }
}
