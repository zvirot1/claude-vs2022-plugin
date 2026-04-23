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
    /// Dialog for managing Claude Code hooks (PreToolUse, PostToolUse, SessionStart, Stop).
    /// Port of com.anthropic.claude.intellij.ui.dialogs.HooksDialog.
    /// </summary>
    public class HooksDialog : Window
    {
        private readonly string _projectDir;
        private readonly ListView _hooksList;
        private readonly ComboBox _fileCombo;
        private readonly ComboBox _eventCombo;
        private readonly TextBox _matcherBox;
        private readonly TextBox _commandBox;

        private readonly string _settingsPath;
        private readonly string _settingsLocalPath;

        public HooksDialog(string projectDir)
        {
            _projectDir = projectDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Title = "Claude Code - Hooks";
            Width = 750;
            Height = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            _settingsPath = Path.Combine(_projectDir, ".claude", "settings.json");
            _settingsLocalPath = Path.Combine(_projectDir, ".claude", "settings.local.json");

            var desc = new TextBlock
            {
                Text = "Hooks run shell commands before/after tool calls and on session events.\nEach hook has an event type, optional matcher (regex for tool name), and a command.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 12
            };

            _hooksList = new ListView();
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Event", DisplayMemberBinding = new System.Windows.Data.Binding("Event"), Width = 110 });
            gridView.Columns.Add(new GridViewColumn { Header = "Matcher", DisplayMemberBinding = new System.Windows.Data.Binding("Matcher"), Width = 120 });
            gridView.Columns.Add(new GridViewColumn { Header = "Command", DisplayMemberBinding = new System.Windows.Data.Binding("Command"), Width = 280 });
            gridView.Columns.Add(new GridViewColumn { Header = "Source", DisplayMemberBinding = new System.Windows.Data.Binding("Source"), Width = 130 });
            _hooksList.View = gridView;

            _fileCombo = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 4, 0) };
            _fileCombo.Items.Add("settings.local.json");
            _fileCombo.Items.Add("settings.json");
            _fileCombo.SelectedIndex = 0;

            _eventCombo = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 4, 0) };
            foreach (var evt in new[] { "PreToolUse", "PostToolUse", "SessionStart", "Stop" })
                _eventCombo.Items.Add(evt);
            _eventCombo.SelectedIndex = 0;

            _matcherBox = new TextBox { Width = 90, Margin = new Thickness(0, 0, 4, 0) };
            _matcherBox.SetValue(System.Windows.Controls.Primitives.TextBoxBase.IsUndoEnabledProperty, true);

            _commandBox = new TextBox { Width = 180, Margin = new Thickness(0, 0, 4, 0), FontFamily = new FontFamily("Consolas") };

            var addBtn = new Button { Content = "Add", Width = 50, Margin = new Thickness(0, 0, 4, 0) };
            addBtn.Click += OnAddHook;
            var removeBtn = new Button { Content = "Remove", Width = 65 };
            removeBtn.Click += OnRemoveHook;

            var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            controlRow.Children.Add(_fileCombo);
            controlRow.Children.Add(_eventCombo);
            controlRow.Children.Add(new TextBlock { Text = "Matcher:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            controlRow.Children.Add(_matcherBox);
            controlRow.Children.Add(new TextBlock { Text = "Cmd:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            controlRow.Children.Add(_commandBox);
            controlRow.Children.Add(addBtn);
            controlRow.Children.Add(removeBtn);

            var saveBtn = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            saveBtn.Click += OnSave;
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttonRow.Children.Add(saveBtn);
            buttonRow.Children.Add(cancelBtn);

            var mainPanel = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(desc, Dock.Top);
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            DockPanel.SetDock(controlRow, Dock.Bottom);
            mainPanel.Children.Add(desc);
            mainPanel.Children.Add(buttonRow);
            mainPanel.Children.Add(controlRow);
            mainPanel.Children.Add(_hooksList);

            Content = mainPanel;
            LoadAll();
        }

        private void LoadAll()
        {
            _hooksList.Items.Clear();
            LoadHooksFromFile(_settingsPath, "settings.json");
            LoadHooksFromFile(_settingsLocalPath, "settings.local.json");
        }

        private void LoadHooksFromFile(string path, string source)
        {
            try
            {
                if (!File.Exists(path)) return;
                var json = JObject.Parse(File.ReadAllText(path));
                var hooks = json["hooks"] as JObject;
                if (hooks == null) return;

                foreach (var kv in hooks)
                {
                    var eventType = kv.Key;
                    var arr = kv.Value as JArray;
                    if (arr == null) continue;

                    foreach (var item in arr)
                    {
                        if (item is JObject hookObj)
                        {
                            _hooksList.Items.Add(new HookRow
                            {
                                Event = eventType,
                                Matcher = hookObj.Value<string>("matcher") ?? "",
                                Command = hookObj.Value<string>("command") ?? "",
                                Source = source
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void OnAddHook(object sender, RoutedEventArgs e)
        {
            var cmd = _commandBox.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            _hooksList.Items.Add(new HookRow
            {
                Event = _eventCombo.SelectedItem?.ToString() ?? "PreToolUse",
                Matcher = _matcherBox.Text.Trim(),
                Command = cmd,
                Source = _fileCombo.SelectedItem?.ToString() ?? "settings.local.json"
            });
            _commandBox.Text = "";
            _matcherBox.Text = "";
        }

        private void OnRemoveHook(object sender, RoutedEventArgs e)
        {
            if (_hooksList.SelectedItem != null)
                _hooksList.Items.Remove(_hooksList.SelectedItem);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var byFile = new Dictionary<string, Dictionary<string, List<JObject>>>();
            foreach (HookRow row in _hooksList.Items)
            {
                if (!byFile.ContainsKey(row.Source))
                    byFile[row.Source] = new Dictionary<string, List<JObject>>();
                if (!byFile[row.Source].ContainsKey(row.Event))
                    byFile[row.Source][row.Event] = new List<JObject>();

                var hookObj = new JObject { ["type"] = "command", ["command"] = row.Command };
                if (!string.IsNullOrEmpty(row.Matcher))
                    hookObj["matcher"] = row.Matcher;
                byFile[row.Source][row.Event].Add(hookObj);
            }

            byFile.TryGetValue("settings.json", out var sjHooks);
            byFile.TryGetValue("settings.local.json", out var slHooks);
            SaveHooksToFile(_settingsPath, sjHooks);
            SaveHooksToFile(_settingsLocalPath, slHooks);
            DialogResult = true;
            Close();
        }

        private void SaveHooksToFile(string path, Dictionary<string, List<JObject>>? hooks)
        {
            try
            {
                JObject json;
                if (File.Exists(path))
                    json = JObject.Parse(File.ReadAllText(path));
                else
                    json = new JObject();

                if (hooks != null && hooks.Count > 0)
                {
                    var hooksObj = new JObject();
                    foreach (var kv in hooks)
                        hooksObj[kv.Key] = new JArray(kv.Value.ToArray());
                    json["hooks"] = hooksObj;
                }
                else
                {
                    json.Remove("hooks");
                }

                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json.ToString(Formatting.Indented));
            }
            catch { }
        }

        private class HookRow
        {
            public string Event { get; set; } = "";
            public string Matcher { get; set; } = "";
            public string Command { get; set; } = "";
            public string Source { get; set; } = "";
        }
    }
}
