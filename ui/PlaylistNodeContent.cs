using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
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
        private bool _isSelected;
        private string _camelotText = "";
        private SolidColorBrush? _camelotBrush;
        private Visibility _camelotVisibility = Visibility.Collapsed;

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

        // Camelot chip, songs only. Collapsed unless the key is cached and maps to the wheel.
        public string CamelotText
        {
            get => _camelotText;
            set
            {
                _camelotText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CamelotText)));
            }
        }

        public SolidColorBrush? CamelotBrush
        {
            get => _camelotBrush;
            set
            {
                _camelotBrush = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CamelotBrush)));
            }
        }

        public Visibility CamelotVisibility
        {
            get => _camelotVisibility;
            set
            {
                _camelotVisibility = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CamelotVisibility)));
            }
        }

        // Selection lives here, not on the TreeView: WinUI offers Single or a checkbox per row and
        // nothing in between, so ctrl/shift selection is tracked by MainWindow and drawn from this.
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionVisibility)));
            }
        }

        public Visibility SelectionVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

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

        public void InsertChild(int index, PlaylistNodeContent child)
        {
            child.Parent = this;
            Children.Insert(Math.Clamp(index, 0, Children.Count), child);
        }

        public void RemoveChild(PlaylistNodeContent child)
        {
            if (Children.Remove(child))
                child.Parent = null;
        }

        public void ReplaceChildren(IEnumerable<PlaylistNodeContent> newChildren)
        {
            foreach (var existing in Children)
                existing.Parent = null;
            Children.Clear();
            foreach (var child in newChildren)
                AddChild(child);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static string PlaylistGlyph => "\uE142";  // FolderOpen
        public static string SongGlyph => "\uE8D6";      // MusicNote2
        public static string RootGlyph => "\uE8B7";      // Library

        public override string ToString() => DisplayText;
    }
}
