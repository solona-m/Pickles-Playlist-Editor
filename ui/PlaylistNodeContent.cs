using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Pickles_Playlist_Editor
{
    /// <summary>
    /// View model bound to TreeView via ItemsSource.
    /// </summary>
    public sealed class PlaylistNodeContent : INotifyPropertyChanged
    {
        private string _displayText = "";
        private bool _isExpanded;

        public string Name { get; set; } = "";

        public string DisplayText
        {
            get => _displayText;
            set
            {
                _displayText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        // 0 = root, 1 = playlist, 2 = song
        public int Level { get; set; }

        // Segoe Fluent Icons glyph code
        public string IconGlyph { get; set; } = "";

        public PlaylistNodeContent? Parent { get; private set; }
        public ObservableCollection<PlaylistNodeContent> Children { get; } = new();

        public void AddChild(PlaylistNodeContent child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static string PlaylistGlyph => "\uE142";  // FolderOpen
        public static string SongGlyph => "\uE8D6";      // MusicNote2
        public static string RootGlyph => "\uE8B7";      // Library

        public override string ToString() => DisplayText;
    }
}
