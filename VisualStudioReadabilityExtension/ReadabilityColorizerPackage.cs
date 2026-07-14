using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace VisualStudioReadabilityExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    // Registers the command table (Menus.ctmenu) compiled from ReadabilityColorizer.vsct
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Load at startup (in the background) so the toolbar buttons' checked state reflects current settings before they're first clicked
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ReadabilityColorizerPackage : AsyncPackage
    {
        public const string PackageGuidString = "c7f2a1d4-9b3e-4c5a-8d67-2f1e0a9b8c7d";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                AddToggle(commandService, PackageCommands.ToggleReadabilityCommandId,
                    invoke: OnToggleBlockColouring, isChecked: () => ReadabilityRuntimeState.EffectiveEnabled,
                    onText: "Code Block Coloring: On", offText: "Code Block Coloring: Off");

                AddToggle(commandService, PackageCommands.ToggleActiveScopeCommandId,
                    invoke: OnToggleActiveScope, isChecked: () => ReadabilityRuntimeState.EffectiveActiveScope,
                    onText: "Parenthesis Grouping: On", offText: "Parenthesis Grouping: Off");

                ReadabilityColorizerSettings.Log("Package: toolbar commands registered");
            }
            else
            {
                ReadabilityColorizerSettings.Log("Package: IMenuCommandService unavailable; commands NOT registered");
            }
        }

        private static void AddToggle(OleMenuCommandService service, int commandId,
            EventHandler invoke, Func<bool> isChecked, string onText, string offText)
        {
            var id = new CommandID(PackageCommands.CommandSet, commandId);
            var command = new OleMenuCommand(invoke, id);
            command.BeforeQueryStatus += (sender, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (sender is OleMenuCommand c)
                {
                    bool on = isChecked();
                    c.Checked = on;
                    c.Text = on ? onText : offText;
                }
            };
            service.AddCommand(command);
        }

        private void OnToggleBlockColouring(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ReadabilityColorizerSettings.Log("Package: OnToggleBlockColouring clicked");
            ReadabilityRuntimeState.ToggleEnabled();
        }

        private void OnToggleActiveScope(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ReadabilityColorizerSettings.Log("Package: OnToggleActiveScope clicked");
            ReadabilityRuntimeState.ToggleActiveScope();
        }
    }
}
