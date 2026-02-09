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
    }
}
