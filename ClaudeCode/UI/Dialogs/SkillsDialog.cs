using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.UI.Dialogs
{
    /// <summary>
    /// Dialog for browsing and managing Claude Code plugins and skills.
    /// 3 tabs: Local Skills, Installed Plugins, Available Plugins.
    /// Right pane shows tabbed detail (Info / SKILL.md / LICENSE) — matching IntelliJ.
    /// </summary>
    public class SkillsDialog : Window
    {
        private readonly string _projectDir;
        private readonly string _home;

        // Local Skills
        private readonly ListView _skillsList;
        private readonly TabControl _skillDetailTabs;
        private readonly TextBox _skillInfoBox, _skillMdBox, _skillLicenseBox;

        // Installed Plugins
        private readonly ListView _installedList;
        private readonly TabControl _installedDetailTabs;
        private readonly TextBox _installedInfoBox, _installedReadmeBox;

        // Available Plugins
        private readonly ListView _availableList;
        private readonly TabControl _availableDetailTabs;
        private readonly TextBox _availableInfoBox, _availableReadmeBox;
        private readonly TextBox _searchBox;

        private readonly List<PluginRow> _allAvailable = new();

        public SkillsDialog(string projectDir)
        {
            _projectDir = projectDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Title = "Claude Code — Skills & Plugins";
            Width = 900;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var header = new StackPanel { Margin = new Thickness(12, 12, 12, 4) };
            header.Children.Add(new TextBlock { Text = "Skills & Plugins", FontWeight = FontWeights.Bold, FontSize = 15 });
            header.Children.Add(new TextBlock
            {
                Text = "Browse, enable, and manage Claude Code plugins and local skills.",
                FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0)
            });

            var mainTabs = new TabControl { Margin = new Thickness(8, 4, 8, 0) };

            // ═══════════════════ Tab 1: Local Skills ═══════════════════
            _skillsList = new ListView();
            SetupGridView(_skillsList, new[] { ("Skill", 150), ("Description", 270) });

            _skillInfoBox = MakeDetailBox();
            _skillMdBox = MakeDetailBox();
            _skillLicenseBox = MakeDetailBox();
            _skillDetailTabs = MakeDetailTabs(
                ("Info", _skillInfoBox), ("SKILL.md", _skillMdBox), ("LICENSE", _skillLicenseBox));

            var skillRefresh = Btn("Refresh", () => LoadLocalSkills());
            var skillOpen = Btn("Open Folder", OpenSkillsFolder);

            mainTabs.Items.Add(MakeSplitTab("Local Skills", _skillsList,
                _skillDetailTabs, new[] { skillRefresh, skillOpen }));

            _skillsList.SelectionChanged += (_, __) => ShowSkillDetail();

            // ═══════════════════ Tab 2: Installed Plugins ═══════════════════
            _installedList = new ListView();
            SetupGridView(_installedList, new[] { ("Plugin", 160), ("Version", 60), ("Source", 80), ("Enabled", 60) });

            _installedInfoBox = MakeDetailBox();
            _installedReadmeBox = MakeDetailBox();
            _installedDetailTabs = MakeDetailTabs(
                ("Info", _installedInfoBox), ("README", _installedReadmeBox));

            var instRefresh = Btn("Refresh", () => LoadInstalledPlugins());
            var instToggle = Btn("Toggle Enable", TogglePlugin);
            var instFolder = Btn("Open Folder", OpenPluginFolder);

            mainTabs.Items.Add(MakeSplitTab("Installed Plugins", _installedList,
                _installedDetailTabs, new[] { instRefresh, instToggle, instFolder }));

            _installedList.SelectionChanged += (_, __) => ShowInstalledDetail();

            // ═══════════════════ Tab 3: Available Plugins ═══════════════════
            _availableList = new ListView();
            SetupGridView(_availableList, new[] { ("Plugin", 180), ("Marketplace", 100), ("Installs", 70) });
            _availableList.MouseDoubleClick += (_, __) => InstallPlugin();

            _availableInfoBox = MakeDetailBox();
            _availableReadmeBox = MakeDetailBox();
            _availableDetailTabs = MakeDetailTabs(
                ("Info", _availableInfoBox), ("README", _availableReadmeBox));

            _searchBox = new TextBox { Width = 200, Margin = new Thickness(0, 0, 4, 0) };
            _searchBox.TextChanged += (_, __) => FilterAvailable();

            var avRefresh = Btn("Refresh", () => LoadAvailablePlugins());
            var avInstall = Btn("Install", InstallPlugin);
            var avCopy = Btn("Copy Command", CopyInstallCommand);

            // Available tab has search bar on top
            var avLeftPanel = new DockPanel();
            var avSearchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            avSearchRow.Children.Add(new TextBlock { Text = "Search: ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            avSearchRow.Children.Add(_searchBox);
            DockPanel.SetDock(avSearchRow, Dock.Top);

            var avToolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            avToolbar.Children.Add(avRefresh);
            avToolbar.Children.Add(avInstall);
            avToolbar.Children.Add(avCopy);
            DockPanel.SetDock(avToolbar, Dock.Bottom);

            avLeftPanel.Children.Add(avSearchRow);
            avLeftPanel.Children.Add(avToolbar);
            avLeftPanel.Children.Add(_availableList);

            mainTabs.Items.Add(MakeSplitTabRaw("Available Plugins", avLeftPanel, _availableDetailTabs));

            _availableList.SelectionChanged += (_, __) => ShowAvailableDetail();

            // ═══════════════════ Bottom buttons ═══════════════════
            var closeBtn = new Button { Content = "Close", Width = 80, IsCancel = true, Margin = new Thickness(8) };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnRow.Children.Add(closeBtn);

            var main = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            main.Children.Add(header);
            main.Children.Add(btnRow);
            main.Children.Add(mainTabs);

            Content = main;
            Loaded += (_, __) => { LoadLocalSkills(); LoadInstalledPlugins(); LoadAvailablePlugins(); };
        }

        #region UI Helpers

        private static void SetupGridView(ListView list, (string header, int width)[] columns)
        {
            var gv = new GridView();
            foreach (var (h, w) in columns)
                gv.Columns.Add(new GridViewColumn
                {
                    Header = h,
                    DisplayMemberBinding = new System.Windows.Data.Binding(h),
                    Width = w
                });
            list.View = gv;
        }

        private static TextBox MakeDetailBox() => new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6)
        };

        private static TabControl MakeDetailTabs(params (string header, TextBox box)[] tabs)
        {
            var tc = new TabControl { FontSize = 11 };
            foreach (var (header, box) in tabs)
                tc.Items.Add(new TabItem
                {
                    Header = header,
                    Content = new ScrollViewer
                    {
                        Content = box,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    }
                });
            return tc;
        }

        private static Button Btn(string caption, Action onClick)
        {
            var btn = new Button { Content = caption, MinWidth = 70, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2) };
            btn.Click += (_, __) => onClick();
            return btn;
        }

        private TabItem MakeSplitTab(string header, ListView list, TabControl detailTabs, Button[] buttons)
        {
            var leftPanel = new DockPanel();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var btn in buttons) toolbar.Children.Add(btn);
            DockPanel.SetDock(toolbar, Dock.Bottom);
            leftPanel.Children.Add(toolbar);
            leftPanel.Children.Add(list);

            return MakeSplitTabRaw(header, leftPanel, detailTabs);
        }

        private TabItem MakeSplitTabRaw(string header, UIElement left, UIElement right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            return new TabItem { Header = header, Content = new Border { Padding = new Thickness(8), Child = grid } };
        }

        #endregion

        #region Local Skills

        private void LoadLocalSkills()
        {
            _skillsList.Items.Clear();
            var dirs = new[]
            {
                Path.Combine(_projectDir, ".claude", "skills"),
                Path.Combine(_home, ".claude", "skills")
            };

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var skillDir in Directory.GetDirectories(dir))
                    {
                        var skillMd = Path.Combine(skillDir, "SKILL.md");
                        if (!File.Exists(skillMd)) continue;
                        var name = System.IO.Path.GetFileName(skillDir);
                        var firstLine = "";
                        try { using var sr = new StreamReader(skillMd); firstLine = sr.ReadLine() ?? ""; } catch { }
                        if (firstLine.StartsWith("#")) firstLine = firstLine.TrimStart('#').Trim();
                        _skillsList.Items.Add(new SkillRow { Skill = name, Description = firstLine, DirPath = skillDir });
                    }
                }
                catch { }
            }
        }

        private void ShowSkillDetail()
        {
            if (_skillsList.SelectedItem is not SkillRow row || row.DirPath == null) return;
            var skillMd = Path.Combine(row.DirPath, "SKILL.md");
            var license = Path.Combine(row.DirPath, "LICENSE.txt");
            if (!File.Exists(license)) license = Path.Combine(row.DirPath, "LICENSE");

            // Info tab
            var info = $"— Skill Info —\n\n";
            info += $"  Name:        {row.Skill}\n";
            info += $"  Path:        {row.DirPath}\n";
            info += $"  Description: {row.Description}\n\n";
            info += $"— Contents —\n\n";
            try
            {
                foreach (var f in Directory.GetFiles(row.DirPath))
                {
                    var fi = new FileInfo(f);
                    info += $"  {fi.Name,-30} {FormatSize(fi.Length)}\n";
                }
                foreach (var d in Directory.GetDirectories(row.DirPath))
                    info += $"  {System.IO.Path.GetFileName(d) + "/",-30} <dir>\n";
            }
            catch { }
            _skillInfoBox.Text = info;

            // SKILL.md tab
            _skillMdBox.Text = File.Exists(skillMd) ? File.ReadAllText(skillMd) : "(not found)";

            // LICENSE tab
            _skillLicenseBox.Text = File.Exists(license) ? File.ReadAllText(license) : "(no license file)";

            _skillDetailTabs.SelectedIndex = 0;
        }

        private void OpenSkillsFolder()
        {
            var dir = Path.Combine(_projectDir, ".claude", "skills");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            try { Process.Start("explorer.exe", dir); } catch { }
        }

        #endregion

        #region Installed Plugins

        private void LoadInstalledPlugins()
        {
            _installedList.Items.Clear();
            RunCliAsync("plugins list --json", output =>
            {
                try
                {
                    var arr = JArray.Parse(output);
                    foreach (JObject p in arr)
                    {
                        _installedList.Items.Add(new PluginRow
                        {
                            Plugin = p.Value<string>("name") ?? "",
                            Version = p.Value<string>("version") ?? "",
                            Source = p.Value<string>("scope") ?? "user",
                            Enabled = p.Value<bool?>("enabled")?.ToString() ?? "True",
                            Description = p.Value<string>("description") ?? "",
                            Marketplace = p.Value<string>("marketplace") ?? "",
                            InstallPath = p.Value<string>("path") ?? "",
                            InstalledAt = p.Value<string>("installedAt") ?? "",
                            RawJson = p
                        });
                    }
                }
                catch { _installedInfoBox.Text = "Failed to load plugins.\n\nRaw output:\n" + output; }
            });
        }

        private void ShowInstalledDetail()
        {
            if (_installedList.SelectedItem is not PluginRow row) return;

            var info = "— Plugin Info —\n\n";
            info += $"  ID:          {row.Plugin}\n";
            info += $"  Version:     {row.Version}\n";
            info += $"  Scope:       {row.Source}\n";
            info += $"  Marketplace: {row.Marketplace}\n";
            info += $"  Enabled:     {row.Enabled}\n";
            info += $"  Path:        {row.InstallPath}\n";
            if (!string.IsNullOrEmpty(row.InstalledAt))
                info += $"  Installed:   {row.InstalledAt}\n";
            info += $"\n— Description —\n\n  {row.Description}\n";

            // Try to read plugin.json for components
            if (!string.IsNullOrEmpty(row.InstallPath))
            {
                var pluginJson = Path.Combine(row.InstallPath, "plugin.json");
                if (File.Exists(pluginJson))
                {
                    try
                    {
                        var pj = JObject.Parse(File.ReadAllText(pluginJson));
                        info += "\n— Components —\n\n";
                        var skills = pj["skills"] as JArray;
                        var commands = pj["commands"] as JArray;
                        var hooks = pj["hooks"] as JArray;
                        var agents = pj["agents"] as JArray;
                        if (skills != null) info += $"  Skills:   {skills.Count}\n";
                        if (commands != null) info += $"  Commands: {commands.Count}\n";
                        if (hooks != null) info += $"  Hooks:    {hooks.Count}\n";
                        if (agents != null) info += $"  Agents:   {agents.Count}\n";
                    }
                    catch { }
                }
            }

            _installedInfoBox.Text = info;

            // README tab
            if (!string.IsNullOrEmpty(row.InstallPath))
            {
                var readme = Path.Combine(row.InstallPath, "README.md");
                _installedReadmeBox.Text = File.Exists(readme) ? File.ReadAllText(readme) : "(no README)";
            }
            else
            {
                _installedReadmeBox.Text = "(path unknown)";
            }

            _installedDetailTabs.SelectedIndex = 0;
        }

        private void TogglePlugin()
        {
            if (_installedList.SelectedItem is not PluginRow row) return;
            var action = row.Enabled == "True" ? "disable" : "enable";
            RunCliAsync($"plugins {action} {row.Plugin}", _ => LoadInstalledPlugins());
        }

        private void OpenPluginFolder()
        {
            if (_installedList.SelectedItem is not PluginRow row || string.IsNullOrEmpty(row.InstallPath)) return;
            try { Process.Start("explorer.exe", row.InstallPath); } catch { }
        }

        #endregion

        #region Available Plugins

        private void LoadAvailablePlugins()
        {
            _availableList.Items.Clear();
            _allAvailable.Clear();
            RunCliAsync("plugins list --available --json", output =>
            {
                try
                {
                    var arr = JArray.Parse(output);
                    foreach (JObject p in arr)
                    {
                        var row = new PluginRow
                        {
                            Plugin = p.Value<string>("name") ?? "",
                            Description = p.Value<string>("description") ?? "",
                            Marketplace = p.Value<string>("marketplace") ?? "",
                            Installs = p.Value<string>("installs") ?? p.Value<int?>("installs")?.ToString() ?? "",
                            Version = p.Value<string>("version") ?? "",
                            RawJson = p
                        };
                        _allAvailable.Add(row);
                        _availableList.Items.Add(row);
                    }
                }
                catch { _availableInfoBox.Text = "Failed to load.\n\nRaw:\n" + output; }
            });
        }

        private void ShowAvailableDetail()
        {
            if (_availableList.SelectedItem is not PluginRow row) return;

            // Check if already installed
            var installed = false;
            foreach (PluginRow inst in _installedList.Items)
            {
                if (inst.Plugin == row.Plugin) { installed = true; break; }
            }

            var info = "— Plugin Info —\n\n";
            info += $"  Status:      {(installed ? "✓ Installed" : "Not installed")}\n";
            info += $"  ID:          {row.Plugin}\n";
            if (!string.IsNullOrEmpty(row.Marketplace))
                info += $"  Marketplace: {row.Marketplace}\n";
            if (!string.IsNullOrEmpty(row.Version))
                info += $"  Version:     {row.Version}\n";
            if (!string.IsNullOrEmpty(row.Installs))
                info += $"  Installs:    {row.Installs}\n";
            info += $"\n— Description —\n\n  {row.Description}\n";
            info += $"\n— Install —\n\n  claude plugins install {row.Plugin}\n";
            _availableInfoBox.Text = info;

            _availableReadmeBox.Text = "(README will be available after installation)";
            _availableDetailTabs.SelectedIndex = 0;
        }

        private void FilterAvailable()
        {
            var filter = _searchBox.Text.Trim().ToLower();
            _availableList.Items.Clear();
            foreach (var row in _allAvailable)
            {
                if (string.IsNullOrEmpty(filter) ||
                    row.Plugin.ToLower().Contains(filter) ||
                    row.Description.ToLower().Contains(filter))
                    _availableList.Items.Add(row);
            }
        }

        private void InstallPlugin()
        {
            if (_availableList.SelectedItem is not PluginRow row) return;
            if (MessageBox.Show($"Install plugin '{row.Plugin}'?", "Confirm Install", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunCliAsync($"plugins install {row.Plugin}", _ => { LoadInstalledPlugins(); ShowAvailableDetail(); });
        }

        private void CopyInstallCommand()
        {
            if (_availableList.SelectedItem is not PluginRow row) return;
            try { Clipboard.SetText($"claude plugins install {row.Plugin}"); } catch { }
        }

        #endregion

        #region Helpers

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        private void RunCliAsync(string args, Action<string> onComplete)
        {
            var cliPath = Cli.ClaudeCliManager.GetCliPath();
            if (cliPath == null) return;

            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(cliPath, args)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        WorkingDirectory = _projectDir
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return;
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    Dispatcher.Invoke(() => onComplete(output));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => onComplete($"Error: {ex.Message}"));
                }
            });
        }

        #endregion

        #region Data Classes

        private class SkillRow
        {
            public string Skill { get; set; } = "";
            public string Description { get; set; } = "";
            public string? DirPath { get; set; }
        }

        private class PluginRow
        {
            public string Plugin { get; set; } = "";
            public string Version { get; set; } = "";
            public string Source { get; set; } = "";
            public string Enabled { get; set; } = "";
            public string Description { get; set; } = "";
            public string Marketplace { get; set; } = "";
            public string Installs { get; set; } = "";
            public string InstallPath { get; set; } = "";
            public string InstalledAt { get; set; } = "";
            public JObject? RawJson { get; set; }
        }

        #endregion
    }
}
