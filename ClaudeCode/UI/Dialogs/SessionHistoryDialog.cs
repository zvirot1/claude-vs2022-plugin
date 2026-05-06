using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCode.Model;
using ClaudeCode.Session;

namespace ClaudeCode.UI.Dialogs
{
    /// <summary>
    /// IntelliJ-style session history dialog with:
    ///  - 4-column sortable table (Date, Summary, Model, Messages)
    ///  - Search filter
    ///  - Preview pane with full session metadata
    ///  - Resume / Delete / Refresh / Rename / Close buttons
    /// Port of com.anthropic.claude.intellij.ui.dialogs.SessionHistoryDialog with added Rename.
    /// </summary>
    public class SessionHistoryDialog : Window
    {
        private readonly ClaudeSessionManager _sessionManager;
        private readonly ListView _listView;
        private readonly TextBox _searchBox;
        private readonly TextBox _previewBox;
        private readonly ObservableSessionList _items;

        /// <summary>Selected session ID after Resume button is clicked, or null if cancelled.</summary>
        public string? SelectedSessionId { get; private set; }

        public SessionHistoryDialog(ClaudeSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _items = new ObservableSessionList();

            Title = "Claude Code — Session History";
            Width = 950;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            // Root layout: Top toolbar / split content / bottom buttons
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Top toolbar: Filter + Refresh
            var toolbar = new DockPanel { Margin = new Thickness(8, 8, 8, 4), LastChildFill = true };
            var refreshBtn = new Button { Content = "Refresh", Width = 70, Margin = new Thickness(8, 0, 0, 0) };
            refreshBtn.Click += (_, __) => LoadSessions();
            DockPanel.SetDock(refreshBtn, Dock.Right);
            toolbar.Children.Add(refreshBtn);

            var filterLabel = new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(filterLabel, Dock.Left);
            toolbar.Children.Add(filterLabel);

            _searchBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
            _searchBox.TextChanged += OnFilterChanged;
            toolbar.Children.Add(_searchBox);
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            // Middle: Split (left=table, right=preview)
            var split = new Grid { Margin = new Thickness(8, 4, 8, 4) };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: ListView with GridView
            _listView = new ListView();
            var gv = new GridView();
            gv.Columns.Add(MakeColumn("Date", 130, "DateText"));
            gv.Columns.Add(MakeColumn("Summary", 270, "Summary"));
            gv.Columns.Add(MakeColumn("Model", 100, "ModelShort"));
            gv.Columns.Add(MakeColumn("Messages", 60, "MessageCount"));
            _listView.View = gv;
            _listView.ItemsSource = _items.View;
            _listView.SelectionChanged += OnSelectionChanged;
            _listView.MouseDoubleClick += OnDoubleClick;
            // Sortable column headers
            foreach (var col in gv.Columns)
            {
                if (col.Header is string hdr)
                {
                    var binding = (Binding)col.DisplayMemberBinding;
                    var prop = binding?.Path?.Path ?? hdr;
                    GridViewColumnHeader header = new GridViewColumnHeader { Content = hdr };
                    header.Click += (_, __) => SortBy(prop);
                    col.Header = header;
                }
            }
            Grid.SetColumn(_listView, 0);
            split.Children.Add(_listView);

            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = SystemColors.ControlBrush };
            Grid.SetColumn(splitter, 1);
            split.Children.Add(splitter);

            // Right: Preview text box
            _previewBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ControlBrush,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
                Text = "Select a session to view details..."
            };
            Grid.SetColumn(_previewBox, 2);
            split.Children.Add(_previewBox);

            Grid.SetRow(split, 1);
            root.Children.Add(split);

            // Bottom buttons
            var buttonBar = new DockPanel { Margin = new Thickness(8, 4, 8, 8), LastChildFill = false };
            var renameBtn = new Button { Content = "Rename...", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
            renameBtn.Click += (_, __) => OnRename();
            DockPanel.SetDock(renameBtn, Dock.Left);
            buttonBar.Children.Add(renameBtn);

            var deleteBtn = new Button { Content = "Delete", Width = 90 };
            deleteBtn.Click += (_, __) => OnDelete();
            DockPanel.SetDock(deleteBtn, Dock.Left);
            buttonBar.Children.Add(deleteBtn);

            var closeBtn = new Button { Content = "Close", Width = 90, IsCancel = true };
            DockPanel.SetDock(closeBtn, Dock.Right);
            buttonBar.Children.Add(closeBtn);

            var resumeBtn = new Button { Content = "Resume", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            resumeBtn.Click += (_, __) => OnResume();
            DockPanel.SetDock(resumeBtn, Dock.Right);
            buttonBar.Children.Add(resumeBtn);

            Grid.SetRow(buttonBar, 2);
            root.Children.Add(buttonBar);

            Content = root;
            Loaded += (_, __) => LoadSessions();
        }

        #region Data Loading & Filtering

        private GridViewColumn MakeColumn(string header, double width, string property)
        {
            return new GridViewColumn
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new Binding(property)
            };
        }

