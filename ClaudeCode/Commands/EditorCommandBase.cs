using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode
{
    /// <summary>
    /// Base class for editor context menu commands that send selected text to Claude.
    /// </summary>
    internal abstract class EditorCommandBase
    {
        protected static async Task InitializeCommandAsync(AsyncPackage package, int commandId, string? prefix = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            var cmdId = new CommandID(CommandIds.CommandSetGuid, commandId);
            var cmd = new OleMenuCommand((s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ExecuteCommand(package, prefix);
            }, cmdId);
            commandService.AddCommand(cmd);
        }

        private static void ExecuteCommand(AsyncPackage package, string? prefix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get selected text from active editor
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var selection = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
            var selectedText = selection?.Text ?? "";
            var fileName = dte?.ActiveDocument?.Name ?? "";

            // Build message
            string message;
            if (!string.IsNullOrEmpty(prefix))
            {
                message = string.IsNullOrEmpty(selectedText)
                    ? $"{prefix} the file {fileName}"
                    : $"{prefix} this code from {fileName}:\n\n```\n{selectedText}\n```";
            }
            else
            {
                message = string.IsNullOrEmpty(selectedText)
                    ? $"Here is the file {fileName}"
                    : selectedText;
            }

            // Open tool window and send message
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await package.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, package.DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
                if (window is ClaudeToolWindow toolWindow && toolWindow.Content is UI.ClaudeChatPanel panel)
                    panel.SendMessageFromCommand(message);
            });
        }
    }

    internal sealed class SendSelectionCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.SendSelection);
    }

    internal sealed class ExplainCodeCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.ExplainCode, "Explain");
    }

    internal sealed class ReviewCodeCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.ReviewCode, "Review");
    }

    internal sealed class RefactorCodeCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.RefactorCode, "Refactor");
    }

    internal sealed class AnalyzeFileCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.AnalyzeFile, "Analyze");
    }

    internal sealed class NewSessionCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.NewSession, null);
    }

    internal sealed class FocusToggleCommand : EditorCommandBase
    {
        public static Task InitializeAsync(AsyncPackage package)
            => InitializeCommandAsync(package, CommandIds.FocusToggle, null);
    }
}
