using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Applies, checks and removes the handful of edits Dissonance's own source
    /// needs before it will run in a browser.
    /// </summary>
    /// <remarks>
    /// Three of these edits are unavoidable, and it is worth saying why, because
    /// patching a third party asset is not something to do lightly.
    ///
    /// Dissonance's audio pipeline is extensible at exactly the points its authors
    /// meant it to be: microphone capture and voice playback are public interfaces,
    /// and this package replaces both without touching a line of Dissonance. The
    /// preprocessing pipeline is not one of those points - <c>IPreprocessingPipeline</c>
    /// is internal and the class that builds it is internal - and a browser cannot
    /// use the pipeline Dissonance builds, because it runs on a dedicated thread
    /// that a WebGL player cannot create. So there has to be a way in.
    ///
    /// Similarly, IL2CPP only emits a direct call to a statically linked native
    /// function when the DllImport names the module <c>__Internal</c>. Every other
    /// name becomes a runtime library lookup, which an Emscripten build does not
    /// have. Dissonance already special cases iOS for exactly this reason; the
    /// patch adds WebGL to the same condition.
    ///
    /// The edits are small, additive and reversible, and each is anchored to an
    /// exact piece of the original text so a version of Dissonance this was not
    /// written against is reported rather than mangled.
    /// </remarks>
    public static class DissonanceCorePatcher
    {
        private const string MenuRoot = "Tools/Dissonance 4 Web/";

        /// <summary>
        /// One edit: an anchor to find, the replacement, and a marker that says it
        /// has already been made.
        /// </summary>
        private class Patch
        {
            /// <summary>File name to search the project for.</summary>
            public string FileName;

            /// <summary>Human readable description, used in reports.</summary>
            public string Summary;

            /// <summary>Whether Dissonance runs in a browser without this.</summary>
            public bool Optional;

            /// <summary>
            /// Exact original text, written with Unix newlines. Must appear once
            /// in the file once translated to the file's own newline.
            /// </summary>
            public string Original;

            /// <summary>What to put in its place, with Unix newlines.</summary>
            public string Replacement;

            /// <summary>Text found only in the patched file.</summary>
            public string Marker;
        }

        /// <summary>
        /// One patch, with its text translated to the newline the target file
        /// actually uses.
        /// </summary>
        /// <remarks>
        /// Dissonance ships with CRLF, but a project that has been through a git
        /// checkout with <c>core.autocrlf</c> off, or through a tool that
        /// normalised it, will have LF. Matching the file's own convention means
        /// neither case is reported as "this is a different version of Dissonance".
        /// </remarks>
        private struct LocalisedPatch
        {
            public string Original;
            public string Replacement;

            public LocalisedPatch(Patch patch, string text)
            {
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";

                Original = patch.Original.Replace("\n", newline);
                Replacement = patch.Replacement.Replace("\n", newline);
            }
        }

        private enum PatchState
        {
            Applied,
            NotApplied,
            FileMissing,
            AnchorMissing
        }

        [MenuItem(MenuRoot + "Patch Dissonance For Web", false, 100)]
        public static void Apply()
        {
            var report = new StringBuilder();
            var changed = 0;
            var failed = 0;

            foreach (var patch in Patches)
            {
                var path = FindFile(patch.FileName);
                if (path == null)
                {
                    report.AppendLine($"  MISSING  {patch.FileName} - not found in this project");
                    failed++;
                    continue;
                }

                var text = File.ReadAllText(path);

                if (text.Contains(patch.Marker))
                {
                    report.AppendLine($"  already  {patch.FileName} - {patch.Summary}");
                    continue;
                }

                var localised = new LocalisedPatch(patch, text);

                var occurrences = CountOccurrences(text, localised.Original);
                if (occurrences != 1)
                {
                    report.AppendLine(
                        occurrences == 0
                            ? $"  FAILED   {patch.FileName} - the text this patch anchors to is not there. This is probably a different version of Dissonance; see Documentation~/CorePatches.md and apply it by hand."
                            : $"  FAILED   {patch.FileName} - the text this patch anchors to appears {occurrences} times, so it is ambiguous. Apply it by hand; see Documentation~/CorePatches.md."
                    );
                    failed++;
                    continue;
                }

                File.WriteAllText(path, text.Replace(localised.Original, localised.Replacement));
                report.AppendLine($"  patched  {patch.FileName} - {patch.Summary}");
                changed++;
            }

            Report("Patch", report, changed, failed);

            if (changed > 0)
                AssetDatabase.Refresh();
        }

        [MenuItem(MenuRoot + "Check Patches", false, 101)]
        public static void Check()
        {
            var report = new StringBuilder();
            var missing = 0;

            foreach (var patch in Patches)
            {
                var state = StateOf(patch, out _);
                var required = patch.Optional ? "optional" : "required";

                switch (state)
                {
                    case PatchState.Applied:
                        report.AppendLine($"  applied     {patch.FileName} ({required}) - {patch.Summary}");
                        break;

                    case PatchState.NotApplied:
                        report.AppendLine($"  NOT APPLIED {patch.FileName} ({required}) - {patch.Summary}");
                        if (!patch.Optional)
                            missing++;
                        break;

                    case PatchState.FileMissing:
                        report.AppendLine($"  NOT FOUND   {patch.FileName} - is Dissonance installed?");
                        missing++;
                        break;

                    case PatchState.AnchorMissing:
                        report.AppendLine($"  UNKNOWN     {patch.FileName} - neither patched nor matching the original text");
                        missing++;
                        break;
                }
            }

            if (missing == 0)
                Debug.Log("[Dissonance4Web] Dissonance core patches:\n" + report + "\nAll required patches are in place.");
            else
                Debug.LogWarning($"[Dissonance4Web] Dissonance core patches:\n{report}\n{missing} required patch(es) are not in place. Run {MenuRoot}Patch Dissonance For Web.");
        }

        [MenuItem(MenuRoot + "Revert Patches", false, 102)]
        public static void Revert()
        {
            if (!EditorUtility.DisplayDialog(
                    "Revert Dissonance patches",
                    "This puts Dissonance's own source back the way it was. Voice chat will stop working in WebGL builds until the patches are re-applied.\n\nContinue?",
                    "Revert",
                    "Cancel"))
            {
                return;
            }

            var report = new StringBuilder();
            var changed = 0;
            var failed = 0;

            foreach (var patch in Patches)
            {
                var path = FindFile(patch.FileName);
                if (path == null)
                {
                    report.AppendLine($"  MISSING  {patch.FileName} - not found in this project");
                    failed++;
                    continue;
                }

                var text = File.ReadAllText(path);
                if (!text.Contains(patch.Marker))
                {
                    report.AppendLine($"  already  {patch.FileName} - not patched");
                    continue;
                }

                var localised = new LocalisedPatch(patch, text);

                if (CountOccurrences(text, localised.Replacement) != 1)
                {
                    report.AppendLine($"  FAILED   {patch.FileName} - the patched text has been edited since, so it cannot be reverted automatically");
                    failed++;
                    continue;
                }

                File.WriteAllText(path, text.Replace(localised.Replacement, localised.Original));
                report.AppendLine($"  reverted {patch.FileName}");
                changed++;
            }

            Report("Revert", report, changed, failed);

            if (changed > 0)
                AssetDatabase.Refresh();
        }

        /// <summary>
        /// Whether every required patch is in place. Used by the build check.
        /// </summary>
        public static bool RequiredPatchesApplied(out string detail)
        {
            var problems = new List<string>();

            foreach (var patch in Patches)
            {
                if (patch.Optional)
                    continue;

                if (StateOf(patch, out _) != PatchState.Applied)
                    problems.Add($"{patch.FileName}: {patch.Summary}");
            }

            detail = string.Join("\n", problems);
            return problems.Count == 0;
        }

        private static PatchState StateOf(Patch patch, out string path)
        {
            path = FindFile(patch.FileName);
            if (path == null)
                return PatchState.FileMissing;

            var text = File.ReadAllText(path);

            if (text.Contains(patch.Marker))
                return PatchState.Applied;

            var localised = new LocalisedPatch(patch, text);

            return CountOccurrences(text, localised.Original) == 1
                ? PatchState.NotApplied
                : PatchState.AnchorMissing;
        }

        private static void Report(string action, StringBuilder report, int changed, int failed)
        {
            var message = $"[Dissonance4Web] {action} Dissonance core:\n{report}";

            if (failed > 0)
                Debug.LogError($"{message}\n{failed} patch(es) could not be applied. See Documentation~/CorePatches.md for the exact edits.");
            else
                Debug.Log($"{message}\n{changed} file(s) changed.");
        }

        /// <summary>
        /// Finds a Dissonance source file by name.
        /// </summary>
        /// <remarks>
        /// Searched by name rather than by a fixed path because Dissonance can be
        /// installed anywhere under Assets, and as a package in some projects.
        /// Only paths that look like Dissonance's own tree are considered, so a
        /// file of the same name elsewhere in the project is not touched.
        /// </remarks>
        private static string FindFile(string fileName)
        {
            var roots = new List<string> { Application.dataPath };

            var packages = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Packages");
            if (Directory.Exists(packages))
                roots.Add(packages);

            foreach (var root in roots)
            {
                var matches = Directory
                    .GetFiles(root, fileName, SearchOption.AllDirectories)
                    .Where(LooksLikeDissonanceCore)
                    .ToList();

                if (matches.Count > 0)
                    return matches[0];
            }

            return null;
        }

        private static bool LooksLikeDissonanceCore(string path)
        {
            var normalised = path.Replace('\\', '/');

            return normalised.Contains("/Dissonance/Core/")
                || normalised.Contains("/Dissonance/Editor/");
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var index = 0;

            while (true)
            {
                index = text.IndexOf(value, index, StringComparison.Ordinal);
                if (index < 0)
                    return count;

                count++;
                index += value.Length;
            }
        }

        // ------------------------------------------------------------------
        // the patches
        // ------------------------------------------------------------------

        private static readonly Patch[] Patches =
        {
            new Patch
            {
                FileName = "AssemblyAttributes.cs",
                Summary = "let Dissonance4Web implement Dissonance's internal preprocessing interface",
                Marker = "InternalsVisibleTo(\"Dissonance4Web\")",
                Original =
                    "[assembly: InternalsVisibleTo(\"Assembly-CSharp-Editor\")]",
                Replacement =
                    "// Dissonance 4 Web: the browser capture pipeline implements Dissonance's own\n" +
                    "// IPreprocessingPipeline, which is internal and has no public equivalent.\n" +
                    "[assembly: InternalsVisibleTo(\"Dissonance4Web\")]\n" +
                    "[assembly: InternalsVisibleTo(\"Dissonance4Web.dll\")]\n" +
                    "\n" +
                    "[assembly: InternalsVisibleTo(\"Assembly-CSharp-Editor\")]"
            },

            new Patch
            {
                FileName = "CapturePipelineManager.cs",
                Summary = "allow the preprocessing pipeline to be replaced, and compile the WebRTC one out of WebGL builds",
                Marker = "PreprocessorFactory",
                Original =
                    "        // ncrunch: no coverage start\n" +
                    "        // Justification: we don't want to load the webrtc preprocessing DLL into tests so we're faking a preprocessor in a derived test class)\n" +
                    "        [NotNull] protected virtual IPreprocessingPipeline CreatePreprocessor([NotNull] WaveFormat format)\n" +
                    "        {\n" +
                    "            return new WebRtcPreprocessingPipeline(format, _isMobilePlatform);\n" +
                    "            //return new EmptyPreprocessingPipeline(format);\n" +
                    "        }\n" +
                    "        //ncrunch: no coverage end",
                Replacement =
                    "        /// <summary>\n" +
                    "        /// Replaces the preprocessing pipeline Dissonance would build for itself.\n" +
                    "        /// Set this before DissonanceComms starts.\n" +
                    "        /// </summary>\n" +
                    "        /// <remarks>\n" +
                    "        /// Dissonance 4 Web patch. Neither half of the default pipeline can run in a\n" +
                    "        /// browser: it is driven from a dedicated thread, and a WebGL player is single\n" +
                    "        /// threaded; and it processes audio in a native library with no WebAssembly\n" +
                    "        /// build. The bool is `_isMobilePlatform`, passed on for a replacement that\n" +
                    "        /// wants to make the same trade offs Dissonance does on a weak device.\n" +
                    "        /// </remarks>\n" +
                    "        internal static Func<WaveFormat, bool, IPreprocessingPipeline> PreprocessorFactory;\n" +
                    "\n" +
                    "        // ncrunch: no coverage start\n" +
                    "        // Justification: we don't want to load the webrtc preprocessing DLL into tests so we're faking a preprocessor in a derived test class)\n" +
                    "        [NotNull] protected virtual IPreprocessingPipeline CreatePreprocessor([NotNull] WaveFormat format)\n" +
                    "        {\n" +
                    "            var factory = PreprocessorFactory;\n" +
                    "            if (factory != null)\n" +
                    "                return factory(format, _isMobilePlatform);\n" +
                    "\n" +
                    "#if UNITY_WEBGL && !UNITY_EDITOR\n" +
                    "            // Deliberately not falling through to the WebRTC pipeline. Referencing it\n" +
                    "            // here would put its P/Invokes into the build, and a build that forgot to\n" +
                    "            // install a replacement should fail with this message rather than with a\n" +
                    "            // missing native symbol.\n" +
                    "            throw Log.CreateUserErrorException(\n" +
                    "                \"no audio preprocessing pipeline is available in a WebGL player\",\n" +
                    "                \"not adding a DissonanceWebAudio component to the DissonanceComms game object\",\n" +
                    "                \"https://github.com/calebh/Dissonance4Web\",\n" +
                    "                \"0f4a1b3c-9d25-4c8a-9b6e-7f2a5d1c8e40\"\n" +
                    "            );\n" +
                    "#else\n" +
                    "            return new WebRtcPreprocessingPipeline(format, _isMobilePlatform);\n" +
                    "            //return new EmptyPreprocessingPipeline(format);\n" +
                    "#endif\n" +
                    "        }\n" +
                    "        //ncrunch: no coverage end"
            },

            new Patch
            {
                FileName = "Opus.cs",
                Summary = "resolve Opus as a statically linked plugin in WebGL builds",
                Marker = "(UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR",
                Original =
                    "#if UNITY_IOS && !UNITY_EDITOR\n" +
                    "        private const string ImportString = \"__Internal\";\n" +
                    "        private const CallingConvention Convention = CallingConvention.Cdecl;\n" +
                    "#else\n" +
                    "        private const string ImportString = \"opus\";\n" +
                    "        private const CallingConvention Convention = CallingConvention.Cdecl;\n" +
                    "#endif",
                Replacement =
                    "        // Dissonance 4 Web patch: a WebGL player links its native plugins statically,\n" +
                    "        // and IL2CPP only emits a direct call for the module name \"__Internal\" - any\n" +
                    "        // other name becomes a dynamic library lookup, which Emscripten builds do not\n" +
                    "        // have. Same reason iOS is already special cased.\n" +
                    "#if (UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR\n" +
                    "        private const string ImportString = \"__Internal\";\n" +
                    "        private const CallingConvention Convention = CallingConvention.Cdecl;\n" +
                    "#else\n" +
                    "        private const string ImportString = \"opus\";\n" +
                    "        private const CallingConvention Convention = CallingConvention.Cdecl;\n" +
                    "#endif"
            },

            new Patch
            {
                FileName = "DissonanceCommsImpl.cs",
                Optional = true,
                Summary = "stop probing for the native audio processing library in WebGL builds, which only logs a false error there",
                Marker = "Dissonance 4 Web patch: AudioPluginDissonance",
                Original =
                    "            // Getting the filter state loads the DLL (and has no side effects, it's just a getter). Will throw if DLL is missing.\n" +
                    "            AudioPluginDissonanceNative.Dissonance_GetFilterState();",
                Replacement =
                    "            // Dissonance 4 Web patch: AudioPluginDissonance has no WebAssembly build and\n" +
                    "            // a browser does not need one, because the browser does this work itself.\n" +
                    "            // Probing for it there only logs a dependency error that nothing can fix.\n" +
                    "#if !UNITY_WEBGL || UNITY_EDITOR\n" +
                    "            // Getting the filter state loads the DLL (and has no side effects, it's just a getter). Will throw if DLL is missing.\n" +
                    "            AudioPluginDissonanceNative.Dissonance_GetFilterState();\n" +
                    "#endif"
            }
        };
    }
}
