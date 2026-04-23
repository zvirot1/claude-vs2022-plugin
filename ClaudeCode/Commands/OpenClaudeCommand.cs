using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode
{
    /// <summary>
    /// Command to open the Claude Code tool window.
    /// Registered programmatically under Tools menu.
    /// </summary>
    internal sealed class OpenClaudeCommand
    {
        // (commands registered programmatically, no VSCT needed)

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            var cmdId = new CommandID(CommandIds.CommandSetGuid, CommandIds.OpenClaude);
            var cmd = new OleMenuCommand(Execute, cmdId);
            cmd.BeforeQueryStatus += (s, e) => { ((OleMenuCommand)s).Visible = true; };
            commandService.AddCommand(cmd);
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var package = ClaudeCodePackage.Instance;
                if (package == null) return;
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await package.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, package.DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            });
        }
    }
}
