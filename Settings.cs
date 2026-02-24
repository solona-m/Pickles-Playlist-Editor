using Microsoft.Win32;
using System;
namespace Pickles_Playlist_Editor
{
    public static class Settings
    {
        private static string s_valueName = "PenumbraPath";
        private static string s_subKey = @"SOFTWARE\ScdConverter";
        private static string s_defaultModName = "Gimme Pickle's DJ Muzik, Movez, and VFX";
        private static string s_defaultBaselineScdKey = "sound/bpmloop.scd";
        public static string[] SupportedFileTypes = new string[] { ".ogg", ".wav", ".mp3", ".m4a", ".scd" };

        public static string PenumbraLocation
        {
            get
            {
                // Read the value from the registry
                return (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue(s_valueName, null);
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
                return (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("ModName", s_defaultModName);
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
                string key = (string)Registry.CurrentUser.OpenSubKey(s_subKey)?.GetValue("BaselineScdKey", s_defaultBaselineScdKey);
                if (string.IsNullOrWhiteSpace(key))
                {
                    return s_defaultBaselineScdKey;
                }
                return key.Trim();
            }
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? s_defaultBaselineScdKey : value.Trim();
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
                return true;
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

    }
}
