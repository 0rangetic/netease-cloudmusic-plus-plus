using System.IO;
using System.Threading.Tasks;

namespace NeteaseToolbox
{
    // Read-only state shown by the native WPF settings page.
    internal sealed class ToolboxSettingsSnapshot
    {
        internal string OutputStatus;
        internal bool ChooseOutputEnabled;
        internal bool ResetOutputEnabled;
        internal bool MaintenanceEnabled;
        internal string ClientFolder;
        internal bool ClientFolderConfigured;
    }

    internal sealed partial class ToolboxForm
    {
        // Minimal facade for SettingsView: the WinForms page stays the owner of
        // the controls and business logic, the WPF page only triggers it.
        internal ToolboxSettingsSnapshot ReadSettingsSnapshot()
        {
            var snapshot = new ToolboxSettingsSnapshot
            {
                OutputStatus = settingsOutputStatus.Text,
                ChooseOutputEnabled = chooseOutputButton.Enabled,
                ResetOutputEnabled = resetOutputButton.Enabled,
                MaintenanceEnabled = !(converting || deduplicating || extractingCover || analyzingGenres)
            };
            try
            {
                snapshot.ClientFolder = ClientFolder();
                snapshot.ClientFolderConfigured = !string.IsNullOrEmpty(snapshot.ClientFolder)
                    && File.Exists(Path.Combine(snapshot.ClientFolder, "cloudmusic.exe"));
            }
            catch
            {
                snapshot.ClientFolder = "";
                snapshot.ClientFolderConfigured = false;
            }
            return snapshot;
        }

        internal void ChooseOutputFolderFromSettings() { ChooseOutputFolder(); }
        internal void ResetOutputFolderFromSettings() { ResetOutputFolder(); }
        internal void OpenPortraitAiSettingsFromSettings() { ShowPortraitAiSettings(); }
        internal void OpenSafeDedupeFromSettings() { OpenSafeDedupe(); }
        internal void OpenTrashFromSettings() { OpenTrash(); }
        internal void ChooseClientFolderFromSettings() { ChooseClientFolder(); }
        internal Task CheckEnvironmentFromSettingsAsync() { return CheckPortableEnvironment(); }
        internal Task SetupEnvironmentFromSettingsAsync() { return SetupPortableEnvironment(); }
    }
}
