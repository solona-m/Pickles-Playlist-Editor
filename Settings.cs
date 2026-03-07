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
            "[yue's + lu's] dj"
        };
        // Initialized with three dummy keys/values
        private static Dictionary<string, string> s_defaultBaselineScdKey = new Dictionary<string, string>
        {
            { s_defaultModNames[0], "sound/bpmloop.scd" },
            { s_defaultModNames[1], "sound/dam.scd" },
            { s_defaultModNames[2], "sound/lolo.scd" }
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
        public static string ModName
        {
            get
            {
                string retval = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ModName");
                if (string.IsNullOrWhiteSpace(retval))
                {
                    foreach (string defaultName in s_defaultModNames)
                    {
                        string potentialPath = System.IO.Path.Combine(PenumbraLocation, defaultName);
                        if (System.IO.Directory.Exists(potentialPath))
                        {
                            ModName = defaultName; // save it for next time
                            return defaultName;
                        }
                    }
                }
                return retval;
            }
            set
            {
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
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (s_defaultBaselineScdKey.TryGetValue(ModName, out var defaultKey))
                    {
                        BaselineScdKey = defaultKey; // save it for next time
                        return defaultKey;
                    }
                    else
                    {
                        return s_defaultBaselineScdKey[s_defaultModNames[0]]; // fallback to first default if mod name is unrecognized, but don't save it
                    }
                }
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
                    var value = Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("NormalizeVolume", 1);
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
