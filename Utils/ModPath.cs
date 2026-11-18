using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Keeps paths read from a mod's own JSON inside that mod's folder. Every delete, move or write
    /// built from a mod-relative path goes through <see cref="ResolveInside"/>; a value that escapes
    /// (<c>..\</c>, <c>C:\...</c>) resolves to null and the caller skips it. Pure: no Settings, no logging.
    /// </summary>
    internal static class ModPath
    {
        private static readonly char[] InvalidChars = Path.GetInvalidPathChars();
        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// The in-mod relative path a value names, with <c>.</c> and <c>..</c> folded away, or null when it
        /// is rooted, holds a ':', an invalid character, a trailing-dot/space segment, or climbs out.
        /// Root-independent, so "..\anything\x.scd" is null whatever "anything" is.
        /// </summary>
        public static string? CanonicalRelative(string? relative)
        {
            // No whitespace trim: Windows keeps leading spaces, NBSP etc., so trimming would name another folder.
            string n = (relative ?? string.Empty).Replace('/', '\\').TrimStart('\\');
            if (n.Length == 0 || n.Contains(':') || n.IndexOfAny(InvalidChars) >= 0 || Path.IsPathRooted(n))
                return null;

            var stack = new List<string>();
            foreach (string seg in n.Split('\\'))
            {
                if (seg.Length == 0 || seg == ".")
                    continue;
                if (seg == "..")
                {
                    if (stack.Count == 0) return null;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                // Windows drops trailing dots/spaces, so "pl.\x" would name pl\x under another key.
                if (seg.EndsWith('.') || seg.EndsWith(' ') || seg.IndexOfAny(InvalidNameChars) >= 0)
                    return null;
                stack.Add(seg);
            }
            return stack.Count == 0 ? null : string.Join('\\', stack);
        }

        /// <summary>The full path a mod-relative value names, only if it lies strictly inside the mod root; else null.</summary>
        public static string? ResolveInside(string? modRoot, string? relative)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return null;
            string? canon = CanonicalRelative(relative);
            if (canon == null) return null;
            try
            {
                string rootFull = Path.GetFullPath(modRoot);
                string full = Path.GetFullPath(Path.Combine(rootFull, canon));
                return IsStrictlyInside(rootFull, full) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a full path lies strictly below a full root (never the root itself, never a sibling sharing its prefix).</summary>
        public static bool IsStrictlyInside(string? rootFull, string? full)
        {
            if (string.IsNullOrWhiteSpace(rootFull) || string.IsNullOrWhiteSpace(full)) return false;
            string prefix = rootFull.TrimEnd('\\', '/') + '\\';
            return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Comparison key for a mod path: canonical when safe, else the normalized raw value (so unsafe values never alias safe ones). Compare with OrdinalIgnoreCase.</summary>
        public static string Key(string? relative) =>
            CanonicalRelative(relative) ?? (relative ?? string.Empty).Replace('/', '\\').TrimStart('\\');

        /// <summary>Lenient full path for "is this still referenced" checks only — never a delete/move target.</summary>
        public static string? FullUnder(string? modRoot, string? value)
        {
            if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(value)) return null;
            try { return Path.GetFullPath(Path.Combine(Path.GetFullPath(modRoot), Playlist.NormalizeRelativeModPath(value))); }
            catch { return null; }
        }
    }
}
