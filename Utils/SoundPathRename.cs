using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>One .avfx file and every reference in it to the path being renamed.</summary>
    internal sealed class AvfxHit
    {
        public string FullPath { get; init; } = string.Empty;

        /// <summary>Path relative to the mod folder, for display and for the backup layout.</summary>
        public string RelativePath { get; init; } = string.Empty;

        public IReadOnlyList<SoundRef> Refs { get; init; } = Array.Empty<SoundRef>();
    }

    /// <summary>
    /// Another Penumbra mod whose .avfx request the same sound path.
    ///
    /// A DJ's effects usually ship as a SEPARATE mod from their music — measured on a real Penumbra
    /// root, "DJ Pickles Base Mod Pack V4" and "Lilly's Silent Dance Party" carry emitters asking for
    /// <c>sound/bpmloop.scd</c> and supply no .scd of their own. Renaming only the configured mod
    /// leaves those effects asking for the old global path, so they trigger whichever other DJ won
    /// the shared slot. They have to be renamed alongside it.
    ///
    /// Opt-in, never automatic: other people's mods live in the same Penumbra root and legitimately
    /// share the path.
    /// </summary>
    internal sealed class CompanionMod
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public IReadOnlyList<AvfxHit> Avfx { get; init; } = Array.Empty<AvfxHit>();

        /// <summary>
        /// The .scd game paths this mod's own options redirect. Non-empty means it is a music mod in
        /// its own right, not a companion effect pack — see <see cref="Selectable"/>.
        /// </summary>
        public IReadOnlyList<string> ScdKeys { get; init; } = Array.Empty<string>();

        /// <summary>
        /// True for a pure emitter mod, which needs only its .avfx patched and no JSON write at all.
        ///
        /// A mod that redirects .scd paths of its own is refused deliberately. Every JSON write in
        /// this app derives its target from <see cref="Settings.ModName"/>, so re-keying another
        /// folder's manifest would mean repointing that setting mid-operation — a registry write, a
        /// misleading log line, and exactly the mod swap <see cref="PenumbraMeta.AssertModRootUnchanged"/>
        /// exists to catch. Renaming its .avfx while leaving its option keys behind would break it
        /// outright, so it is listed and refused rather than half-handled.
        /// </summary>
        public bool Selectable => ScdKeys.Count == 0;

        public int RefCount => Avfx.Sum(a => a.Refs.Count);
    }

    /// <summary>Everything a rename would touch, gathered without writing anything.</summary>
    internal sealed class SoundPathRenamePlan
    {
        public string ModRoot { get; init; } = string.Empty;
        public string ModName { get; init; } = string.Empty;
        public string OldPath { get; init; } = string.Empty;
        public string NewPath { get; init; } = string.Empty;
        public ModFormat Format { get; init; }

        public IReadOnlyList<AvfxHit> Avfx { get; init; } = Array.Empty<AvfxHit>();
        public IReadOnlyList<CompanionMod> Companions { get; init; } = Array.Empty<CompanionMod>();

        /// <summary>Files naming the old path as text where no reference passed the SdNm checks.</summary>
        public IReadOnlyList<string> SuspectFiles { get; init; } = Array.Empty<string>();

        /// <summary>.tmb / .pap files naming the old path. Renaming out from under these breaks them.</summary>
        public IReadOnlyList<string> BlockingFiles { get; init; } = Array.Empty<string>();

        /// <summary>Reasons this rename must not proceed. Empty means it may.</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        /// <summary>
        /// How many <c>Files</c> entries in the mod's JSON name the old path — songs plus any default
        /// redirect. Counted off disk, so it matches what
        /// <see cref="SoundPathRenameResult.OptionKeysRenamed"/> will report.
        /// </summary>
        public int OptionKeyCount { get; init; }

        public bool DefaultDataAffected { get; init; }

        public bool CanApply => Errors.Count == 0;
        public int RefCount => Avfx.Sum(a => a.Refs.Count);
    }

    internal sealed class SoundPathRenameResult
    {
        public bool Succeeded { get; init; }
        public int AvfxPatched { get; init; }
        public int OptionKeysRenamed { get; init; }
        public IReadOnlyList<string> CompanionsPatched { get; init; } = Array.Empty<string>();
        public string? BackupFolder { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
    }

    /// <summary>
    /// Renames the sound path a DJ mod claims — the .avfx emitters that REQUEST it and the Penumbra
    /// option keys that SUPPLY it — in one operation that either lands completely or changes nothing.
    ///
    /// Penumbra collection-scopes VFX but not sound, so a mod-invented path like
    /// <c>sound/bpmloop.scd</c> is one global slot for the whole zone: whichever collection resolves
    /// it first fills it for everyone. Every DJ pack built on Pickles redirects that same path, so a
    /// venue with two DJs plays the wrong track or nothing. Moving each DJ to a path nobody else uses
    /// gives it exactly one claimant, and it works for every listener including stock clients.
    ///
    /// The path lives in two places and the app only ever wrote one of them, which is why this exists
    /// rather than just letting Settings write the key: change the option keys alone and the song
    /// sits on a path nothing asks for, while the .avfx still asks for a path the DJ no longer
    /// supplies. <see cref="Playlist.GetScdKey"/>'s fallback then hides the damage from the app's own
    /// UI, so the mod looks fine here and is silently broken in game.
    /// </summary>
    internal static class SoundPathRename
    {
        /// <summary>See <see cref="AvfxSoundPath.AllowedLengthRange"/>.</summary>
        public static (int Min, int Max) AllowedLengthRange(string oldPath) =>
            AvfxSoundPath.AllowedLengthRange(oldPath);

        /// <summary>
        /// How many characters of a DJ NAME fit in a replacement for <paramref name="oldPath"/>.
        ///
        /// This is the number worth putting in front of a user. The bucket in
        /// <see cref="AvfxSoundPath.AllowedLengthRange"/> is a whole-PATH length — 16 to 19 for
        /// <c>sound/bpmloop.scd</c> — and quoting it beside a box labelled "your DJ name" reads as a
        /// demand for a 16-character name, which is absurd: real DJ names are "Soli".
        /// <c>sound/</c> + <c>bpm</c> + <c>.scd</c> is 13 of those characters before the name gets a
        /// look in, so the real answer for that baseline is 3 to 6.
        /// </summary>
        public static (int Min, int Max) NameLengthRange(string oldPath)
        {
            (int min, int max) = AllowedLengthRange(oldPath);
            int fixedChars = NamePrefix(oldPath).Length + ScdSuffix.Length;
            return (Math.Max(0, min - fixedChars), Math.Max(0, max - fixedChars));
        }

        private const string ScdSuffix = ".scd";
        private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

        private static string NamePrefix(string oldPath) => DirectoryOf(oldPath) + "bpm";

        /// <summary>
        /// A candidate path built from a DJ's name. Empty only when the name has no usable characters
        /// at all.
        ///
        /// A name that is too LONG is truncated, and one that is too SHORT is padded rather than
        /// rejected — "Soli" fits <c>sound/bpmsoli.scd</c> comfortably, but a one- or two-letter name
        /// would leave the path below the bucket minimum, and telling someone their DJ name is
        /// unacceptable is not a reasonable thing for this app to do. The padding is a deterministic
        /// FNV-1a digest of the name in base 36, so the same name always yields the same path — a
        /// requirement, not a nicety: a DJ who renames twice must land on the same path both times, or
        /// their own effects would stop matching their songs.
        /// </summary>
        public static string SuggestPath(string? djName, string oldPath)
        {
            var sb = new StringBuilder();
            foreach (char c in (djName ?? string.Empty).ToLowerInvariant())
            {
                if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                    sb.Append(c);
            }
            if (sb.Length == 0)
                return string.Empty;

            (int minName, int maxName) = NameLengthRange(oldPath);
            if (maxName < 1)
                return string.Empty;   // no room for any name at all — a very short baseline

            if (sb.Length > maxName)
                sb.Length = maxName;

            if (sb.Length < minName)
            {
                uint hash = VfxEditor.Utils.FnvUtils.Encode(sb.ToString());
                while (sb.Length < minName)
                {
                    sb.Append(Base36[(int)(hash % 36)]);
                    hash /= 36;
                    // Re-seed rather than run out of digits into a run of zeroes.
                    if (hash == 0) hash = VfxEditor.Utils.FnvUtils.Encode(sb.ToString());
                }
            }

            return NamePrefix(oldPath) + sb + ScdSuffix;
        }

        /// <summary>
        /// True when <see cref="SuggestPath"/> had to add characters the user did not type, so the UI
        /// can say so instead of silently handing back a path they did not ask for.
        /// </summary>
        public static bool WasPadded(string? djName, string oldPath)
        {
            int typed = (djName ?? string.Empty).Count(c =>
                c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'));
            return typed > 0 && typed < NameLengthRange(oldPath).Min;
        }

        // Keeps a suggestion in the same folder as the path it replaces: the folder is part of the
        // length budget, and a mod whose baseline is "sound/dam.scd" has no room to move.
        private static string DirectoryOf(string path)
        {
            int slash = (path ?? string.Empty).LastIndexOf('/');
            return slash < 0 ? string.Empty : path[..(slash + 1)];
        }

        /// <summary>
        /// Why <paramref name="newPath"/> cannot be used, or null when it can. Shape only — whether
        /// the mod actually contains it is <see cref="BuildPlan"/>'s job.
        /// </summary>
        public static string? ValidateNewPath(string oldPath, string? newPath)
        {
            string candidate = PenumbraMeta.NormalizeScdKey(newPath);
            if (string.IsNullOrWhiteSpace(candidate))
                return "Enter a sound path.";

            if (!candidate.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                return "The sound path must end in .scd";

            // Penumbra game paths are lowercase, so an uppercase one would never match the request
            // the .avfx makes — it would fail silently and look exactly like the bug being fixed.
            foreach (char c in candidate)
            {
                if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.' or '/')
                    continue;
                return "The sound path may only use lowercase letters, digits, '/', '_', '-' and '.'";
            }

            if (string.Equals(candidate, PenumbraMeta.NormalizeScdKey(oldPath), StringComparison.OrdinalIgnoreCase))
                return "That is already the current sound path.";

            if (!AvfxSoundPath.FitsBucket(oldPath, candidate))
            {
                (int min, int max) = AllowedLengthRange(oldPath);
                return $"The new path must be {min}-{max} characters long (it is {candidate.Length}). " +
                       "Longer or shorter would change the size of the effect files, which cannot be " +
                       "patched safely.";
            }

            return null;
        }

        // ---- discovery ---------------------------------------------------------------------------

        private const string AvfxGlob = "*.avfx";

        /// <summary>
        /// Every .avfx under <paramref name="root"/> carrying a reference to <paramref name="path"/>.
        /// <paramref name="suspects"/> collects files that name the path as text without a single
        /// reference passing the structural checks — reported, never silently skipped, because for an
        /// authoring tool a missed reference is far worse than a false alarm.
        /// </summary>
        public static List<AvfxHit> ScanForAvfx(string root, string path, List<string>? suspects = null)
        {
            var hits = new List<AvfxHit>();
            if (string.IsNullOrEmpty(AvfxSoundPath.Normalize(path)) || !Directory.Exists(root))
                return hits;

            foreach (string file in EnumerateFilesSafely(root, AvfxGlob))
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); }
                catch (Exception ex)
                {
                    Logger.LogWarn("Sound path scan: could not read '{File}': {Error}", file, ex.Message);
                    suspects?.Add(file);
                    continue;
                }

                var refs = AvfxSoundPath.FindReferences(bytes, path);
                var literals = AvfxSoundPath.FindLiteralOffsets(bytes, path);

                if (refs.Count > 0)
                {
                    hits.Add(new AvfxHit
                    {
                        FullPath = file,
                        RelativePath = Path.GetRelativePath(root, file),
                        Refs = refs,
                    });
                }

                // Every textual occurrence has to be accounted for by a validated reference. One that
                // is not may be a Sound field this scanner failed to recognise, and patching the rest
                // while leaving it behind is the half-renamed mod this whole operation exists to avoid.
                if (literals.Any(offset => refs.All(r => r.PathOffset != offset)))
                    suspects?.Add(file);
            }

            return hits;
        }

        /// <summary>
        /// .tmb / .pap files naming the path. A TMB C063 audio entry names an .scd outright and a PAP
        /// embeds a TMB, so renaming out from under one breaks it — and neither has the size headroom
        /// the AVFX chunk does. These are reported and the rename refused; that is a case for a human.
        /// </summary>
        public static List<string> ScanForBlockers(string root, string path)
        {
            var blockers = new List<string>();
            if (string.IsNullOrEmpty(AvfxSoundPath.Normalize(path)) || !Directory.Exists(root))
                return blockers;

            foreach (string file in EnumerateFilesSafely(root, "*.tmb").Concat(EnumerateFilesSafely(root, "*.pap")))
            {
                try
                {
                    if (AvfxSoundPath.FindLiteralOffsets(File.ReadAllBytes(file), path).Count > 0)
                        blockers.Add(file);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("Sound path scan: could not read '{File}': {Error}", file, ex.Message);
                }
            }

            return blockers;
        }

        // A single unreadable subdirectory must not abort the sweep: a mod folder can contain
        // anything, and a missed reference is the failure mode this operation cannot afford.
        private static IEnumerable<string> EnumerateFilesSafely(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Sound path scan: could not enumerate '{Root}': {Error}", root, ex.Message);
                return Array.Empty<string>();
            }
        }

        /// <summary>How many Sound references to <paramref name="path"/> a mod folder holds.</summary>
        public static int CountAvfxReferences(string root, string path) =>
            ScanForAvfx(root, path).Sum(hit => hit.Refs.Count);

        /// <summary>
        /// Other mods under the Penumbra root whose effects request the same path. See
        /// <see cref="CompanionMod"/> for why this matters and why selection is left to the user.
        /// </summary>
        public static List<CompanionMod> FindCompanions(string penumbraRoot, string configuredModName, string path)
        {
            var companions = new List<CompanionMod>();
            if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot))
                return companions;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(penumbraRoot); }
            catch (Exception ex)
            {
                Logger.LogWarn("Companion scan: could not list '{Root}': {Error}", penumbraRoot, ex.Message);
                return companions;
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, configuredModName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var hits = ScanForAvfx(directory, path);
                if (hits.Count == 0)
                    continue;

                companions.Add(new CompanionMod
                {
                    Name = name,
                    FullPath = directory,
                    Avfx = hits,
                    ScdKeys = PenumbraMeta.CollectScdKeys(directory),
                });
            }

            return companions.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---- planning ----------------------------------------------------------------------------

        /// <summary>
        /// Everything the rename would touch, and every reason it must not run. Reads only.
        /// </summary>
        public static SoundPathRenamePlan BuildPlan(string oldPath, string newPath)
        {
            string modRoot = PenumbraMeta.ModRoot;
            string modName = Settings.ModName ?? string.Empty;
            string normalizedOld = PenumbraMeta.NormalizeScdKey(oldPath);
            string normalizedNew = PenumbraMeta.NormalizeScdKey(newPath);
            var errors = new List<string>();

            string? shapeError = ValidateNewPath(normalizedOld, normalizedNew);
            if (shapeError != null)
                errors.Add(shapeError);

            if (!Directory.Exists(modRoot))
            {
                errors.Add($"The mod folder does not exist: {modRoot}");
                return new SoundPathRenamePlan
                {
                    ModRoot = modRoot, ModName = modName,
                    OldPath = normalizedOld, NewPath = normalizedNew,
                    Errors = errors,
                };
            }

            var format = PenumbraMeta.DetectFormat();
            if (format == ModFormat.Unknown)
                errors.Add("This is not a recognizable Penumbra mod folder (meta.json is missing or " +
                           "unreadable), so nothing can be renamed safely.");

            var suspects = new List<string>();
            var avfx = ScanForAvfx(modRoot, normalizedOld, suspects);
            var blockers = ScanForBlockers(modRoot, normalizedOld);

            if (avfx.Count == 0)
                errors.Add($"No effect files in '{modName}' request '{normalizedOld}'. Renaming the " +
                           "song entries alone would leave the mod with music nothing asks for.");

            if (blockers.Count > 0)
                errors.Add("These animation files also name the old path and cannot be patched " +
                           "safely:\n  " + string.Join("\n  ", blockers.Select(Path.GetFileName)));

            if (PenumbraMeta.CollectScdKeys(modRoot)
                    .Any(k => string.Equals(k, normalizedNew, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"'{normalizedNew}' is already used by a song in this mod. Pick another name.");

            var unreadable = UnreadablePlaylistFiles(modRoot, format);
            if (unreadable.Count > 0)
                errors.Add("These playlist files cannot be read, so the songs in them could not be " +
                           "moved and would be left behind:\n  "
                           + string.Join("\n  ", unreadable.Select(Path.GetFileName))
                           + "\n\n(If Penumbra is running it may be mid-write — try again in a moment.)");

            // Counted off disk rather than through the store, so this number agrees with what Apply
            // will actually move and cannot be quietly short by a playlist the store skipped.
            int optionKeys = 0;
            bool defaultData = false;
            try
            {
                optionKeys = CountKeysOnDisk(modRoot, format, normalizedOld);
                defaultData = DefaultDataCarriesKey(modRoot, format, normalizedOld);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Sound path plan: could not count the songs to re-key: {Error}", ex.Message);
            }

            var companions = FindCompanions(Settings.PenumbraLocation ?? string.Empty, modName, normalizedOld);

            return new SoundPathRenamePlan
            {
                ModRoot = modRoot,
                ModName = modName,
                OldPath = normalizedOld,
                NewPath = normalizedNew,
                Format = format,
                Avfx = avfx,
                Companions = companions,
                SuspectFiles = suspects,
                BlockingFiles = blockers,
                Errors = errors,
                OptionKeyCount = optionKeys,
                DefaultDataAffected = defaultData,
            };
        }

        /// <summary>
        /// Whether the mod's DEFAULT redirect carries the key — <c>DefaultData</c> under v4,
        /// <c>default_mod.json</c> under v3.
        ///
        /// Worth reporting separately from the song count: a default redirect left on the old path
        /// keeps this mod claiming the shared global slot even after every song has moved, which is
        /// the entire bug the rename exists to fix.
        /// </summary>
        private static bool DefaultDataCarriesKey(string modRoot, ModFormat format, string oldPath)
        {
            if (format == ModFormat.V3)
            {
                string legacy = Path.Combine(modRoot, PenumbraMeta.LegacyDefaultMod);
                return File.Exists(legacy)
                    && ScdKeyRename.CountInFiles(
                        JObject.Parse(File.ReadAllText(legacy, Encoding.UTF8)), oldPath) > 0;
            }

            string meta = Path.Combine(modRoot, PenumbraMeta.MetaFile);
            return File.Exists(meta)
                && ScdKeyRename.CountInFiles(
                    JObject.Parse(File.ReadAllText(meta, Encoding.UTF8))["DefaultData"] as JObject, oldPath) > 0;
        }

        /// <summary>
        /// v3 group files (and <c>default_mod.json</c>) that will not parse right now.
        ///
        /// This is a hard stop for a rename, and the reason is subtle enough to be worth stating.
        /// <see cref="V3GroupFileStore.ReadAll"/> deliberately SKIPS a group file it cannot read — a
        /// sensible policy for loading a library, and a silent disaster here: the skipped playlist is
        /// simply absent from the re-key, so its songs stay on the old path while the effects move to
        /// the new one, and the whole operation reports success. Worse, a verification pass reading
        /// through the same store would agree, being blind in exactly the same way. Observed while
        /// testing: locking one group file produced a "successful" rename that left half the mod
        /// behind.
        ///
        /// v4 has no equivalent hole — the manifest either parses whole or <see cref="PenumbraMeta.Read"/>
        /// returns null and <see cref="PenumbraMeta.Mutate"/> refuses to write.
        /// </summary>
        private static List<string> UnreadablePlaylistFiles(string modRoot, ModFormat format)
        {
            var unreadable = new List<string>();
            if (format != ModFormat.V3 || !Directory.Exists(modRoot))
                return unreadable;

            foreach (string file in Directory
                .EnumerateFiles(modRoot, "group_*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(modRoot, PenumbraMeta.LegacyDefaultMod, SearchOption.TopDirectoryOnly)))
            {
                try { JObject.Parse(File.ReadAllText(file, Encoding.UTF8)); }
                catch { unreadable.Add(file); }
            }
            return unreadable;
        }

        /// <summary>
        /// How many <c>Files</c> entries in the mod's own JSON still name <paramref name="path"/>,
        /// read straight off disk.
        ///
        /// Deliberately NOT through <see cref="IPlaylistStore.ReadAll"/>: see
        /// <see cref="UnreadablePlaylistFiles"/> for why a check that goes through the store cannot
        /// detect the failure it most needs to.
        /// </summary>
        private static int CountKeysOnDisk(string modRoot, ModFormat format, string path)
        {
            IEnumerable<string> files = format == ModFormat.V3
                ? Directory.EnumerateFiles(modRoot, "group_*.json", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(modRoot, PenumbraMeta.LegacyDefaultMod, SearchOption.TopDirectoryOnly))
                : new[] { Path.Combine(modRoot, PenumbraMeta.MetaFile) };

            int found = 0;
            foreach (string file in files.Where(File.Exists))
            {
                var root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                // Every Files object at any depth, so one walk covers DefaultData, each option, and
                // the v3 group and default_mod shapes alike.
                found += root.SelectTokens("$..Files").OfType<JObject>()
                    .Sum(f => f.Properties().Count(p => ScdKeyRename.KeyMatches(p.Name, path)));
            }
            return found;
        }

        // ---- applying ----------------------------------------------------------------------------

        /// <summary>
        /// Runs the rename, or leaves everything as it was.
        ///
        /// Order is the whole design, because a partial rename is worse than none: validate, back up
        /// every .avfx (the manifest snapshots do NOT cover those), patch the effects, then re-key the
        /// JSON — and restore every backup if any of it fails. The effects come first because they are
        /// the half this app can roll back byte-for-byte.
        /// </summary>
        /// <param name="selectedCompanions">
        /// Names of companion mods the user ticked. Their .avfx are patched inside the SAME
        /// transaction — a renamed music mod whose companion effects still request the old path is
        /// the same broken state the rename exists to prevent, so one failing aborts all of it.
        /// </param>
        public static SoundPathRenameResult Apply(SoundPathRenamePlan plan, IReadOnlyCollection<string> selectedCompanions)
        {
            var warnings = new List<string>();

            if (!plan.CanApply)
                return new SoundPathRenameResult { Error = string.Join("\n\n", plan.Errors) };

            var companions = plan.Companions
                .Where(c => selectedCompanions.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var notSelectable = companions.Where(c => !c.Selectable).ToList();
            if (notSelectable.Count > 0)
                return new SoundPathRenameResult
                {
                    Error = "These mods have songs of their own, so they cannot be renamed from here:\n  "
                            + string.Join("\n  ", notSelectable.Select(c => c.Name))
                            + "\n\nSelect each one as the mod folder in Settings and rename it there.",
                };

            string capturedRoot = plan.ModRoot;
            try
            {
                PenumbraMeta.AssertModRootUnchanged(capturedRoot);
            }
            catch (Exception ex)
            {
                return new SoundPathRenameResult { Error = ex.Message };
            }

            // Re-checked here, not just at planning time: a group file can become unreadable in
            // between, and the v3 store would then quietly skip that playlist and leave its songs on
            // the old path. Checked BEFORE anything is written, since this is the one failure that
            // rolling the effects back cannot fully undo.
            var unreadable = UnreadablePlaylistFiles(plan.ModRoot, plan.Format);
            if (unreadable.Count > 0)
                return new SoundPathRenameResult
                {
                    Error = "These playlist files cannot be read, so their songs would be left behind. " +
                            "Nothing was changed:\n  "
                            + string.Join("\n  ", unreadable.Select(Path.GetFileName))
                            + "\n\n(If Penumbra is running it may be mid-write — try again in a moment.)",
                };

            // Snapshot the JSON side up front. Mutate and the v3 store each take one anyway, but
            // taking it here means the pre-operation state is recoverable even if the JSON phase
            // fails partway under v3, where the writes cannot be atomic across group files.
            if (plan.Format == ModFormat.V3)
                V3GroupFileStore.TrySnapshotSet();
            else
                PenumbraMeta.TrySnapshot();

            // Every file about to change, keyed by the mod folder it belongs to so a restore can put
            // each one back where it came from.
            var targets = new List<(string ModName, string ModRoot, AvfxHit Hit)>();
            foreach (var hit in plan.Avfx)
                targets.Add((plan.ModName, plan.ModRoot, hit));
            foreach (var companion in companions)
                foreach (var hit in companion.Avfx)
                    targets.Add((companion.Name, companion.FullPath, hit));

            string backupFolder;
            try
            {
                backupFolder = BackUp(targets);
            }
            catch (Exception ex)
            {
                Logger.LogError("Sound path rename: backup failed, nothing was changed: {Error}", ex);
                return new SoundPathRenameResult
                {
                    Error = "The effect files could not be backed up, so nothing was changed.\n\n" + ex.Message,
                };
            }

            var written = new List<(string ModName, AvfxHit Hit)>();
            try
            {
                foreach (var (modName, _, hit) in targets)
                {
                    byte[] bytes = File.ReadAllBytes(hit.FullPath);
                    AvfxSoundPath.Patch(bytes, hit.Refs, plan.NewPath);
                    File.WriteAllBytes(hit.FullPath, bytes);
                    written.Add((modName, hit));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Sound path rename: patching '{File}' failed — restoring: {Error}",
                    written.Count < targets.Count ? targets[written.Count].Hit.RelativePath : "?", ex);
                RestoreAll(backupFolder, targets, warnings);
                return new SoundPathRenameResult
                {
                    Error = "An effect file could not be updated, so every change was rolled back.\n\n" + ex.Message,
                    BackupFolder = backupFolder,
                    Warnings = warnings,
                };
            }

            int renamed;
            // Under v3 each playlist is its own file, so the re-key cannot be one atomic write and a
            // failure can leave some already done. Recording them as it goes is what lets the error
            // say which, instead of claiming a clean abort it cannot deliver.
            var partiallyRenamed = new List<string>();
            try
            {
                PenumbraMeta.AssertModRootUnchanged(capturedRoot);
                renamed = ReKeyJson(plan, partiallyRenamed);
            }
            catch (Exception ex)
            {
                Logger.LogError("Sound path rename: re-keying the songs failed — restoring the effects: {Error}", ex);
                RestoreAll(backupFolder, targets, warnings);

                string detail = partiallyRenamed.Count == 0
                    ? "The songs could not be moved onto the new path, so the effect files were rolled " +
                      "back and nothing else was changed."
                    : "The songs could not all be moved onto the new path. The effect files were rolled " +
                      "back, but these playlists had already been changed and are still on the new " +
                      "path:\n  " + string.Join("\n  ", partiallyRenamed) +
                      "\n\nA copy of the mod folder from before this ran is in " +
                      V3GroupFileStore.SnapshotRoot;

                return new SoundPathRenameResult
                {
                    Error = detail + "\n\n" + ex.Message,
                    BackupFolder = backupFolder,
                    Warnings = warnings,
                };
            }

            Settings.BaselineScdKey = plan.NewPath;

            Logger.LogInfo("Sound path renamed from '{Old}' to '{New}': {Files} effect file(s), " +
                "{Keys} song(s), {Companions} companion mod(s).",
                plan.OldPath, plan.NewPath, targets.Count, renamed, companions.Count);

            Reload(plan, companions, warnings);
            VerifyNothingLeftBehind(plan, companions, warnings);

            return new SoundPathRenameResult
            {
                Succeeded = true,
                AvfxPatched = targets.Count,
                OptionKeysRenamed = renamed,
                CompanionsPatched = companions.Select(c => c.Name).ToList(),
                BackupFolder = backupFolder,
                Warnings = warnings,
            };
        }

        // ---- backups -----------------------------------------------------------------------------

        /// <summary>
        /// Copies every .avfx about to change into one timestamped folder, the mod folder name as the
        /// first path segment so a restore is unambiguous across mods.
        ///
        /// <see cref="PenumbraMeta.TrySnapshot"/> covers the manifest and group JSON only — .avfx are
        /// not in it, and this is the only copy of the pre-rename bytes.
        /// </summary>
        private static string BackUp(IReadOnlyList<(string ModName, string ModRoot, AvfxHit Hit)> targets)
        {
            string folder = Path.Combine(Playlist.BackupDir, "avfx",
                PenumbraMeta.SnapshotFolderNameForMod(),
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(folder);

            foreach (var (modName, _, hit) in targets)
            {
                string destination = Path.Combine(folder, SafeFolderName(modName), hit.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(hit.FullPath, destination, overwrite: true);
            }

            Logger.LogInfo("Sound path rename: backed up {Count} effect file(s) to {Folder}",
                targets.Count, folder);
            return folder;
        }

        private static void RestoreAll(string backupFolder,
            IReadOnlyList<(string ModName, string ModRoot, AvfxHit Hit)> targets, List<string> warnings)
        {
            foreach (var (modName, _, hit) in targets)
            {
                string source = Path.Combine(backupFolder, SafeFolderName(modName), hit.RelativePath);
                try
                {
                    if (File.Exists(source))
                        File.Copy(source, hit.FullPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    // The bytes are still in the backup folder, so say where rather than lose them.
                    warnings.Add($"Could not restore {hit.RelativePath}: {ex.Message}");
                    Logger.LogError("Sound path rename: could not restore '{File}' from {Folder}: {Error}",
                        hit.FullPath, backupFolder, ex);
                }
            }
        }

        private static string SafeFolderName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim(' ', '.');
            return string.IsNullOrEmpty(name) ? "mod" : name;
        }

        // ---- the JSON half -----------------------------------------------------------------------

        /// <param name="renamedPlaylists">
        /// Appended to as each playlist lands, so a failure part-way through v3 can name what already
        /// changed. Stays empty for v4, where the whole manifest is one atomic write.
        /// </param>
        private static int ReKeyJson(SoundPathRenamePlan plan, List<string> renamedPlaylists)
        {
            lock (PlaylistStore.ModFolderGate)
            {
                return plan.Format == ModFormat.V3
                    ? ReKeyV3(plan, renamedPlaylists)
                    : ReKeyV4(plan);
            }
        }

        /// <summary>
        /// One <see cref="PenumbraMeta.Mutate"/> for the whole library.
        ///
        /// Deliberately not a Save() per playlist: under v4 every group lives in the same ~360KB
        /// manifest, so per-playlist saves would rewrite the entire file once per playlist and leave a
        /// window after each one in which the mod is half-renamed. Mutate re-reads under the lock and
        /// writes atomically, so this is a single all-or-nothing write.
        /// </summary>
        private static int ReKeyV4(SoundPathRenamePlan plan)
        {
            int renamed = 0;
            // Assigned inside the callback rather than accumulated: Mutate re-reads the manifest under
            // the lock, and if it ever retried, a running total would double-count.
            PenumbraMeta.Mutate(root => renamed = ScdKeyRename.RenameInManifest(root, plan.OldPath, plan.NewPath));
            return renamed;
        }

        /// <summary>
        /// v3 keeps each group in its own file, so this cannot be one atomic write. It goes through
        /// the store rather than writing the files directly: that is what keeps Penumbra's group
        /// numbering, its filename expectations and the in-place write contract intact.
        /// </summary>
        private static int ReKeyV3(SoundPathRenamePlan plan, List<string> renamedPlaylists)
        {
            int renamed = 0;
            var store = PlaylistStore.For(ModFormat.V3);

            foreach (var playlist in store.ReadAll())
            {
                int inThis = 0;
                foreach (var option in playlist.Options ?? new List<Option>())
                {
                    if (option?.Files == null) continue;
                    if (ScdKeyRename.RenameInFiles(option.Files, plan.OldPath, plan.NewPath))
                        inThis++;
                }
                if (inThis == 0) continue;

                store.Save(playlist);
                renamedPlaylists.Add(playlist.Name);
                renamed += inThis;
            }

            if (ReKeyLegacyDefaultMod(plan) > 0)
            {
                renamedPlaylists.Add(PenumbraMeta.LegacyDefaultMod);
                renamed++;
            }
            return renamed;
        }

        // default_mod.json is not a group file, so no store owns it. Written the way V3GroupFileStore
        // writes: staged in temp and copied in place, never moved — replacing the directory entry is
        // what makes Penumbra's watcher rewrite and renumber the folder.
        private static int ReKeyLegacyDefaultMod(SoundPathRenamePlan plan)
        {
            string path = Path.Combine(plan.ModRoot, PenumbraMeta.LegacyDefaultMod);
            if (!File.Exists(path))
                return 0;

            var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (ScdKeyRename.RenameInFiles(root, plan.OldPath, plan.NewPath) == 0)
                return 0;

            string tmp = Path.Combine(Path.GetTempPath(),
                PenumbraMeta.LegacyDefaultMod + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, PenumbraMeta.Serialize(root, ModFormat.V3), new UTF8Encoding(false));
                File.Copy(tmp, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            return 1;
        }

        // ---- afterwards --------------------------------------------------------------------------

        private static void Reload(SoundPathRenamePlan plan, IReadOnlyList<CompanionMod> companions, List<string> warnings)
        {
            // Refresh arms the debounce timer; flush fires it now rather than 20 seconds from now.
            // Flush alone would return immediately, since it no-ops when no timer was ever armed.
            Playlist.RefreshPenumbraMod();
            Playlist.FlushPenumbraMod();

            // The debounced path only ever reloads Settings.ModName, so each companion is asked
            // directly. A failure here is a warning, never a rollback: the bytes on disk are already
            // correct and Penumbra picks them up the next time it reloads anyway.
            foreach (var companion in companions)
            {
                try
                {
                    PenumbraApi.ReloadMod(companion.Name, companion.Name).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Penumbra could not be asked to reload '{companion.Name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Re-runs discovery for the OLD path and reports anything still on it.
        ///
        /// Worth the second pass because the app cannot otherwise reveal a half-rename to itself:
        /// <see cref="Playlist.GetScdKey"/> falls back to <c>sound/bpmloop.scd</c> and then to any
        /// .scd key, so a mod left half-renamed keeps looking perfectly healthy in this UI while being
        /// silently broken in game.
        /// </summary>
        private static void VerifyNothingLeftBehind(SoundPathRenamePlan plan,
            IReadOnlyList<CompanionMod> companions, List<string> warnings)
        {
            try
            {
                int leftover = CountAvfxReferences(plan.ModRoot, plan.OldPath);
                foreach (var companion in companions)
                    leftover += CountAvfxReferences(companion.FullPath, plan.OldPath);

                if (leftover > 0)
                    warnings.Add($"{leftover} effect reference(s) still name '{plan.OldPath}'. " +
                                 "Check them before playing.");

                int stillKeyed = CountKeysOnDisk(plan.ModRoot, plan.Format, plan.OldPath);
                if (stillKeyed > 0)
                    warnings.Add($"{stillKeyed} song entr(y/ies) still use '{plan.OldPath}'.");
            }
            catch (Exception ex)
            {
                warnings.Add("The rename could not be verified afterwards: " + ex.Message);
            }
        }
    }
}
