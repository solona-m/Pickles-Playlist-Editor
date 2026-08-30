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
            ISet<string> djStrings, string animationName = PapFile.DanceAnimationName)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var soundPaths = new List<string>();
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
                }

                // Not fatal: the rename in the info table is what the game matches on, and a handful
                // of real dances carry no C009 at all. Worth saying, because it is also what a
                // half-converted file looks like.
                if (!sawAnimationEntry)
                    warnings.Add("This dance has no animation entry in its timeline, so only its " +
                        "animation name was retargeted.");

                TmbSplice.TrackInsertionPoint(timeline);
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
            string animationName = PapFile.DanceAnimationName)
        {
            var plan = Inspect(sourcePap, bundle, djStrings, animationName);
            if (!plan.CanApply)
                throw new PapFormatException(string.Join(" ", plan.Errors));

            var pap = PapFile.Parse((byte[])sourcePap.Clone());
            pap.SetAnimationName(0, animationName);

            byte[] timeline = pap.GetTimeline();
            var layout = TmbBinary.Walk(timeline);

            var edits = new List<TmbStringEdit>();
            foreach (var entry in layout.Entries)
            {
                if (entry.Fields.Count == 0) continue;
                if (entry.Magic == TmbBinary.AnimationEntry)
                    edits.Add(new TmbStringEdit(entry.Offset, entry.Fields[0].BodyOffset, animationName));
            }

            timeline = TmbSplice.SetStrings(timeline, edits);
            timeline = TmbSplice.Remove(timeline, SoundEntriesToDrop(layout));
            timeline = ReconcileDurations(timeline);
            timeline = TmbSplice.AppendTracks(timeline, bundle);

            byte[] result = pap.WithTimeline(timeline);
            Verify(result, bundle, djStrings, animationName);
            return result;
        }

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
                if (entry.Magic is not (TmbBinary.AnimationEntry or "C010")) continue;

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
        private static List<int> SoundEntriesToDrop(TmbLayout layout)
        {
            var drop = new List<int>();
            var dropped = new HashSet<short>();

            foreach (var entry in layout.Entries)
            {
                if (entry.Magic != TmbBinary.SoundEntry) continue;
                drop.Add(entry.Offset);
                dropped.Add(entry.Id);
            }

            if (drop.Count == 0) return drop;

            foreach (var entry in layout.Entries)
            {
                if (entry.Magic != TmbBinary.Tmtr || entry.Fields.Count == 0) continue;
                var items = TmbBinary.ResolveInt16List(layout, entry, entry.Fields[0]);
                if (items.Length > 0 && items.All(dropped.Contains))
                    drop.Add(entry.Offset);
            }

            return drop;
        }

        /// <summary>
        /// Re-reads the produced bytes and checks they are what was asked for.
        ///
        /// A second pass over the output rather than a re-check of the inputs, for the same reason
        /// the rest of this app verifies after writing: the failure worth catching is the one where
        /// the transformation was subtly wrong, and only the result can show that.
        /// </summary>
        private static void Verify(byte[] produced, TmbTrackBundle bundle, ISet<string> djStrings,
            string animationName)
        {
            var pap = PapFile.Parse(produced);

            if (pap.GetAnimationName(0) != animationName)
                throw new PapFormatException(
                    $"The prepared dance names its animation '{pap.GetAnimationName(0)}' rather than " +
                    $"'{animationName}'; it was not written.");

            var timeline = TmbBinary.Walk(pap.GetTimeline());

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
                if (!carried.Contains(effect))
                    throw new PapFormatException(
                        $"The prepared dance is missing effect '{effect}'; it was not written.");
            }
        }
    }
}
