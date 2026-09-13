using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Checks, before a WebGL build starts, the things that would otherwise fail
    /// in a way that does not name its own cause.
    /// </summary>
    /// <remarks>
    /// Everything here would otherwise surface minutes into a build that cannot
    /// finish, reading as something wrong with Unity rather than with the install:
    /// a missing preprocessing patch at IL2CPP conversion or at runtime, and a
    /// missing or foreign libopus.a at the Emscripten link step.
    /// </remarks>
    public class Dissonance4WebBuildCheck
        : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            var problems = new List<string>();

            if (!DissonanceCorePatcher.RequiredPatchesApplied(out var patchDetail))
            {
                problems.Add(
                    "Dissonance's core has not been patched for the web. Run Tools > Dissonance 4 Web > Patch Dissonance For Web.\n" +
                    "  Missing:\n    " + patchDetail.Replace("\n", "\n    ")
                );
            }

            CheckOpusArchive(problems);

            if (problems.Count == 0)
                return;

            // Stopping here rather than letting the build run for several minutes
            // and then fail at the link step.
            throw new BuildFailedException("Dissonance 4 Web is not ready for a WebGL build:\n\n" + string.Join("\n\n", problems));
        }

        /// <summary>
        /// Checks the libopus.a Unity will actually link into the player.
        /// </summary>
        /// <remarks>
        /// Asks the plugin importers rather than searching folders. A package can
        /// live anywhere on disk - a <c>file:</c> package is outside the project
        /// altogether - and only the importer knows whether a given archive is
        /// enabled for WebGL. That matters here: Dissonance ships an iOS libopus.a
        /// of its own, which is the wrong architecture and must not count.
        /// </remarks>
        private static void CheckOpusArchive(List<string> problems)
        {
            var archives = PluginImporter.GetImporters(BuildTarget.WebGL)
                .Select(importer => importer.assetPath)
                .Where(path => string.Equals(Path.GetFileName(path), "libopus.a", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (archives.Count == 0)
            {
                problems.Add(
                    "No libopus.a is enabled for WebGL, so Opus has nothing to link against and the build would fail at the link step " +
                    "with undefined opus_* symbols. Build it with Native~/build-opus-wasm.ps1 (or .sh); it lands in Runtime/Plugins/WebGL."
                );
                return;
            }

            foreach (var assetPath in archives)
            {
                // Path.GetFullPath resolves a "Packages/..." asset path to wherever
                // the package really is on disk.
                var problem = WebGLArchiveInspector.DescribeProblem(Path.GetFullPath(assetPath));
                if (problem == null)
                    continue;

                problems.Add(
                    $"{assetPath} cannot be linked into a WebGL player: {problem}. The link step would warn \"neither Wasm object file " +
                    "nor LLVM bitcode\" for each one and then fail. Rebuild it with Native~/build-opus-wasm.ps1 (or .sh), which compiles " +
                    "with Emscripten through Ninja. An archive built by CMake's default Visual Studio generator on Windows looks exactly like this."
                );
            }

            if (archives.Count > 1)
            {
                problems.Add(
                    $"More than one libopus.a is enabled for WebGL ({string.Join(", ", archives)}), so Opus would be defined twice. Keep " +
                    "the one in Dissonance 4 Web's Runtime/Plugins/WebGL and disable WebGL on the others in the Plugin Inspector."
                );
            }
        }
    }
}
