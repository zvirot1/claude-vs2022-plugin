using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.UI.Dialogs
{
    /// <summary>
    /// Dialog for managing MCP (Model Context Protocol) server configurations.
    /// 2 tabs: Project Servers (.mcp.json) and Global Servers (~/.claude.json).
    /// Port of com.anthropic.claude.intellij.ui.dialogs.McpServersDialog.
    /// </summary>
    public class McpServersDialog : Window
    {
        private readonly string _projectDir;
        private readonly ListView _projectList;
        private readonly ListView _globalList;
        private readonly string _projectPath;
        private readonly string _globalPath;

        public McpServersDialog(string projectDir)
        {
            _projectDir = projectDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Title = "Claude Code - MCP Servers";
            Width = 700;
            Height = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            _projectPath = Path.Combine(_projectDir, ".mcp.json");
            _globalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

            var tabs = new TabControl { Margin = new Thickness(8) };

            _projectList = new ListView();
            tabs.Items.Add(CreateServerTab("Project Servers", _projectPath, _projectList));

            _globalList = new ListView();
            tabs.Items.Add(CreateServerTab("Global Servers", _globalPath, _globalList));

            var saveBtn = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            saveBtn.Click += OnSave;
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttonRow.Children.Add(saveBtn);
            buttonRow.Children.Add(cancelBtn);

            var main = new DockPanel();
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            main.Children.Add(buttonRow);
            main.Children.Add(tabs);

            Content = main;
            LoadAll();
        }

        private TabItem CreateServerTab(string header, string filePath, ListView list)
        {
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new System.Windows.Data.Binding("Name"), Width = 140 });
            gridView.Columns.Add(new GridViewColumn { Header = "Command", DisplayMemberBinding = new System.Windows.Data.Binding("Command"), Width = 150 });
            gridView.Columns.Add(new GridViewColumn { Header = "Args", DisplayMemberBinding = new System.Windows.Data.Binding("Args"), Width = 180 });
            gridView.Columns.Add(new GridViewColumn { Header = "Env", DisplayMemberBinding = new System.Windows.Data.Binding("Env"), Width = 120 });
            list.View = gridView;

            var addBtn = new Button { Content = "Add Server", Width = 90, Margin = new Thickness(0, 0, 4, 0) };
            addBtn.Click += (s, e) => OnAddServer(list);
            var editBtn = new Button { Content = "Edit", Width = 60, Margin = new Thickness(0, 0, 4, 0) };
            editBtn.Click += (s, e) => OnEditServer(list);
            var removeBtn = new Button { Content = "Remove", Width = 70 };
            removeBtn.Click += (s, e) => OnRemoveServer(list);

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            toolbar.Children.Add(addBtn);
            toolbar.Children.Add(editBtn);
            toolbar.Children.Add(removeBtn);

            var pathLabel = new TextBlock { Text = filePath, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) };

            var panel = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(pathLabel, Dock.Top);
            DockPanel.SetDock(toolbar, Dock.Bottom);
            panel.Children.Add(pathLabel);
            panel.Children.Add(toolbar);
            panel.Children.Add(list);

            return new TabItem { Header = header, Content = panel };
        }

        private void LoadAll()
        {
            LoadServers(_projectPath, _projectList);
            LoadServers(_globalPath, _globalList);
        }

        private void LoadServers(string path, ListView list)
        {
            list.Items.Clear();
            try
            {
                if (!File.Exists(path)) return;
                var json = JObject.Parse(File.ReadAllText(path));

                // Eclipse fix #9: read root-level mcpServers (the location the CLI writes for `--scope user`)
                var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rootServers = json["mcpServers"] as JObject;
                if (rootServers != null)
                {
                    foreach (var kv in rootServers)
                    {
                        if (kv.Value is JObject cfg && added.Add(kv.Key))
                            list.Items.Add(BuildRow(kv.Key, cfg));
                    }
                }

                // Also read legacy projects[<currentDir>].mcpServers (older CLI versions wrote here),
                // de-duplicating against the root-level entries above. Only relevant for the global file.
                if (path == _globalPath)
                {
                    var projects = json["projects"] as JObject;
                    if (projects != null)
                    {
                        var cwd = System.IO.Directory.GetCurrentDirectory();
                        var project = projects[cwd] as JObject ?? projects.Properties().Select(p => p.Value as JObject).FirstOrDefault();
                        var projServers = project?["mcpServers"] as JObject;
                        if (projServers != null)
                        {
                            foreach (var kv in projServers)
                            {
                                if (kv.Value is JObject cfg && added.Add(kv.Key))
                                    list.Items.Add(BuildRow(kv.Key, cfg));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private ServerRow BuildRow(string name, JObject cfg) => new ServerRow
        {
            Name = name,
            Command = cfg.Value<string>("command") ?? "",
            Args = cfg["args"] is JArray args ? string.Join(" ", args) : "",
            Env = FormatEnv(cfg["env"] as JObject)
        };

        private string FormatEnv(JObject? env)
        {
            if (env == null) return "";
            var parts = new List<string>();
            foreach (var kv in env)
                parts.Add($"{kv.Key}={kv.Value}");
            return string.Join(", ", parts);
        }

        private void OnAddServer(ListView list)
        {
            var dlg = new McpServerEditDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                list.Items.Add(new ServerRow
                {
                    Name = dlg.ServerName,
                    Command = dlg.ServerCommand,
                    Args = dlg.ServerArgs,
                    Env = dlg.ServerEnv
                });
            }
        }

        private void OnEditServer(ListView list)
        {
            if (list.SelectedItem is not ServerRow row) return;
            var dlg = new McpServerEditDialog
            {
                Owner = this,
                ServerName = row.Name,
                ServerCommand = row.Command,
                ServerArgs = row.Args,
                ServerEnv = row.Env
            };
            if (dlg.ShowDialog() == true)
            {
                row.Name = dlg.ServerName;
                row.Command = dlg.ServerCommand;
                row.Args = dlg.ServerArgs;
                row.Env = dlg.ServerEnv;
                list.Items.Refresh();
            }
        }

        private void OnRemoveServer(ListView list)
        {
            if (list.SelectedItem != null)
                list.Items.Remove(list.SelectedItem);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            SaveServers(_projectPath, _projectList);
            SaveServers(_globalPath, _globalList);
            DialogResult = true;
            Close();
        }

        private void SaveServers(string path, ListView list)
        {
            try
            {
                JObject json;
                if (File.Exists(path))
                    json = JObject.Parse(File.ReadAllText(path));
                else
                    json = new JObject();

                if (list.Items.Count > 0)
                {
                    var servers = new JObject();
                    foreach (ServerRow row in list.Items)
                    {
                        var cfg = new JObject { ["command"] = row.Command };
                        if (!string.IsNullOrWhiteSpace(row.Args))
                            cfg["args"] = new JArray(row.Args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                        if (!string.IsNullOrWhiteSpace(row.Env))
                        {
                            var envObj = new JObject();
                            foreach (var pair in row.Env.Split(','))
                            {
                                var eqIdx = pair.IndexOf('=');
                                if (eqIdx > 0)
                                    envObj[pair.Substring(0, eqIdx).Trim()] = pair.Substring(eqIdx + 1).Trim();
                            }
                            cfg["env"] = envObj;
                        }
                        servers[row.Name] = cfg;
                    }
                    json["mcpServers"] = servers;
                }
                else
                {
                    json.Remove("mcpServers");
                }

                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json.ToString(Formatting.Indented));
            }
            catch { }
        }

        private class ServerRow
        {
            public string Name { get; set; } = "";
            public string Command { get; set; } = "";
            public string Args { get; set; } = "";
            public string Env { get; set; } = "";
        }
    }

    /// <summary>Inner dialog for adding/editing a single MCP server.</summary>
    internal class McpServerEditDialog : Window
    {
        private readonly TextBox _nameBox, _cmdBox, _argsBox, _envBox;

        public string ServerName { get; set; } = "";
        public string ServerCommand { get; set; } = "";
        public string ServerArgs { get; set; } = "";
        public string ServerEnv { get; set; } = "";

        public McpServerEditDialog()
        {
            Title = "MCP Server";
            Width = 420;
            Height = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _nameBox = AddRow(grid, 0, "Name:");
            _cmdBox = AddRow(grid, 1, "Command:");
            _argsBox = AddRow(grid, 2, "Args:");
            _envBox = AddRow(grid, 3, "Env (K=V,...):");

            var okBtn = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            okBtn.Click += (s, e) =>
            {
                ServerName = _nameBox.Text.Trim();
                ServerCommand = _cmdBox.Text.Trim();
                ServerArgs = _argsBox.Text.Trim();
                ServerEnv = _envBox.Text.Trim();
                if (string.IsNullOrEmpty(ServerName) || string.IsNullOrEmpty(ServerCommand))
                {
                    MessageBox.Show("Name and Command are required.", "Validation");
                    return;
                }
                DialogResult = true;
                Close();
            };
            var cancelBtn = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 4);
            Grid.SetColumnSpan(btnPanel, 2);
            grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (s, e) =>
            {
                _nameBox.Text = ServerName;
                _cmdBox.Text = ServerCommand;
                _argsBox.Text = ServerArgs;
                _envBox.Text = ServerEnv;
            };
        }

        private TextBox AddRow(Grid grid, int row, string label)
        {
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            var box = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }
    }
}
