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
        private string _aiText = "";
        private string _aiTooltip = "";
        private string _aiAutomationName = "";
        private string? _songTooltip;
        private string _songHelpText = "";
        private SolidColorBrush? _aiBrush;
        private Visibility _aiVisibility = Visibility.Collapsed;

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

        /// <summary>A song's SCD path relative to the mod, so its chips can be refreshed in place.</summary>
        public string ScdRel { get; set; } = "";

        // AI-likelihood chip, songs only. Collapsed unless the setting is on and a score is cached.
        public string AiText
        {
            get => _aiText;
            set { _aiText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiText))); }
        }

        public string AiTooltip
        {
            get => _aiTooltip;
            set { _aiTooltip = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiTooltip))); }
        }

        /// <summary>What a screen reader says for the chip: the band and the score.</summary>
        public string AiAutomationName
        {
            get => _aiAutomationName;
            set { _aiAutomationName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiAutomationName))); }
        }

        /// <summary>Artist, album and AI likelihood; null means no tooltip. Raised only on change, so an open tooltip doesn't flicker.</summary>
        public string? SongTooltip
        {
            get => _songTooltip;
            set { if (string.Equals(_songTooltip, value, StringComparison.Ordinal)) return; _songTooltip = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SongTooltip))); }
        }

        /// <summary>The tooltip for screen readers; empty when there is none.</summary>
        public string SongHelpText
        {
            get => _songHelpText;
            set { if (string.Equals(_songHelpText, value, StringComparison.Ordinal)) return; _songHelpText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SongHelpText))); }
        }

        public SolidColorBrush? AiBrush
        {
            get => _aiBrush;
            set { _aiBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiBrush))); }
        }

        public Visibility AiVisibility
        {
            get => _aiVisibility;
            set { _aiVisibility = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiVisibility))); }
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
