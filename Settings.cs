using Microsoft.Win32;
using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor
{
    public static class Settings
    {
        private static string s_valueName = "PenumbraPath";
        private static string s_subKey = @"SOFTWARE\ScdConverter";
        private static string[] s_defaultModNames = {
            "Gimme Pickle's DJ Muzik, Movez, and VFX",
            "DAMThunderdome.exe",
            "[yue's + lu's] dj",
            "[Yue & Lu's] Mega Music Mod",
        };
        // Initialized with three dummy keys/values
        private static Dictionary<string, string> s_defaultBaselineScdKey = new Dictionary<string, string>
        {
            { s_defaultModNames[0], "sound/bpmloop.scd" },
            { s_defaultModNames[1], "sound/dam.scd" },
            { s_defaultModNames[2], "sound/lolo.scd" },
            { s_defaultModNames[3], "sound/lolo.scd" }
        };
            
        public static string[] SupportedFileTypes = new string[] { ".ogg", ".wav", ".mp3", ".m4a", ".flac", ".scd" };

        public static string PenumbraLocation
        {
            get
            {
                // Read the value from the registry
                string retval = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(s_valueName, null);
                if (string.IsNullOrWhiteSpace(retval))
                {
                    retval = PenumbraApi.GetPenumbraDirectory();
                    if (!string.IsNullOrWhiteSpace(retval))
                    {
                        PenumbraLocation = retval; // save it for next time
                    }
                }
                return retval;
            }
            set
            {
                // Half of what ModRoot is built from, so on a real change the folder the guard holds
                // counts for is no longer the folder those counts came from. Only on a real change,
                // though: the Settings dialog writes this on every OK whether or not the path was
                // touched, and forgetting unconditionally would disarm the guard for anyone who
                // opened Settings to move the volume slider.
                string current = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(s_valueName, null);
                if (!string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                    ModFolderGuard.Forget();

                // Specify the registry key and value

                // Open or create the registry key
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        // Write the value
                        key.SetValue(s_valueName, value);
                    }
                }
            }
        }
        // Remembered for the process only — see the getter for why this is never persisted.
        private static string? s_guessedModName;

        private static string GuessModName(string name, string how)
        {
            s_guessedModName = name;
            Logger.LogWarn("Settings: no mod folder is configured — guessing '{Mod}' via {How}. " +
                "If that is the wrong mod, set it in Settings; playlists will look missing until you do.",
                name, how);
            return name;
        }

        public static string ModName
        {
            get
            {
                string retval = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ModName");
                if (!string.IsNullOrWhiteSpace(retval))
                    return retval;

                // No configured mod. Everything below is a GUESS, and it decides which folder every
                // read, write, repair and delete in this app targets — so it is cached in memory and
                // logged, never written to the registry. Persisting it used to make a guess
                // indistinguishable from a deliberate choice on the next run, which is a bad state to
                // be in when the wrong answer means editing (or repairing) somebody else's mod.
                if (s_guessedModName != null)
                    return s_guessedModName;

                string penumbra = PenumbraLocation;
                if (!string.IsNullOrWhiteSpace(penumbra))
                {
                    foreach (string defaultName in s_defaultModNames)
                    {
                        string potentialPath = System.IO.Path.Combine(penumbra, defaultName);
                        if (System.IO.Directory.Exists(potentialPath))
                            return GuessModName(defaultName, "a built-in default name");
                    }

                    // Fall back: search for any directory containing "[yue & lu's]"
                    try
                    {
                        foreach (string dir in System.IO.Directory.EnumerateDirectories(penumbra))
                        {
                            string name = System.IO.Path.GetFileName(dir);
                            if (name.Contains("[yue & lu's]", StringComparison.OrdinalIgnoreCase))
                                return GuessModName(name, "a folder-name search");
                        }
                    }
                    catch { }
                }
                return retval;
            }
            set
            {
                string previous = s_guessedModName
                    ?? (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ModName");

                // The mod folder decides where every playlist read and write lands, so a change of it
                // is worth a line in the log: a report of "my playlists vanished" is otherwise
                // indistinguishable from "I am pointed at a different mod than I think".
                Logger.LogInfo("Settings: mod folder set to '{Mod}' (was '{Old}').",
                    value, previous ?? "<unset>");
                s_guessedModName = null;

                // The other half of ModRoot. Playlist counts held for the old mod say nothing about
                // the new one, and a baseline kept across the switch would be compared again the next
                // time the app is pointed back — by which point it can be arbitrarily old. Guarded on
                // a real change for the same reason as PenumbraLocation: the Settings dialog rewrites
                // both halves on every OK. A name that was only ever a guess counts as unchanged when
                // the user confirms it, because the folder it resolves to is the same one.
                if (!string.Equals(previous, value, StringComparison.OrdinalIgnoreCase))
                    ModFolderGuard.Forget();

                // Open or create the registry key
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        // Write the value
                        key.SetValue("ModName", value);
                    }
                }
            }
        }

        public static string BaselineScdKey
        {
            get
            {
                string key = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("BaselineScdKey");
                try
                {
                    if (string.IsNullOrWhiteSpace(ModName))
                        return string.Empty;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        if (s_defaultBaselineScdKey.TryGetValue(ModName, out var defaultKey))
                        {
                            BaselineScdKey = defaultKey; // save it for next time
                            return defaultKey;
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(ModName) && ModName.Contains("[yue & lu's]", StringComparison.OrdinalIgnoreCase))
                            {
                                return "sound/lolo.scd";
                            }
                            return s_defaultBaselineScdKey[s_defaultModNames[0]]; // fallback to first default if mod name is unrecognized, but don't save it
                        }
                    }
                }
                catch {}
                return key;
            }
            set
            {
                string normalized = value.Trim();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("BaselineScdKey", normalized);
                    }
                }
            }
        }


        /// <summary>
        /// Controls whether converted audio should be loudness-normalized.
        /// Default: true.
        /// Stored as integer 1 (true) or 0 (false) under the same registry subkey.
        /// </summary>
        public static bool NormalizeVolume
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("NormalizeVolume", 0);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch
                {
                    // fallthrough to default
                }
                return false;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("NormalizeVolume", value ? 1 : 0);
                    }
                }
            }
        }

        public static bool FadeBackgroundMusic
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("FadeBackgroundMusic", 1);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch { }
                return true;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                        key.SetValue("FadeBackgroundMusic", value ? 1 : 0);
                }
            }
        }

        /// <summary>
        /// Controls whether songs loop when they finish playing.
        /// Default: false.
        /// Stored as integer 1 (true) or 0 (false) under the same registry subkey.
        /// </summary>
        public static bool LoopSongs
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("LoopSongs", 0);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch
                {
                    // fallthrough to default
                }
                return false;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("LoopSongs", value ? 1 : 0);
                    }
                }
            }
        }

        public static bool ScdVersionShift
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ScdVersionShift", 1);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch
                {
                    // fallthrough to default
                }
                return true;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("ScdVersionShift", value ? 1 : 0);
                    }
                }
            }
        }

        /// <summary>
        /// Controls whether music fades out with distance from the sound source.
        /// Default: false.
        /// Stored as integer 1 (true) or 0 (false) under the same registry subkey.
        /// </summary>
        public static bool FadeWithDistance
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("FadeWithDistance", 0);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch
                {
                    // fallthrough to default
                }
                return false;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("FadeWithDistance", value ? 1 : 0);
                    }
                }
            }
        }

        /// <summary>
        /// The mod folder holding the DJ's dances, which is usually NOT <see cref="ModName"/>.
        ///
        /// A DJ setup is normally split in two: a music mod full of .scd, which is what the rest of
        /// this app edits, and a dance/VFX mod holding the .pap animations and the effects they fire.
        /// Some DJs run one combined mod instead, in which case this is the same folder as
        /// <see cref="ModName"/> — so the two settings are independent rather than one implying
        /// anything about the other.
        ///
        /// Empty until the user picks one; the dances dialog offers a ranked list on first use.
        /// </summary>
        public static string DanceModName
        {
            get => (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("DanceModName") ?? string.Empty;
            set
            {
                // Worth a log line for the same reason ModName's setter is: "my dances vanished" and
                // "I am pointed at a different mod than I think" look identical from a bug report.
                Logger.LogInfo("Settings: dance mod set to '{Mod}' (was '{Old}').",
                    value, DanceModName.Length == 0 ? "<unset>" : DanceModName);

                using RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("DanceModName", value ?? string.Empty);
            }
        }

        /// <summary>
        /// The option group inside <see cref="DanceModName"/> that holds the dances.
        ///
        /// Remembered by id because a group can be renamed, and matching on the name alone would
        /// silently start editing a different group the day somebody does.
        /// </summary>
        public static string DanceGroupId
        {
            get => (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("DanceGroupId") ?? string.Empty;
            set
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("DanceGroupId", value ?? string.Empty);
            }
        }

        /// <summary>
        /// Controls whether the mod should be auto-reloaded (Penumbra) after changes.
        /// Default: true.
        /// Stored as integer 1 (true) or 0 (false) under the same registry subkey.
        /// </summary>
        public static bool AutoReloadMod
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("AutoReloadMod", 1);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && bool.TryParse(sv, out var bv)) return bv;
                    if (value is string sv2 && int.TryParse(sv2, out var parsed)) return parsed != 0;
                }
                catch
                {
                    // fallthrough to default
                }
                return true;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("AutoReloadMod", value ? 1 : 0);
                    }
                }
            }
        }

        /// <summary>
        /// Controls which audio bus the imported SCD plays through (16=BGM, 2=SFX, 3=Voice, etc.).
        /// Default: 16 (BGM). FadeWithDistance overrides this to bus 8 (positional) at import time.
        /// Stored as integer under the same registry subkey.
        /// </summary>
        public static int BusNumber
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("BusNumber", 16);
                    int[] valid = [16, 2, 3, 4, 5];
                    if (value is int iv) { if (System.Array.IndexOf(valid, iv) >= 0) return iv; }
                    else if (value is long lv) { int i = (int)lv; if (System.Array.IndexOf(valid, i) >= 0) return i; }
                    else if (value is string sv && int.TryParse(sv, out var bv)) { if (System.Array.IndexOf(valid, bv) >= 0) return bv; }
                }
                catch
                {
                    // fallthrough to default
                }
                return 16;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(s_subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("BusNumber", value, RegistryValueKind.DWord);
                    }
                }
            }
        }

        /// <summary>Value of <see cref="UpdateChannel"/> meaning "only main releases".</summary>
        public const string UpdateChannelStable = "stable";

        /// <summary>Value of <see cref="UpdateChannel"/> meaning "prereleases too".</summary>
        public const string UpdateChannelTesting = "testing";

        /// <summary>
        /// Which release feed the updater follows: <see cref="UpdateChannelStable"/> (GitHub
        /// releases only) or <see cref="UpdateChannelTesting"/> (prereleases included).
        ///
        /// With nothing stored, this follows the running build — a build that came from the
        /// testing pipeline keeps getting testing builds, a stable build never sees them.
        /// That is the behaviour the updater had before the setting existed, so an existing
        /// install's channel does not change underneath it the first time this ships.
        /// Anything else stored is treated as stable rather than thrown away, so a hand-edited
        /// registry value can never leave a user silently on prereleases.
        /// </summary>
        public static string UpdateChannel
        {
            get
            {
                try
                {
                    var stored = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("UpdateChannel") as string;
                    if (!string.IsNullOrWhiteSpace(stored))
                    {
                        return string.Equals(stored.Trim(), UpdateChannelTesting, StringComparison.OrdinalIgnoreCase)
                            ? UpdateChannelTesting
                            : UpdateChannelStable;
                    }
                }
                catch { }
                return AppVersion.IsPrerelease ? UpdateChannelTesting : UpdateChannelStable;
            }
            set
            {
                string normalized = string.Equals(value?.Trim(), UpdateChannelTesting, StringComparison.OrdinalIgnoreCase)
                    ? UpdateChannelTesting
                    : UpdateChannelStable;
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("UpdateChannel", normalized);
            }
        }

        /// <summary>True when the updater should offer prereleases (testing builds).</summary>
        public static bool FollowTestingReleases =>
            UpdateChannel == UpdateChannelTesting;

        /// <summary>
        /// Registry values that must never leave the registry. <see cref="SoundCloudToken"/> is a
        /// DPAPI-protected OAuth credential; copying it into a backup folder would put a credential
        /// somewhere the user is likely to zip up and share when asking for help, and it could not be
        /// decrypted after a Windows profile change anyway. Do not "fix" this omission.
        /// </summary>
        /// <remarks>
        /// Case-insensitive on purpose: registry value names are, so an ordinal match here would let
        /// a value stored as "soundcloudtoken" slip past the filter and serialize the credential.
        /// </remarks>
        private static readonly HashSet<string> NonExportableValueNames =
            new(StringComparer.OrdinalIgnoreCase) { SoundCloudTokenValueName };

        /// <summary>
        /// Every registry value under this app's key except the credentials in
        /// <see cref="NonExportableValueNames"/>, for <see cref="Utils.VersionBackup"/>'s settings
        /// export. Reads the raw values rather than the typed properties above so a setting added
        /// later is captured without touching this method.
        /// </summary>
        public static Dictionary<string, object?> ExportableValues()
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(s_subKey);
                if (key == null) return result;

                foreach (string name in key.GetValueNames())
                {
                    if (NonExportableValueNames.Contains(name)) continue;
                    result[name] = key.GetValue(name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Could not enumerate settings for the version backup: {Error}", ex.Message);
            }
            return result;
        }

        public static readonly string DefaultBackgroundImagePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PicklesPlaylistEditor", "current", "ui", "picklebackground.png");

        public static string BackgroundImagePath
        {
            get => (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("BackgroundImagePath", null)
                   ?? DefaultBackgroundImagePath;
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                if (string.IsNullOrEmpty(value) || value == DefaultBackgroundImagePath)
                    key?.DeleteValue("BackgroundImagePath", throwOnMissingValue: false);
                else
                    key?.SetValue("BackgroundImagePath", value);
            }
        }

        private const string SoundCloudTokenValueName = "SoundCloudToken";

        /// <summary>
        /// True when a SoundCloud token is on file. Lets the settings UI show sign-in
        /// state without decrypting, and without the token ever entering memory.
        /// </summary>
        public static bool HasSoundCloudToken =>
            !string.IsNullOrEmpty(Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(SoundCloudTokenValueName, "") as string);

        /// <summary>
        /// The SoundCloud `oauth_token` cookie value, encrypted at rest with Windows DPAPI
        /// under the current user. Empty string means signed out.
        ///
        /// Getting this returns the plaintext token, so treat the result as a credential:
        /// don't log it, and don't put it on a command line where other processes can read
        /// it. A DPAPI blob can't be decrypted after a Windows profile change, so a failure
        /// to unprotect is treated as "signed out" and the stale value is discarded rather
        /// than thrown to the caller.
        /// </summary>
        public static string SoundCloudToken
        {
            get
            {
                var stored = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(SoundCloudTokenValueName, "") as string;
                if (string.IsNullOrEmpty(stored))
                    return "";

                try
                {
                    byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        Convert.FromBase64String(stored), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(plain);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Stored SoundCloud sign-in could not be read and has been discarded; sign in again. {Error}", ex.Message);
                    try { SoundCloudToken = ""; } catch { }
                    return "";
                }
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                if (string.IsNullOrWhiteSpace(value))
                {
                    key?.DeleteValue(SoundCloudTokenValueName, throwOnMissingValue: false);
                    return;
                }

                byte[] blob = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(value.Trim()), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                key?.SetValue(SoundCloudTokenValueName, Convert.ToBase64String(blob));
            }
        }

        /// <summary>
        /// BCP-47 language tag overriding the UI language (e.g. "en-US", "zh-Hans").
        /// Empty string means follow the system language. Default: "".
        /// </summary>
        public static string Language
        {
            get => (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("Language", "") ?? "";
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                if (string.IsNullOrWhiteSpace(value))
                    key?.DeleteValue("Language", throwOnMissingValue: false);
                else
                    key?.SetValue("Language", value.Trim());
            }
        }

        /// <summary>
        /// Volume percentage applied to SCD output (1–100).
        /// Default: 100.
        /// </summary>
        public static int ScdVolumePercentage
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ScdVolumePercentage", 100);
                    if (value is int iv && iv >= 1 && iv <= 100) return iv;
                    if (value is long lv && lv >= 1 && lv <= 100) return (int)lv;
                    if (value is string sv && int.TryParse(sv, out var parsed) && parsed >= 1 && parsed <= 100) return parsed;
                }
                catch { }
                return 100;
            }
            set
            {
                int clamped = Math.Clamp(value, 1, 100);
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("ScdVolumePercentage", clamped, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Loudness target for volume normalization as a 1–100 slider value
        /// (1 = quiet, 100 = loudest). Maps to a LUFS target via
        /// <see cref="NormalizationLoudnessLufs"/>. Default: 95.
        /// </summary>
        public static int NormalizationLoudness
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("NormalizationLoudness", 95);
                    if (value is int iv && iv >= 1 && iv <= 100) return iv;
                    if (value is long lv && lv >= 1 && lv <= 100) return (int)lv;
                    if (value is string sv && int.TryParse(sv, out var parsed) && parsed >= 1 && parsed <= 100) return parsed;
                }
                catch { }
                return 95;
            }
            set
            {
                int clamped = Math.Clamp(value, 1, 100);
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("NormalizationLoudness", clamped, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// The configured loudness target expressed in LUFS for FFmpeg's loudnorm
        /// filter. Maps the 1–100 <see cref="NormalizationLoudness"/> slider linearly
        /// onto -24 LUFS (quiet) … -5 LUFS (loudest, loudnorm's maximum).
        /// </summary>
        public static double NormalizationLoudnessLufs
            => -24.0 + (NormalizationLoudness - 1) * 19.0 / 99.0;

        /// <summary>
        /// The true-peak ceiling (dBTP) passed to FFmpeg's loudnorm filter. Scales
        /// with the <see cref="NormalizationLoudness"/> slider so quieter targets keep
        /// a comfortable -1.5 dB of headroom while the loudest setting pushes the
        /// ceiling up to -0.3 dBTP (effectively maximizing peak level like Audacity's
        /// Normalize + Amplify), leaving just enough margin to avoid clipping.
        /// </summary>
        public static double NormalizationTruePeak
            => -1.5 + (NormalizationLoudness - 1) * 1.2 / 99.0;

        /// <summary>
        /// When true, volume normalization also trims leading and trailing digital
        /// silence from the audio so tracks start (and end) on the first/last audible
        /// sample. Default: true.
        /// </summary>
        public static bool TrimSilence
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("TrimSilence", 1);
                    if (value is int iv) return iv != 0;
                    if (value is long lv) return lv != 0;
                    if (value is string sv && int.TryParse(sv, out var parsed)) return parsed != 0;
                }
                catch { }
                return true;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("TrimSilence", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Playback volume for the bottom player bar (0–100). Default: 100.
        /// Distinct from <see cref="ScdVolumePercentage"/>, which controls exported SCD loudness.
        /// </summary>
        public static int PlaybackVolume
        {
            get
            {
                try
                {
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("PlaybackVolume", 100);
                    if (value is int iv && iv >= 0 && iv <= 100) return iv;
                    if (value is long lv && lv >= 0 && lv <= 100) return (int)lv;
                    if (value is string sv && int.TryParse(sv, out var parsed) && parsed >= 0 && parsed <= 100) return parsed;
                }
                catch { }
                return 100;
            }
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("PlaybackVolume", clamped, RegistryValueKind.DWord);
            }
        }

        public static (int Width, int Height) WindowSize
        {
            get
            {
                try
                {
                    var k = Registry.CurrentUser.OpenSubKey(s_subKey);
                    if (k != null)
                    {
                        var w = k.GetValue("WindowWidth");
                        var h = k.GetValue("WindowHeight");
                        if (w is int wi && h is int hi && wi > 100 && hi > 100)
                            return (wi, hi);
                    }
                }
                catch { }
                return (900, 600);
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(s_subKey);
                key?.SetValue("WindowWidth",  value.Width,  RegistryValueKind.DWord);
                key?.SetValue("WindowHeight", value.Height, RegistryValueKind.DWord);
            }
        }
    }
}