        private void LoadSessions()
        {
            _items.Clear();
            try
            {
                var sessions = _sessionManager.ListSessions();
                foreach (var s in sessions)
                {
                    _items.Add(new SessionRow(s));
                }
            }
            catch { }
            UpdatePreview();
        }

        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            var filter = _searchBox.Text?.Trim() ?? "";
            _items.View.Filter = obj =>
            {
                if (string.IsNullOrEmpty(filter)) return true;
                if (obj is SessionRow row)
                {
                    return (row.Summary?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (row.ModelShort?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (row.DateText?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return false;
            };
        }

        private string? _lastSortProp;
        private bool _sortAscending;
        private void SortBy(string property)
        {
            if (_lastSortProp == property) _sortAscending = !_sortAscending;
            else { _lastSortProp = property; _sortAscending = false; }
            _items.View.SortDescriptions.Clear();
            _items.View.SortDescriptions.Add(new SortDescription(property,
                _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        }

        #endregion

        #region Preview

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            var selected = _listView.SelectedItem as SessionRow;
            if (selected == null)
            {
                _previewBox.Text = "Select a session to view details...";
                return;
            }
            var s = selected.Info;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Session ID:  {s.SessionId}");
            sb.AppendLine($"Model:       {s.Model ?? "(unknown)"}");
            sb.AppendLine($"Working Dir: {s.WorkingDirectory ?? "(none)"}");
            sb.AppendLine($"Permissions: {s.PermissionMode ?? "default"}");
            sb.AppendLine($"Messages:    {s.MessageCount}");
            sb.AppendLine($"Started:     {FormatTime(s.StartTime)}");
            sb.AppendLine($"Last Active: {FormatTime(s.LastActiveTime)}");
            sb.AppendLine();
            sb.AppendLine("Summary:");
            sb.AppendLine(string.IsNullOrEmpty(s.Summary) ? "(none)" : s.Summary);
            sb.AppendLine();
            sb.AppendLine("Double-click or press 'Resume' to continue this session.");
            _previewBox.Text = sb.ToString();
        }

        private static string FormatTime(long unixMs)
        {
            if (unixMs <= 0) return "(unknown)";
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            return dt.ToString("yyyy-MM-dd HH:mm");
        }

        #endregion

        #region Actions

        private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnResume();

        private void OnResume()
        {
            var selected = _listView.SelectedItem as SessionRow;
            if (selected == null) return;
            SelectedSessionId = selected.Info.SessionId;
            DialogResult = true;
            Close();
        }

        private void OnDelete()
        {
            var selected = _listView.SelectedItem as SessionRow;
            if (selected == null) return;
            var name = string.IsNullOrEmpty(selected.Summary) ? selected.Info.SessionId ?? "(unnamed)" : selected.Summary;
            var result = MessageBox.Show(this,
                $"Delete session \"{name}\"?\n\nThis removes the local metadata. The CLI session file may remain in ~/.claude/.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            if (!string.IsNullOrEmpty(selected.Info.SessionId))
                _sessionManager.DeleteSession(selected.Info.SessionId!);
            LoadSessions();
        }

        private void OnRename()
        {
            var selected = _listView.SelectedItem as SessionRow;
            if (selected == null) return;
            var oldName = selected.Summary ?? "";
            var newName = ShowInputDialog("Rename Session", "New name:", oldName);
            if (newName == null || newName == oldName) return;
            if (!string.IsNullOrEmpty(selected.Info.SessionId))
            {
                _sessionManager.RenameSession(selected.Info.SessionId!, newName);
                LoadSessions();
            }
        }

        /// <summary>
        /// Shows a small modal input dialog asking for a single text string.
        /// Returns the entered string (possibly empty) on OK, or null on Cancel.
        /// </summary>
        private string? ShowInputDialog(string title, string label, string defaultValue)
        {
            var dlg = new Window
            {
                Owner = this,
                Title = title,
                Width = 400,
                Height = 160,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(lbl, 0);
            grid.Children.Add(lbl);

            var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 8) };
            input.SelectAll();
            input.Focus();
            Grid.SetRow(input, 1);
            grid.Children.Add(input);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            string? result = null;
            okBtn.Click += (_, __) => { result = input.Text; dlg.DialogResult = true; dlg.Close(); };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 3);
            grid.Children.Add(btnPanel);

            dlg.Content = grid;
            dlg.Loaded += (_, __) => input.Focus();
            return dlg.ShowDialog() == true ? result : null;
        }

        #endregion

        #region Row & Collection wrapper

        /// <summary>Wrapper around SessionInfo with formatted properties for the ListView.</summary>
        private class SessionRow
        {
            public SessionInfo Info { get; }
            public string DateText { get; }
            public string Summary { get; }
            public string ModelShort { get; }
            public int MessageCount { get; }

            public SessionRow(SessionInfo info)
            {
                Info = info;
                MessageCount = info.MessageCount;
                // IntelliJ port (b97fbe6): defensively strip noise prefixes here too, in case
                // the SessionStore was populated by an earlier version that didn't clean them.
                var raw = ClaudeCode.Session.SessionJsonlLoader.StripPrependedNoise(info.Summary ?? "");
                Summary = string.IsNullOrEmpty(raw) ? "(no summary)" : raw;
                ModelShort = ShortenModel(info.Model);
                DateText = info.LastActiveTime > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(info.LastActiveTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                    : "";
            }

            private static string ShortenModel(string? model)
            {
                if (string.IsNullOrEmpty(model)) return "?";
                var s = model!;
                if (s.StartsWith("claude-")) s = s.Substring(7);
                // Strip trailing date suffix like "-20250514"
                var idx = s.LastIndexOf('-');
                if (idx > 0 && idx < s.Length - 1)
                {
                    var tail = s.Substring(idx + 1);
                    if (tail.Length == 8 && long.TryParse(tail, out _))
                        s = s.Substring(0, idx);
                }
                return s;
            }
        }

        private class ObservableSessionList
        {
            private readonly System.Collections.ObjectModel.ObservableCollection<SessionRow> _list = new();
            public ICollectionView View { get; }
            public ObservableSessionList()
            {
                View = CollectionViewSource.GetDefaultView(_list);
                // Default sort: most recent first
                View.SortDescriptions.Add(new SortDescription("DateText", ListSortDirection.Descending));
            }
            public void Add(SessionRow row) => _list.Add(row);
            public void Clear() => _list.Clear();
        }

        #endregion
    }
}
