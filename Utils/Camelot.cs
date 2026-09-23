using System;
using System.Collections.Generic;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Camelot wheel notation for the keys KeyDetector produces ("C", "F#", "Am", "F#m").
    /// Minor keys are the A ring, major the B ring: Am -> 8A, C -> 8B.
    /// </summary>
    internal static class Camelot
    {
        // Pitch class (C=0..B=11) -> Camelot number on the major ring. The minor ring is the
        // same table read a minor third up, so one table covers both.
        private static readonly int[] MajorNumbers = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };

        // Canonical spelling per pitch class, matching what KeyDetector writes today.
        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private static readonly Dictionary<string, int> PitchClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = 0,
            ["C#"] = 1, ["Db"] = 1,
            ["D"] = 2,
            ["D#"] = 3, ["Eb"] = 3,
            ["E"] = 4,
            ["F"] = 5,
            ["F#"] = 6, ["Gb"] = 6,
            ["G"] = 7,
            ["G#"] = 8, ["Ab"] = 8,
            ["A"] = 9,
            ["A#"] = 10, ["Bb"] = 10,
            ["B"] = 11,
        };

        internal static bool TryParse(string? key, out int number, out bool minor)
            => TryParse(key, out number, out minor, out _);

        private static bool TryParse(string? key, out int number, out bool minor, out int pitchClass)
        {
            number = 0;
            minor = false;
            pitchClass = 0;
            if (string.IsNullOrWhiteSpace(key)) return false;

            // Two spellings live in the key cache: the current "F#m"/"F#", and "F# Minor"/
            // "F# Major" written by an older build. Both have to parse or those songs silently
            // lose their wheel position.
            string root = key.Trim();
            if (root.EndsWith(" Minor", StringComparison.OrdinalIgnoreCase))
            {
                minor = true;
                root = root[..^6].TrimEnd();
            }
            else if (root.EndsWith(" Major", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^6].TrimEnd();
            }
            else if (root.EndsWith("m", StringComparison.Ordinal))
            {
                minor = true;
                root = root.Substring(0, root.Length - 1);
            }

            if (!PitchClasses.TryGetValue(root, out pitchClass)) return false;

            number = minor ? MajorNumbers[(pitchClass + 3) % 12] : MajorNumbers[pitchClass];
            return true;
        }

        /// <summary>
        /// The key in the current short spelling ("F# Minor" -> "F#m"), so names built from an
        /// old cache entry don't read "[11A F# Minor]" beside "[8A Am]". Unrecognised keys are
        /// passed through untouched rather than dropped.
        /// </summary>
        internal static string? Normalize(string? key)
            => TryParse(key, out _, out bool minor, out int pitchClass)
                ? NoteNames[pitchClass] + (minor ? "m" : "")
                : key;

        /// <summary>"F#m" -> "11A". Null when the key is missing or unrecognised.</summary>
        internal static string? Code(string? key)
            => TryParse(key, out int number, out bool minor) ? $"{number}{(minor ? 'A' : 'B')}" : null;

        /// <summary>
        /// Position in a zig-zag walk of the wheel: 1A, 1B, 2B, 2A, 3A, 3B, 4B, 4A, ... Every
        /// consecutive pair is a valid move (relative major/minor, or one step with the letter
        /// held), and the sequence closes back on 1A. Unknown keys sort last.
        /// </summary>
        internal static int SortIndex(string? key)
        {
            if (!TryParse(key, out int number, out bool minor)) return int.MaxValue;
            bool minorFirst = number % 2 == 1;
            return (number - 1) * 2 + (minor == minorFirst ? 0 : 1);
        }

        /// <summary>
        /// Wheel colour for the key: hue steps 30 degrees per number with 12 at cyan, matching the
        /// standard wheel. The minor ring is the same hue, paler. Light enough for black text.
        /// </summary>
        internal static Windows.UI.Color? Background(string? key)
        {
            if (!TryParse(key, out int number, out bool minor)) return null;

            double hue = (180.0 - number * 30.0 + 360.0) % 360.0;
            return minor ? FromHsl(hue, 0.55, 0.78) : FromHsl(hue, 0.85, 0.70);
        }

        private static Windows.UI.Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
            double m = l - c / 2;

            (double r, double g, double b) = (h / 60.0) switch
            {
                < 1 => (c, x, 0.0),
                < 2 => (x, c, 0.0),
                < 3 => (0.0, c, x),
                < 4 => (0.0, x, c),
                < 5 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };

            return Windows.UI.Color.FromArgb(
                255,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }
}
