using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// Opens the Setup Wizard once, the first time this project is opened in the editor on this machine
    /// (per-project EditorPrefs flag). Skipped in batch mode (CI, command-line builds) and while entering Play mode.
    /// </summary>
    [InitializeOnLoad]
    internal static class SetupWizardAutoOpen
    {
        private const string ShownPref = "SetupWizardAutoOpened";

        static SetupWizardAutoOpen()
        {
            if (Application.isBatchMode) return;
            // Wait until the editor has finished loading its layout before opening a window.
            EditorApplication.delayCall += TryOpen;
        }

        private static void TryOpen()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SetupPrefs.GetBool(ShownPref, false)) return;
            SetupPrefs.SetBool(ShownPref, true);
            SetupWizardWindow.Open();
        }
    }
}
