using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Tools
{
    /// <summary>One pinned release asset of the AI score. Files that load each other (.onnx and .onnx.data) share a tag.</summary>
    internal sealed record AiAsset(string Name, string Tag, long Bytes, string Sha256);

    /// <summary>NotFound: every source answered 404 or 410 for the file.</summary>
    internal enum AiAssetFailure { None, Http, Checksum, Transport, Disk, NotFound }

    internal sealed record AiAssetResult(bool Ok, AiAssetFailure Failure, string? FileName, string Detail)
    {
        public static readonly AiAssetResult Success = new(true, AiAssetFailure.None, null, "");
    }

    /// <summary>Absolute progress: this file's bytes so far and the whole set's.</summary>
    internal readonly record struct AiAssetProgress(string FileName, int FileIndex, int FileCount,
        long FileBytes, long FileTotal, long BytesDone, long BytesTotal);

    /// <summary>
    /// Pins of the AI-score downloads. Kept in sync with devtools/aiscan/releases.json (a test compares them) and
    /// with the /pickles/ route of the Proteus worker (worker/src/index.js) that mirrors them at dl.solona.info.
    /// </summary>
    internal static class AiAssets
    {
        public const string MirrorBase = "https://dl.solona.info/pickles/";
        public const string GitHubBase = "https://github.com/solona-m/Pickles-Playlist-Editor/releases/download/";

        // Pasted from devtools/aiscan/Stage-AiscanAssets.ps1 -EmitPins.
        public static readonly AiAsset[] Files =
        [
            new("ffmpeg.exe", "aiscan-v1", 227823104L, "589e50b766d251afdf181dd664d40bd94407e200b019989fd7468c7d118a28d0"),
            new("sonics-beta-120s.onnx", "aiscan-v1", 1160829L, "ef15582fef09654f2638c3656d7cd3615e61471e461a8cd460797b176db657a7"),
            new("sonics-beta-120s.onnx.data", "aiscan-v1", 86246448L, "9ac1de27e38dc5e84c917753dac67d1ef6da72dd23d147a090954789f3b30ceb"),
        ];

        /// <summary>Mirror first; GitHub stays the origin, so a missing or down mirror only costs one request.</summary>
        public static string[] BaseUrls(string tag) => [MirrorBase + tag + "/", GitHubBase + tag + "/"];

        public static AiAsset Ffmpeg => Files.Single(f => f.Name == "ffmpeg.exe");
        public static AiAsset Model => Files.Single(f => f.Name.EndsWith(".onnx", StringComparison.Ordinal));

        public static long TotalBytes => Files.Sum(f => f.Bytes);

        /// <summary>Where a pinned file lives once downloaded: root, tag folder, name.</summary>
        public static string LocalPath(AiAsset asset, string? root = null) => Path.Combine(root ?? DefaultRoot, asset.Tag, asset.Name);

        /// <summary>The download hosts in the order tried, for the consent text.</summary>
        public static IReadOnlyList<string> SourceHosts => [new Uri(MirrorBase).Host, new Uri(GitHubBase).Host];

        /// <summary>Next to Velopack's "current" folder, which updates replace wholesale.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicklesPlaylistEditor", "aiscan");
    }
}
