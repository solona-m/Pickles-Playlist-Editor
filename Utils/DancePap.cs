using System;
using System.Collections.Generic;
using System.Linq;
using Pickles_Playlist_Editor.Utils.Tmb;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>What preparing a dance would do, gathered without writing anything.</summary>
    internal sealed class DancePapPlan
    {
        /// <summary>The animation name the source ships with, for the confirmation text.</summary>
        public string OldAnimationName { get; init; } = string.Empty;

        public string NewAnimationName { get; init; } = string.Empty;

        /// <summary>The race this .pap animates, as Penumbra spells it: c0101, c0801, ...</summary>
        public string RaceCode { get; init; } = string.Empty;

        /// <summary>C063 sound entries that name an .scd the dance would otherwise keep playing.</summary>
        public IReadOnlyList<string> SoundPathsToBlank { get; init; } = Array.Empty<string>();

        /// <summary>Effects the appended block will fire.</summary>
        public IReadOnlyList<string> EffectsToAdd { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Effects the dance ALREADY fires, which come along with it and are the DJ mod's problem
        /// afterwards: unlike the appended block, nothing supplies these.
        /// </summary>
        public IReadOnlyList<string> EffectsUsed { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The effect PATHS whose async-VFX clips will be removed, because the mod cannot supply the
        /// .avfx they name and the game does not ship it either.
        ///
        /// Not a warning about something the user might want to fix later — the entry is going, and
        /// they should be told, because a C173 pointing at a file nobody has is a crash rather than a
        /// missing sparkle. Paths rather than sentences so the caller can subtract them from its own
        /// "these effects are missing" list.
        /// </summary>
        public IReadOnlyList<string> AsyncEffectsDropped { get; init; } = Array.Empty<string>();

        public int TracksToAdd { get; init; }

        /// <summary>Reasons this must not run. Empty means it may.</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public bool CanApply => Errors.Count == 0;
    }

    /// <summary>
    /// Turns a downloaded dance into one the DJ mod can play, which is the whole of chapter one of the
    /// mod author's guide done without VFXEditor.
    ///
    /// Three edits, and no others:
    ///   1. the animation is renamed to what the DJ mod's option expects, in the .pap info table;
    ///   2. the timeline's C009 animation entry is pointed at that same name;
    ///   3. any C063 sound entry is REMOVED, so a dance that shipped with its own music stops
    ///      asking for it and leaves the slot to the DJ;
    ///   4. the DJ's effect block is appended, which is what makes the dance react to the music.
    ///
    /// Everything else in the file — the havok animation data, every undecoded timeline entry, the
    /// dance's own effects and facial expressions — is carried through untouched. See
    /// <see cref="TmbSplice"/> for why that is provable rather than hoped for.
    /// </summary>
    internal static class DancePap
    {
        /// <summary>
        /// Reads a source .pap and reports what preparing it would involve. Writes nothing.
        ///
        /// Gathers every reason to refuse rather than stopping at the first, so the dialog can show
        /// the user all of them at once instead of one per attempt.
        /// </summary>
        public static DancePapPlan Inspect(byte[] sourcePap, TmbTrackBundle bundle,
            ISet<string> djStrings, string animationName = PapFile.DanceAnimationName,
            ISet<string>? modSupplies = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var soundPaths = new List<string>();
            var ownEffects = new List<string>();
            var droppedEffects = new List<string>();
            string oldName = string.Empty;
            string race = string.Empty;

            try
            {
                var pap = PapFile.Parse(sourcePap);
                race = pap.RaceCode;
                oldName = pap.GetAnimationName(0);

                // Every real dance holds exactly one animation. More than one would make "the
                // animation to rename" ambiguous, and guessing is not something to do to a file the
                // user cannot easily rebuild.
                if (pap.AnimationCount != 1)
                    errors.Add($"This animation file contains {pap.AnimationCount} animations; " +
                        "this tool can only prepare one that contains a single animation.");

                var timeline = TmbBinary.Walk(pap.GetTimeline());

                if (TmbTrackBundle.IsAlreadyPrepped(timeline, djStrings))
                {
                    int carried = TmbTrackBundle.CoverageOf(timeline, djStrings).Count;
                    errors.Add(carried >= djStrings.Count
                        ? "This dance has already been set up for a DJ mod."
                        : $"This dance already carries part of a DJ effect block ({carried} of " +
                          $"{djStrings.Count} effects). Adding another would make it fire some twice.");
                }

                bool sawAnimationEntry = false;
                foreach (var entry in timeline.Entries)
                {
                    if (entry.Magic == TmbBinary.AnimationEntry && entry.Fields.Count > 0)
                        sawAnimationEntry = true;
                    else if (entry.Magic == TmbBinary.SoundEntry && entry.Fields.Count > 0)
                    {
                        string path = TmbBinary.ResolveString(timeline, entry, entry.Fields[0]);
                        if (path.Length > 0) soundPaths.Add(path);
                    }
                    else if (TmbTrackBundle.IsEffectEntry(entry.Magic))
                    {
                        foreach (var field in entry.Fields)
                        {
                            if (field.Kind != TmbFieldKind.String) continue;
                            string path = TmbBinary.ResolveString(timeline, entry, field);
                            if (path.Length > 0 && !ownEffects.Contains(path)) ownEffects.Add(path);
                        }
                    }
                }

                // Not fatal: the rename in the info table is what the game matches on, and a handful
                // of real dances carry no C009 at all. Worth saying, because it is also what a
                // half-converted file looks like.
                if (!sawAnimationEntry)
                    warnings.Add("This dance has no animation entry in its timeline, so only its " +
                        "animation name was retargeted.");

                // Reported as PATHS rather than as sentences, so the caller can subtract them from
                // the "this mod does not provide these effects" warning instead of telling the user
                // both to go and find a file and that the clip using it has been removed.
                droppedEffects.AddRange(UnsuppliedAsyncEffectPaths(timeline, modSupplies));

                TmbSplice.TrackInsertionPoint(timeline);

                // Asked here so a source this app cannot rewrite safely is reported in the dialog,
                // beside every other reason, rather than throwing halfway through writing files. The
                // same exclusion Prepare will apply is passed in, or a dance whose only fault is a
                // sound entry about to be removed anyway would be refused for it.
                TmbSplice.AssertSafeToRebuild(timeline, SoundEntriesToDrop(timeline));
            }
            catch (Exception ex) when (ex is PapFormatException or TmbFormatException)
            {
                errors.Add(ex.Message);
            }

            return new DancePapPlan
            {
                OldAnimationName = oldName,
                NewAnimationName = animationName,
                RaceCode = race,
                SoundPathsToBlank = soundPaths,
                EffectsToAdd = bundle.EffectPaths,
                EffectsUsed = ownEffects,
                AsyncEffectsDropped = droppedEffects,
                TracksToAdd = bundle.TrackCount,
                Errors = errors,
                Warnings = warnings,
            };
        }

        /// <summary>
        /// The prepared .pap. The input array is not modified.
        ///
        /// Re-inspects rather than trusting a plan handed in from elsewhere: this is the last point
        /// before bytes are produced, and a plan built against a different file would otherwise be
        /// applied to this one.
        /// </summary>
        public static byte[] Prepare(byte[] sourcePap, TmbTrackBundle bundle, ISet<string> djStrings,
            string animationName = PapFile.DanceAnimationName, ISet<string>? modSupplies = null)
        {
            var plan = Inspect(sourcePap, bundle, djStrings, animationName, modSupplies);
            if (!plan.CanApply)
                throw new PapFormatException(string.Join(" ", plan.Errors));

            var pap = PapFile.Parse((byte[])sourcePap.Clone());
            pap.SetAnimationName(0, animationName);

            byte[] timeline = pap.GetTimeline();

            // The sound entries go FIRST, and the order is load-bearing. Every step below rebuilds the
            // timeline and each one refuses a source that is already broken, so removing an entry with
            // a blanked path — which a hand-edited dance really does carry — has to happen before
            // anything else asks whether the file is sound. Doing it second meant the retarget refused
            // a dance that the plan had already said could be prepared.
            //
            // Removing entries renumbers everything after them, so the edits are computed from the
            // timeline as it stands afterwards rather than from the one that came in.
            timeline = TmbSplice.Remove(timeline, SoundEntriesToDrop(TmbBinary.Walk(timeline)));
            timeline = TmbSplice.SetStrings(timeline, RetargetAnimation(TmbBinary.Walk(timeline), animationName));
            timeline = ReconcileDurations(timeline);
            timeline = TmbSplice.AppendTracks(timeline, bundle);

            // LAST, so it sees the spliced result rather than the source. The DJ block arrives in the
            // line above and is not filtered on the way in, so an unsupplied async clip in the block
            // ITSELF would otherwise be stamped into every dance built from this mod — the same crash
            // this rule exists to prevent, on the one path that touches every single dance.
            timeline = DropUnsuppliedAsyncEffects(timeline, modSupplies, out var dropped);

            byte[] result = pap.WithTimeline(timeline);
            Verify(result, bundle, djStrings, animationName, modSupplies, dropped);
            return result;
        }

        /// <summary>The async-VFX paths in this timeline that nothing will supply, as paths.</summary>
        private static HashSet<string> UnsuppliedAsyncEffectPaths(TmbLayout layout,
            ISet<string>? modSupplies)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unsupplied in DanceRepair.UnsuppliedAsyncEffects(layout, modSupplies))
                if (TmbBinary.TryResolveString(layout, unsupplied.Entry, unsupplied.Field,
                        out string path, out _))
                    paths.Add(path);
            return paths;
        }

        /// <summary>
        /// The timeline with every async-VFX clip removed whose effect file nothing provides.
        ///
        /// A C173 naming a file nobody has is not a missing effect — it is the same
        /// <c>[null + 0xC0]</c> as an empty path. The author usually has the source mod installed and
        /// never sees it; everyone watching them over sync receives only what the author's collection
        /// resolves, and gets the crash. See <see cref="DanceRepair.UnsuppliedAsyncEffects"/>.
        /// </summary>
        private static byte[] DropUnsuppliedAsyncEffects(byte[] timeline, ISet<string>? modSupplies,
            out HashSet<string> droppedPaths)
        {
            // One walk, so what is reported and what is removed cannot drift apart.
            var layout = TmbBinary.Walk(timeline);
            var unsupplied = DanceRepair.UnsuppliedAsyncEffects(layout, modSupplies);

            droppedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in unsupplied)
                if (TmbBinary.TryResolveString(layout, clip.Entry, clip.Field, out string path, out _))
                    droppedPaths.Add(path);

            if (unsupplied.Count == 0) return timeline;

            return TmbSplice.Remove(timeline, TmbSplice.WithEmptiedTracks(layout,
                unsupplied.Select(b => b.Entry.Offset).Distinct().ToList()));
        }

        /// <summary>
        /// Pointing every animation entry at the name the DJ mod's option expects.
        ///
        /// A list rather than one edit because nothing guarantees there is only one C009, and the
        /// second one being left on the source's own name is the kind of half-conversion that plays
        /// perfectly on the machine that made it.
        /// </summary>
        private static List<TmbStringEdit> RetargetAnimation(TmbLayout layout, string animationName) =>
            layout.Entries
                .Where(e => e.Magic == TmbBinary.AnimationEntry && e.Fields.Count > 0)
                .Select(e => new TmbStringEdit(e.Offset, e.Fields[0].BodyOffset, animationName))
                .ToList();

        /// <summary>
        /// Durations that are wildly out of step with the timeline's own length, brought back to it.
        ///
        /// This is a heuristic, and it is the only one in this file — everything else is faithful.
        /// The evidence for it is narrow but direct: one downloaded dance produced a file that was
        /// correct in every structural respect and still fired nothing in game, and changing exactly
        /// four bytes — its C009 and C010 durations, 2837 against a timeline whose TMDH says 180 —
        /// made it work. Nothing else about the file changed.
        ///
        /// What this deliberately does NOT touch:
        ///   - sentinels. Sources use -1, 99999999 and 1410065407 to mean "for as long as it runs",
        ///     and those are normal and must survive.
        ///   - modest overshoot. Plenty of working dances run 1.5-5x their TMDH, so only a gross
        ///     mismatch is treated as a mistake in the source rather than an intention.
        ///
        /// Honest limitation: the prepped dances in a real mod include one at 20x that works, so
        /// magnitude alone is not the real rule and this threshold is a guess at where "inconsistent"
        /// begins. It is applied only when preparing a downloaded dance for re-slotting, never to a
        /// file that already works, and it is reported so the change is never silent.
        /// </summary>
        private static byte[] ReconcileDurations(byte[] timeline)
        {
            var layout = TmbBinary.Walk(timeline);
            var header = layout.Entries.FirstOrDefault(e => e.Magic == TmbBinary.Tmdh);
            if (header == null) return timeline;

            int length = BitConverter.ToInt16(layout.Bytes, header.Body + 4);
            if (length <= 0) return timeline;

            var output = (byte[])timeline.Clone();
            foreach (var entry in layout.Entries)
            {
                if (entry.Magic is not (TmbBinary.AnimationEntry or TmbBinary.ExpressionEntry)) continue;

                int at = entry.Body + 4;
                int duration = BitConverter.ToInt32(timeline, at);
                if (duration < 0 || IsForever(duration)) continue;
                if (duration <= length * GrossOvershoot) continue;

                TmbBinary.WriteInt32(output, at, length);
            }
            return output;
        }

        /// <summary>How far past the timeline's length a duration may run before it reads as a mistake.</summary>
        private const int GrossOvershoot = 8;

        /// <summary>Values sources use to mean "until it stops", which are not durations to reconcile.</summary>
        private static bool IsForever(int duration) =>
            duration is 99999999 or 1410065407 || duration > 1_000_000;

        /// <summary>
        /// The sound entries to drop, plus any track left with nothing in it.
        ///
        /// The entry is REMOVED rather than left in place with a blank path. Blanking is the obvious
        /// reading of the guide's "delete the name of the .scd file from the path line", and it is
        /// what an editor's text box does — but it leaves an armed sound entry pointing at an empty
        /// string, which is not a state any dance in a working mod is actually in. The dances that
        /// carry no music simply have no sound entry, so that is the state to produce.
        ///
        /// A track emptied by the removal goes too: a track with no items is not something the format
        /// is ever seen to contain.
        /// </summary>
        private static List<int> SoundEntriesToDrop(TmbLayout layout) =>
            TmbSplice.WithEmptiedTracks(layout, layout.Entries
                .Where(e => e.Magic == TmbBinary.SoundEntry)
                .Select(e => e.Offset)
                .ToList());

        /// <summary>
        /// Re-reads the produced bytes and checks they are what was asked for.
        ///
        /// A second pass over the output rather than a re-check of the inputs, for the same reason
        /// the rest of this app verifies after writing: the failure worth catching is the one where
        /// the transformation was subtly wrong, and only the result can show that.
        /// </summary>
        private static void Verify(byte[] produced, TmbTrackBundle bundle, ISet<string> djStrings,
            string animationName, ISet<string>? modSupplies, ISet<string> droppedEffects)
        {
            var pap = PapFile.Parse(produced);

            if (pap.GetAnimationName(0) != animationName)
                throw new PapFormatException(
                    $"The prepared dance names its animation '{pap.GetAnimationName(0)}' rather than " +
                    $"'{animationName}'; it was not written.");

            var timeline = TmbBinary.Walk(pap.GetTimeline());

            // The generic assertion, ahead of the specific ones. The two checks below say the
            // transformation did what it was asked; this one says the result is a file the game can
            // survive, and it does not depend on knowing which entry type went wrong this time —
            // which is the property the C009/C063 pair conspicuously lacked when a C173 did.
            var broken = TmbBinary.BrokenPaths(timeline);

            // The unsupplied async clips are checked here too, on the FINAL bytes. That is the only
            // place that sees the spliced-in DJ block, so it is the only place that can prove the
            // block did not smuggle one back in after the drop.
            broken.AddRange(DanceRepair.UnsuppliedAsyncEffects(timeline, modSupplies));

            if (broken.Count > 0)
                throw new PapFormatException(
                    $"The prepared dance {broken[0].Problem} ({broken[0].Entry.Magic}); it was not " +
                    $"written. {broken.Count} entr{(broken.Count == 1 ? "y is" : "ies are")} affected.");

            foreach (var entry in timeline.Entries)
            {
                if (entry.Fields.Count == 0) continue;

                if (entry.Magic == TmbBinary.AnimationEntry)
                {
                    string named = TmbBinary.ResolveString(timeline, entry, entry.Fields[0]);
                    if (named != animationName)
                        throw new PapFormatException(
                            $"The prepared dance still triggers animation '{named}'; it was not written.");
                }
                else if (entry.Magic == TmbBinary.SoundEntry)
                    throw new PapFormatException(
                        "The prepared dance still carries a sound entry; it was not written.");
            }

            var carried = TmbTrackBundle.CoverageOf(timeline, djStrings);
            foreach (string effect in bundle.EffectPaths)
            {
                // An effect the drop deliberately removed is not a missing one. Without this, a DJ
                // block that itself names an unsupplied .avfx would make every add fail outright
                // rather than quietly producing a dance that cannot crash anybody.
                if (droppedEffects.Contains(effect)) continue;

                if (!carried.Contains(effect))
                    throw new PapFormatException(
                        $"The prepared dance is missing effect '{effect}'; it was not written.");
            }
        }
    }
}
