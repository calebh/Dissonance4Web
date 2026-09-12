using UnityEditor;
using UnityEngine;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Notices, on load, that Dissonance has not been patched yet, and offers to
    /// do it.
    /// </summary>
    /// <remarks>
    /// Without this the first thing a new install produces is a handful of
    /// "inaccessible due to its protection level" errors from
    /// <c>WebPreprocessingPipeline</c>, which name a symptom and not a cause. The
    /// fix is one menu item, but only if you already know that.
    ///
    /// This lives in the same assembly as the patcher, and that assembly
    /// deliberately references nothing, so both survive the compile errors they
    /// exist to clear.
    /// </remarks>
    [InitializeOnLoad]
    public static class Dissonance4WebSetupPrompt
    {
        /// <summary>Set for the rest of the editor session once we have asked.</summary>
        private const string AskedThisSessionKey = "Dissonance4Web.SetupPrompt.Asked";

        /// <summary>Set for good when the user says not to ask again.</summary>
        private const string SuppressedKey = "Dissonance4Web.SetupPrompt.Suppressed";

        static Dissonance4WebSetupPrompt()
        {
            // Deferred: a domain reload is a bad time to put a modal dialog on
            // screen, and during a package import the asset database may not have
            // settled enough to find Dissonance's files yet.
            EditorApplication.delayCall += Check;
        }

        [MenuItem("Tools/Dissonance 4 Web/Check Patches On Startup", false, 120)]
        private static void ToggleSuppressed()
        {
            EditorPrefs.SetBool(SuppressedKey, !EditorPrefs.GetBool(SuppressedKey, false));
        }

        [MenuItem("Tools/Dissonance 4 Web/Check Patches On Startup", true)]
        private static bool ToggleSuppressedValidate()
        {
            Menu.SetChecked("Tools/Dissonance 4 Web/Check Patches On Startup", !EditorPrefs.GetBool(SuppressedKey, false));
            return true;
        }

        private static void Check()
        {
            if (Application.isBatchMode)
                return;

            if (!DissonanceCorePatcher.DissonanceIsInstalled())
            {
                // Dissonance is not here yet. The package is no use without it, but
                // installing it later is a perfectly normal order to work in.
                return;
            }

            if (DissonanceCorePatcher.RequiredPatchesApplied(out var detail))
                return;

            // Always say what is wrong in the console, even when the dialog is
            // suppressed, because the compile errors on their own do not point
            // anywhere useful.
            Debug.LogWarning(
                "[Dissonance4Web] Dissonance has not been patched for the web yet, so Dissonance4Web will not compile. " +
                "Run Tools > Dissonance 4 Web > Patch Dissonance For Web.\n\nMissing:\n  " + detail.Replace("\n", "\n  ")
            );

            if (EditorPrefs.GetBool(SuppressedKey, false))
                return;

            if (SessionState.GetBool(AskedThisSessionKey, false))
                return;
            SessionState.SetBool(AskedThisSessionKey, true);

            if (!DissonanceCorePatcher.CanApplyAutomatically())
            {
                // The anchors do not match, so this is a Dissonance version the
                // patches were not written against. Offering a button that cannot
                // work would be worse than saying so.
                EditorUtility.DisplayDialog(
                    "Dissonance 4 Web",
                    "Dissonance needs a few small edits before it will run in a browser, but this copy of Dissonance does not match what " +
                    "the automatic patcher expects.\n\n" +
                    "Documentation~/CorePatches.md lists the edits in full so they can be applied by hand. " +
                    "Tools > Dissonance 4 Web > Check Patches reports which ones are the problem.",
                    "OK"
                );
                return;
            }

            var apply = EditorUtility.DisplayDialog(
                "Dissonance 4 Web",
                "Dissonance needs three small edits to its own source before it will run in a browser. Until they are applied, " +
                "Dissonance4Web will not compile.\n\n" +
                "The edits are additive and reversible - Tools > Dissonance 4 Web > Revert Patches puts them back - and " +
                "Documentation~/CorePatches.md explains each one.\n\n" +
                "Apply them now?",
                "Patch Dissonance",
                "Not now"
            );

            if (apply)
                DissonanceCorePatcher.Apply();
            else
                Debug.Log("[Dissonance4Web] Not patching. Run Tools > Dissonance 4 Web > Patch Dissonance For Web when you are ready.");
        }
    }
}
