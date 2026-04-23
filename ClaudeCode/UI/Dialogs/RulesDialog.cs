using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.UI.Dialogs
{
    /// <summary>
    /// Dialog for managing Claude Code rules and permissions.
    /// 4 tabs: Project Rules, Local Rules, Global Rules, Permissions.
    /// Port of com.anthropic.claude.intellij.ui.dialogs.RulesDialog.
    /// </summary>
    public class RulesDialog : Window
    {
        private readonly string _projectDir;
        private readonly TextBox _projectRulesEditor;
        private readonly TextBox _localRulesEditor;
        private readonly TextBox _globalRulesEditor;
        private readonly ListView _permissionsList;
        private readonly ComboBox _permFileCombo;
        private readonly ComboBox _permTypeCombo;
        private readonly TextBox _permPatternBox;

        private readonly string _projectRulesPath;
        private readonly string _localRulesPath;
        private readonly string _globalRulesPath;
        private readonly string _settingsJsonPath;
        private readonly string _settingsLocalJsonPath;

        public RulesDialog(string projectDir)
        {
            _projectDir = projectDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Title = "Claude Code - Rules & Permissions";
            Width = 750;
            Height = 550;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            _projectRulesPath = Path.Combine(_projectDir, "CLAUDE.md");
            _localRulesPath = Path.Combine(_projectDir, ".claude.local.md");
            _globalRulesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
            _settingsJsonPath = Path.Combine(_projectDir, ".claude", "settings.json");
            _settingsLocalJsonPath = Path.Combine(_projectDir, ".claude", "settings.local.json");

            var tabs = new TabControl { Margin = new Thickness(8) };

            // Tab 1: Project Rules
            _projectRulesEditor = CreateMarkdownEditor();
            tabs.Items.Add(CreateRulesTab("Project Rules", _projectRulesPath, _projectRulesEditor));

            // Tab 2: Local Rules
            _localRulesEditor = CreateMarkdownEditor();
            tabs.Items.Add(CreateRulesTab("Local Rules", _localRulesPath, _localRulesEditor));

            // Tab 3: Global Rules
            _globalRulesEditor = CreateMarkdownEditor();
            tabs.Items.Add(CreateRulesTab("Global Rules", _globalRulesPath, _globalRulesEditor));

            // Tab 4: Permissions
            _permissionsList = new ListView();
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Type", DisplayMemberBinding = new System.Windows.Data.Binding("Type"), Width = 80 });
            gridView.Columns.Add(new GridViewColumn { Header = "Rule Pattern", DisplayMemberBinding = new System.Windows.Data.Binding("Pattern"), Width = 300 });
            gridView.Columns.Add(new GridViewColumn { Header = "Source", DisplayMemberBinding = new System.Windows.Data.Binding("Source"), Width = 150 });
            _permissionsList.View = gridView;

            _permFileCombo = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            _permFileCombo.Items.Add("settings.local.json");
            _permFileCombo.Items.Add("settings.json");
            _permFileCombo.SelectedIndex = 0;

            _permTypeCombo = new ComboBox { Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            _permTypeCombo.Items.Add("allow");
            _permTypeCombo.Items.Add("deny");
            _permTypeCombo.Items.Add("ask");
            _permTypeCombo.SelectedIndex = 0;

            _permPatternBox = new TextBox { Width = 200, Margin = new Thickness(0, 0, 8, 0) };

            var addBtn = new Button { Content = "Add", Width = 60, Margin = new Thickness(0, 0, 4, 0) };
            addBtn.Click += OnAddPermission;
            var removeBtn = new Button { Content = "Remove", Width = 70 };
            removeBtn.Click += OnRemovePermission;

            var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            controlRow.Children.Add(_permFileCombo);
            controlRow.Children.Add(_permTypeCombo);
            controlRow.Children.Add(_permPatternBox);
            controlRow.Children.Add(addBtn);
            controlRow.Children.Add(removeBtn);

            var permPanel = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(controlRow, Dock.Bottom);
            permPanel.Children.Add(controlRow);
            permPanel.Children.Add(_permissionsList);

            tabs.Items.Add(new TabItem { Header = "Permissions", Content = permPanel });

            // Buttons
            var saveBtn = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            saveBtn.Click += OnSave;
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttonRow.Children.Add(saveBtn);
            buttonRow.Children.Add(cancelBtn);

            var mainPanel = new DockPanel();
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            mainPanel.Children.Add(buttonRow);
            mainPanel.Children.Add(tabs);

            Content = mainPanel;
            LoadAll();
        }

        private TabItem CreateRulesTab(string header, string filePath, TextBox editor)
        {
            var exists = File.Exists(filePath);
            var statusText = exists ? "✓ File exists" : "⚠ Will be created on save";
            var status = new TextBlock
            {
                Text = $"{filePath}\n{statusText}",
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 11,
                Foreground = exists ? Brushes.Green : Brushes.Orange
            };

            var panel = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(status, Dock.Top);
            panel.Children.Add(status);
            panel.Children.Add(new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            return new TabItem { Header = header, Content = panel };
        }

        private TextBox CreateMarkdownEditor()
        {
            return new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void LoadAll()
        {
            _projectRulesEditor.Text = LoadFile(_projectRulesPath);
            _localRulesEditor.Text = LoadFile(_localRulesPath);
            _globalRulesEditor.Text = LoadFile(_globalRulesPath);
            LoadPermissions();
        }

        private string LoadFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        private void LoadPermissions()
        {
            _permissionsList.Items.Clear();
            LoadPermissionsFromFile(_settingsJsonPath, "settings.json");
            LoadPermissionsFromFile(_settingsLocalJsonPath, "settings.local.json");
        }

        private void LoadPermissionsFromFile(string path, string source)
        {
            try
            {
                if (!File.Exists(path)) return;
                var json = JObject.Parse(File.ReadAllText(path));
                var perms = json["permissions"] as JObject;
                if (perms == null) return;

                foreach (var type in new[] { "allow", "deny", "ask" })
                {
                    var arr = perms[type] as JArray;
                    if (arr == null) continue;
                    foreach (var item in arr)
                        _permissionsList.Items.Add(new PermissionRow { Type = type, Pattern = item.ToString(), Source = source });
                }
            }
            catch { }
        }

        private void OnAddPermission(object sender, RoutedEventArgs e)
        {
            var pattern = _permPatternBox.Text.Trim();
            if (string.IsNullOrEmpty(pattern)) return;
            _permissionsList.Items.Add(new PermissionRow
            {
                Type = _permTypeCombo.SelectedItem?.ToString() ?? "allow",
                Pattern = pattern,
                Source = _permFileCombo.SelectedItem?.ToString() ?? "settings.local.json"
            });
            _permPatternBox.Text = "";
        }

        private void OnRemovePermission(object sender, RoutedEventArgs e)
        {
            if (_permissionsList.SelectedItem != null)
                _permissionsList.Items.Remove(_permissionsList.SelectedItem);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            SaveFile(_projectRulesPath, _projectRulesEditor.Text);
            SaveFile(_localRulesPath, _localRulesEditor.Text);
            SaveFile(_globalRulesPath, _globalRulesEditor.Text);
            SavePermissions();
            DialogResult = true;
            Close();
        }

        private void SaveFile(string path, string content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content);
            }
            catch { }
        }

        private void SavePermissions()
        {
            var byFile = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (PermissionRow row in _permissionsList.Items)
            {
                if (!byFile.ContainsKey(row.Source))
                    byFile[row.Source] = new Dictionary<string, List<string>>();
                if (!byFile[row.Source].ContainsKey(row.Type))
                    byFile[row.Source][row.Type] = new List<string>();
                byFile[row.Source][row.Type].Add(row.Pattern);
            }

            byFile.TryGetValue("settings.json", out var sjPerms);
            byFile.TryGetValue("settings.local.json", out var slPerms);
            SavePermissionsToFile(_settingsJsonPath, sjPerms);
            SavePermissionsToFile(_settingsLocalJsonPath, slPerms);
        }

        private void SavePermissionsToFile(string path, Dictionary<string, List<string>>? perms)
        {
            try
            {
                JObject json;
                if (File.Exists(path))
                    json = JObject.Parse(File.ReadAllText(path));
                else
                    json = new JObject();

                if (perms != null && perms.Count > 0)
                {
                    var permObj = new JObject();
                    foreach (var kv in perms)
                        permObj[kv.Key] = new JArray(kv.Value.ToArray());
                    json["permissions"] = permObj;
                }
                else
                {
                    json.Remove("permissions");
                }

                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json.ToString(Formatting.Indented));
            }
            catch { }
        }

        private class PermissionRow
        {
            public string Type { get; set; } = "";
            public string Pattern { get; set; } = "";
            public string Source { get; set; } = "";
        }
    }
}
