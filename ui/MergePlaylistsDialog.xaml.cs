using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// Picks the playlists to fold into one target playlist.
    ///
    /// The target is whichever playlist is selected in the tree — the tree is single-select
    /// (MainWindow.xaml), so the sources are chosen here instead. Everything the merge will do is
    /// summarised live under the list, because WinUI permits only one ContentDialog at a time and
    /// this one therefore cannot raise a confirmation on top of itself.
    /// </summary>
    public sealed partial class MergePlaylistsDialog : ContentDialog
    {
        private readonly Playlist _target;
        private readonly List<Playlist> _candidates;

        // Internal, along with SelectedSources: exposing a Playlist-typed member publicly makes the
        // XAML markup compiler walk Playlist -> Option -> JArray and fail with WMC9999, because
        // Newtonsoft's JArray has no single collection interface it can bind to.
        internal MergePlaylistsDialog(Playlist target, IEnumerable<Playlist> candidates)
        {
            this.InitializeComponent();

            _target = target;
            _candidates = candidates.ToList();

            TargetHeader.Text = AppStrings.MergeTargetHeader(target.Name);

            foreach (var candidate in _candidates)
            {
                var box = new CheckBox
                {
                    Content = AppStrings.MergeSourceEntry(candidate.Name, SongCount(candidate)),
                    Tag = candidate.Name,
                    IsChecked = false,
                    MinHeight = 0,
                };
                // The primary button and the summary both depend on what is ticked, so unlike the
                // companion list this pattern came from, each box has to report changes.
                box.Checked += (_, _) => ValidateFields();
                box.Unchecked += (_, _) => ValidateFields();
                SourcePlaylistsPanel.Children.Add(box);
            }

            IsPrimaryButtonEnabled = false;
            ValidateFields();
        }

        /// <summary>The playlists the user ticked, in the order they are listed.</summary>
        internal List<Playlist> SelectedSources { get; private set; } = new();

        // "Off" is a placeholder Penumbra needs, not a song, so it never counts towards anything the
        // user is shown.
        private static int SongCount(Playlist playlist) =>
            playlist.Options?.Count(o => !string.Equals(o.Name, "Off", StringComparison.OrdinalIgnoreCase)) ?? 0;

        private List<Playlist> TickedSources()
        {
            var ticked = SourcePlaylistsPanel.Children
                .OfType<CheckBox>()
                .Where(box => box.IsChecked == true)
                .Select(box => box.Tag as string ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);

            // Driven off _candidates rather than the checkbox order so the merge order always matches
            // the order the list was built in.
            return _candidates.Where(p => ticked.Contains(p.Name)).ToList();
        }

        /// <summary>
        /// Recomputes the summary from the current ticks. This mirrors the first-wins de-duplication
        /// in <see cref="Playlist.MergeFrom"/> so the counts shown are the counts that will happen.
        /// </summary>
        private void ValidateFields()
        {
            var sources = TickedSources();
            IsPrimaryButtonEnabled = sources.Count > 0;

            if (sources.Count == 0)
            {
                SummaryText.Text = AppStrings.MergeSummaryNone;
                DiscardWarningText.Visibility = Visibility.Collapsed;
                return;
            }

            var seen = new HashSet<string>(
                Songs(_target).Select(Playlist.MergeKey), StringComparer.OrdinalIgnoreCase);

            int moving = 0;
            var discarded = new List<string>();
            foreach (var song in sources.SelectMany(Songs))
            {
                if (seen.Add(Playlist.MergeKey(song))) moving++;
                else discarded.Add(song.Name ?? string.Empty);
            }

            SummaryText.Text = AppStrings.MergeSummary(moving, sources.Count, _target.Name);

            if (discarded.Count == 0)
            {
                DiscardWarningText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Named, not just counted: two different tracks can share a name, and this is the
                // user's only chance to notice before the audio goes with the source playlist.
                DiscardWarningText.Text = AppStrings.MergeDiscardWarning(
                    discarded.Count, string.Join(", ", discarded.Take(5)));
                DiscardWarningText.Visibility = Visibility.Visible;
            }
        }

        private static IEnumerable<Option> Songs(Playlist playlist) =>
            (playlist.Options ?? new List<Option>())
                .Where(o => !string.Equals(o.Name, "Off", StringComparison.OrdinalIgnoreCase));

        private void MergeButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            SelectedSources = TickedSources();
        }
    }
}
