# The Dissonance core patches

Four edits to Dissonance's own source. Three are required for a browser to work at all; the
fourth only stops a misleading error in the log.

**Tools > Dissonance 4 Web > Patch Dissonance For Web** applies them all, **Check Patches**
reports which are in place, and **Revert Patches** puts the files back. Each is anchored to an
exact piece of the original text, so a Dissonance version this was not written against is reported
rather than mangled - in which case apply the edit from this document by hand.

All four are additive and none changes behaviour on any platform other than WebGL.

---

## 1. `Core/AssemblyAttributes.cs` — required

Add `Dissonance4Web` to the assemblies that can see Dissonance's internals.

```diff
+// Dissonance 4 Web: the browser capture pipeline implements Dissonance's own
+// IPreprocessingPipeline, which is internal and has no public equivalent.
+[assembly: InternalsVisibleTo("Dissonance4Web")]
+[assembly: InternalsVisibleTo("Dissonance4Web.dll")]
+
 [assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
```

**Why.** `WebPreprocessingPipeline` implements `IPreprocessingPipeline`, and reuses Dissonance's
`Resampler`, `BufferedSampleProvider`, `SampleToFrameProvider` and `ArvCalculator` so that the
frames a browser produces are framed and resampled exactly the way every other platform's are. All
of those are internal. There is no public equivalent to implement instead.

---

## 2. `Core/Audio/Capture/CapturePipelineManager.cs` — required

Let the preprocessing pipeline be substituted, and compile the WebRTC one out of a WebGL player.

```diff
+        /// <summary>
+        /// Replaces the preprocessing pipeline Dissonance would build for itself.
+        /// Set this before DissonanceComms starts.
+        /// </summary>
+        /// <remarks>
+        /// Dissonance 4 Web patch. Neither half of the default pipeline can run in a
+        /// browser: it is driven from a dedicated thread, and a WebGL player is single
+        /// threaded; and it processes audio in a native library with no WebAssembly
+        /// build. The bool is `_isMobilePlatform`, passed on for a replacement that
+        /// wants to make the same trade offs Dissonance does on a weak device.
+        /// </remarks>
+        internal static Func<WaveFormat, bool, IPreprocessingPipeline> PreprocessorFactory;
+
         // ncrunch: no coverage start
         // Justification: we don't want to load the webrtc preprocessing DLL into tests so we're faking a preprocessor in a derived test class)
         [NotNull] protected virtual IPreprocessingPipeline CreatePreprocessor([NotNull] WaveFormat format)
         {
+            var factory = PreprocessorFactory;
+            if (factory != null)
+                return factory(format, _isMobilePlatform);
+
+#if UNITY_WEBGL && !UNITY_EDITOR
+            // Deliberately not falling through to the WebRTC pipeline. Referencing it
+            // here would put its P/Invokes into the build, and a build that forgot to
+            // install a replacement should fail with this message rather than with a
+            // missing native symbol.
+            throw Log.CreateUserErrorException(
+                "no audio preprocessing pipeline is available in a WebGL player",
+                "not adding a DissonanceWebAudio component to the DissonanceComms game object",
+                "https://github.com/calebh/Dissonance4Web",
+                "0f4a1b3c-9d25-4c8a-9b6e-7f2a5d1c8e40"
+            );
+#else
             return new WebRtcPreprocessingPipeline(format, _isMobilePlatform);
             //return new EmptyPreprocessingPipeline(format);
+#endif
         }
         //ncrunch: no coverage end
```

**Why.** `BasePreprocessingPipeline` creates a `System.Threading.Thread` in its constructor and
starts it from `Start()`. A WebGL player is single threaded and `Thread.Start` throws there, so the
default pipeline cannot run in a browser at all. `CreatePipelineManager` is internal and
`CreatePreprocessor` is `protected virtual` on an internal class, so there is no way to override
it from outside the assembly - hence a hook.

The `#if` matters as much as the hook. With it, a WebGL build contains no reference to
`WebRtcPreprocessingPipeline` and therefore none of the `AudioPluginDissonance` P/Invokes it makes,
and a build that forgot to add the `DissonanceWebAudio` component fails with a sentence explaining
itself rather than at some later, less obvious point.

Requires `System` and `NAudio.Wave`, both already imported in this file.

---

## 3. `Core/Audio/Codecs/Opus/Opus.cs` — required

Resolve Opus as a statically linked plugin in a WebGL build.

```diff
-#if UNITY_IOS && !UNITY_EDITOR
+        // Dissonance 4 Web patch: a WebGL player links its native plugins statically,
+        // and IL2CPP only emits a direct call for the module name "__Internal" - any
+        // other name becomes a dynamic library lookup, which Emscripten builds do not
+        // have. Same reason iOS is already special cased.
+#if (UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR
         private const string ImportString = "__Internal";
         private const CallingConvention Convention = CallingConvention.Cdecl;
 #else
         private const string ImportString = "opus";
         private const CallingConvention Convention = CallingConvention.Cdecl;
 #endif
```

**Why.** IL2CPP resolves a `DllImport` whose module is not `__Internal` at runtime through
`il2cpp::os::LibraryLoader`, and on Emscripten that always fails; the shipped libil2cpp source
returns an invalid handle unconditionally, with the comment
"we do not use Emscripten/WebAssembly dynamic linking support". `__Internal` is the marker that
makes IL2CPP emit a direct call to a statically linked symbol instead. iOS is already special
cased here for the same reason.

One line changes; everything downstream of it in this file keeps working, including the
`opus_get_version_string` import that names an explicit `EntryPoint`, and the
`dissonance_opus_*_ctl_*` wrappers, which a WebGL build resolves against
`Runtime/Plugins/WebGL/dissonance_opus_shim.c`.

A missing `libopus.a` now fails the Emscripten link rather than throwing at runtime, which is the
better of the two - and `Dissonance4WebBuildCheck` stops the build before that with a message
naming the build script.

---

## 4. `Core/DissonanceCommsImpl.cs` — optional

Stop probing for a native library that a browser does not need.

```diff
+            // Dissonance 4 Web patch: AudioPluginDissonance has no WebAssembly build and
+            // a browser does not need one, because the browser does this work itself.
+            // Probing for it there only logs a dependency error that nothing can fix.
+#if !UNITY_WEBGL || UNITY_EDITOR
             // Getting the filter state loads the DLL (and has no side effects, it's just a getter). Will throw if DLL is missing.
             AudioPluginDissonanceNative.Dissonance_GetFilterState();
+#endif
```

**Why.** `TestDependencies` deliberately touches both native libraries at startup so a missing one
is reported early. In a browser, `AudioPluginDissonance` is missing by design - echo cancellation
and noise suppression are the browser's job here - so the probe throws, the surrounding try/catch
logs `Dependency Error: Unable to load DLL 'AudioPluginDissonance'`, and the player is left with an
error that looks like a broken install and cannot be acted on.

Nothing breaks without this patch. The message is simply wrong.

---

## After a Dissonance update

Updating Dissonance overwrites these files, so re-run **Patch Dissonance For Web**. **Check
Patches** reports the state of all four, and `Dissonance4WebBuildCheck` fails a WebGL build that
is missing a required one rather than letting it run for several minutes and then fail at the link
step.

If a patch reports that its anchor text is not there, Dissonance has changed that part of its
source. The diffs above are small enough to re-derive by hand; the three required ones are an
`InternalsVisibleTo`, a factory hook, and one `#if` condition.
