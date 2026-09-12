using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Checks, before a WebGL build starts, the things that would otherwise fail
    /// in a way that does not name its own cause.
    /// </summary>
    /// <remarks>
    /// Two of these are worth stopping the build for. A missing libopus.a fails
    /// at the Emscripten link step with a list of undefined symbols, and a missing
    /// preprocessing patch fails at IL2CPP conversion or, worse, at runtime. Both
    /// read as something being wrong with Unity rather than with the install, and
    /// both are minutes into a build that will not finish.
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

            if (FindOpusArchive() == null)
            {
                problems.Add(
                    "libopus.a is not in the project, so Opus has no WebAssembly build to link against and this build will fail at the " +
                    "link step with undefined opus_* symbols. Build it with Native~/build-opus-wasm.ps1 (or .sh)."
                );
            }

            if (problems.Count == 0)
                return;

            var message = "Dissonance 4 Web is not ready for a WebGL build:\n\n" + string.Join("\n\n", problems);

            // Stopping here rather than letting the build run for several minutes
            // and then fail at the link step with undefined symbols.
            throw new BuildFailedException(message);
        }

        /// <summary>
        /// Looks for the WebAssembly Opus archive anywhere Unity would pick it up.
        /// </summary>
        private static string FindOpusArchive()
        {
            var roots = new List<string> { Application.dataPath };

            var packages = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Packages");
            if (Directory.Exists(packages))
                roots.Add(packages);

            // A package installed from git or the registry lives outside the
            // project, under Library/PackageCache.
            var cache = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library/PackageCache");
            if (Directory.Exists(cache))
                roots.Add(cache);

            foreach (var root in roots)
            {
                var match = Directory
                    .GetFiles(root, "libopus.a", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Replace('\\', '/').Contains("/WebGL/"));

                if (match != null)
                    return match;
            }

            return null;
        }
    }
}
