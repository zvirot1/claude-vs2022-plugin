using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeCode.UI.Dialogs
{
    /// <summary>
    /// Dialog for managing Claude project memory (MEMORY.md).
    /// 2 tabs: Editor and Tips & Examples.
    /// Port of com.anthropic.claude.intellij.ui.dialogs.MemoryDialog.
    /// </summary>
    public class MemoryDialog : Window
    {
        private readonly TextBox _editor;
        private readonly string _memoryPath;

        public MemoryDialog(string projectDir)
        {
            Title = "Claude Code - Memory & Context";
            Width = 650;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            // Compute memory file path: ~/.claude/projects/{encoded-dir}/memory/MEMORY.md
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var encoded = (projectDir ?? home).Replace("\\", "-").Replace("/", "-").Replace(":", "");
            _memoryPath = Path.Combine(home, ".claude", "projects", encoded, "memory", "MEMORY.md");

            var tabs = new TabControl { Margin = new Thickness(8) };

            // Tab 1: Memory Editor
            var exists = File.Exists(_memoryPath);
            var status = new TextBlock
            {
                Text = $"{_memoryPath}\n{(exists ? "✓ File exists" : "⚠ Will be created on save")}",
                FontSize = 11,
                Foreground = exists ? Brushes.Green : Brushes.Orange,
                Margin = new Thickness(0, 0, 0, 4)
            };

            _editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var editorPanel = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(status, Dock.Top);
            editorPanel.Children.Add(status);
            editorPanel.Children.Add(new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            tabs.Items.Add(new TabItem { Header = "Project Memory", Content = editorPanel });

            // Tab 2: Tips & Examples
            var tips = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(8),
                Text = @"# Project Memory (MEMORY.md)

This file helps Claude remember important context about your project
across sessions. Write anything you want Claude to know.

## Good things to include:

### Architecture
- Project structure and key directories
- Framework and language versions
- Important design patterns used

### Build & Run
- How to build the project
- How to run tests
- Common development commands

### Conventions
- Coding style preferences
- Naming conventions
- File organization rules

### Known Issues
- Current bugs or limitations
- Areas under active development
- Technical debt to be aware of

## Example:

```markdown
# MyProject Memory

## Stack
- .NET 8, C#, ASP.NET Core
- PostgreSQL database
- React frontend (TypeScript)

## Build
- `dotnet build` to compile
- `dotnet test` to run tests
- `npm run dev` for frontend

## Conventions
- Use PascalCase for public members
- Async methods end with 'Async'
- Controllers in /Controllers, Services in /Services
```"
            };

            tabs.Items.Add(new TabItem
            {
                Header = "Tips & Examples",
                Content = new ScrollViewer { Content = tips, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            });

            // Buttons
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
            LoadMemory();
        }

        private void LoadMemory()
        {
            try
            {
                if (File.Exists(_memoryPath))
                    _editor.Text = File.ReadAllText(_memoryPath);
            }
            catch { }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = _editor.Text;
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (File.Exists(_memoryPath)) File.Delete(_memoryPath);
                }
                else
                {
                    var dir = Path.GetDirectoryName(_memoryPath);
                    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_memoryPath, content);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving: {ex.Message}", "Error");
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
