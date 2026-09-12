using System;
using System.Collections.Generic;
using Dissonance.Audio.Capture;
using UnityEngine;

namespace Dissonance.Web
{
    /// <summary>
    /// Makes Dissonance work in a browser. Add it to the same game object as
    /// <see cref="DissonanceComms"/> and leave it there on every platform.
    /// </summary>
    /// <remarks>
    /// Three things in Dissonance's audio pipeline have no browser equivalent, and
    /// this component swaps each of them out - but only in a WebGL player, so a
    /// project can ship one scene for desktop and web:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// Capture. Unity's <c>Microphone</c> exists in a Web player but records
    /// through MediaRecorder and will not hand over samples until recording stops,
    /// so <see cref="WebMicrophoneCapture"/> uses getUserMedia directly.
    /// </description></item>
    /// <item><description>
    /// Preprocessing. Dissonance drives its capture pipeline from a dedicated
    /// thread and processes audio in a native library; a Web player has neither.
    /// <see cref="WebPreprocessingPipeline"/> does the resampling and framing
    /// inline and leaves echo cancellation and noise suppression to the browser.
    /// </description></item>
    /// <item><description>
    /// Playback. Unity's Web audio layer raises no DSP callback, so Dissonance's
    /// <c>OnAudioFilterRead</c> based playback is silent there.
    /// <see cref="WebVoicePlayback"/> pushes decoded samples into an AudioWorklet.
    /// </description></item>
    /// </list>
    ///
    /// The codec is not one of them. Opus runs as WebAssembly in the browser, from
    /// the same managed Dissonance code as everywhere else, so a browser and a
    /// desktop player exchange Opus frames directly and nothing in the path
    /// transcodes. See <c>Documentation~/Setup.md</c> for the one time build step
    /// that produces the WebAssembly library.
    /// </remarks>
    [RequireComponent(typeof(DissonanceComms))]
    [HelpURL("https://github.com/calebh/Dissonance4Web")]
    public class DissonanceWebAudio
        : MonoBehaviour
    {
        private static readonly Log Log = Logs.Create(LogCategory.Core, "Dissonance Web Audio");

        [Header("Playback")]
        [Tooltip("Prefab used for voice playback in a browser. Leave empty to build a minimal one at runtime. Any prefab assigned here must have a WebVoicePlayback component; use it to set buffer size and positional audio distances, or to attach an IAudioOutputSubscriber.")]
        public GameObject WebPlaybackPrefab;

        [Header("Capture")]
        [Tooltip("Ask the browser to cancel echo of the output device from the captured signal.")]
        public bool EchoCancellation = true;

        [Tooltip("Ask the browser to suppress steady background noise.")]
        public bool NoiseSuppression = true;

        [Tooltip("Ask the browser to normalise the input level.")]
        public bool AutoGainControl = true;

        [Tooltip("Ask for microphone access as soon as the scene loads. Turn this off to put the browser permission prompt behind a button of your own, then call RequestMicrophoneAccess().")]
        public bool RequestMicrophoneAccessOnStart = true;

        private WebMicrophoneCapture _microphone;

        /// <summary>
        /// Whether this build is actually running in a browser, and so whether the
        /// substitutions this component makes are in effect.
        /// </summary>
        public static bool IsWebPlayer => WebAudioNative.IsAvailable;

        /// <summary>
        /// The browser capture component, or null outside a Web player.
        /// </summary>
        public WebMicrophoneCapture Microphone => _microphone;

        private void Awake()
        {
            if (!IsWebPlayer)
            {
                // Everything Dissonance does by default works on this platform.
                // Deliberately silent: this component is meant to be left in the
                // scene for every build target.
                return;
            }

            // Loading the AudioWorklet module is asynchronous, so start it now
            // rather than when the first player speaks. It is normally ready within
            // a frame or two, which is well before anyone joins.
            WebAudioNative.D4W_OutInit();

            InstallPreprocessor();
            InstallMicrophone();
            InstallPlaybackPrefab();
        }

        private void OnDestroy()
        {
            // Only withdraw the hook if it is still ours. A second comms object, or
            // a project with its own pipeline, may have replaced it since.
            if (CapturePipelineManager.PreprocessorFactory == CreatePreprocessor)
                CapturePipelineManager.PreprocessorFactory = null;
        }

        /// <summary>
        /// Ask the browser for microphone access, prompting the user if they have
        /// not decided yet. Does nothing outside a Web player.
        /// </summary>
        /// <remarks>
        /// Worth calling from a button rather than on load: the player gets some
        /// context for the prompt, and a browser remembers a refusal.
        /// </remarks>
        public void RequestMicrophoneAccess()
        {
            _microphone?.RequestAccess();
        }

        /// <summary>
        /// Whether the browser is currently willing to play audio.
        /// </summary>
        /// <remarks>
        /// A browser starts its AudioContext suspended and will not run it until
        /// the page has seen a real user gesture - a click or a key press. While
        /// it is suspended, voice is neither captured nor played. Unity unlocks
        /// its own audio the same way and this shares that context, so in practice
        /// it is running by the time anyone is in a game; it matters for a scene
        /// that starts a voice session before the player has touched anything.
        /// </remarks>
        public bool IsAudioRunning => !IsWebPlayer || WebAudioNative.D4W_OutIsRunning() != 0;

        /// <summary>
        /// Ask the browser to resume audio. Only works from a user gesture, so
        /// call it from a button or a first-click handler.
        /// </summary>
        public void ResumeAudio()
        {
            if (IsWebPlayer)
                WebAudioNative.D4W_OutResume();
        }

        /// <summary>
        /// Lists the microphones the browser will admit to having.
        /// </summary>
        /// <remarks>
        /// <c>DissonanceComms.GetMicrophoneDevices</c> returns nothing in a Web
        /// player, because it is compiled around Unity's Microphone class. Use
        /// this instead to populate a device picker, and assign the chosen name to
        /// <c>DissonanceComms.MicrophoneName</c> as usual.
        ///
        /// Browsers hide device labels until the user has granted access once, so
        /// before that this returns placeholder names ("Microphone 1" and so on)
        /// which still select the right device.
        /// </remarks>
        public void GetMicrophoneDevices(List<string> output)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            _microphone?.GetDevices(output);
        }

        private void InstallPreprocessor()
        {
            var existing = CapturePipelineManager.PreprocessorFactory;
            if (existing != null && existing != CreatePreprocessor)
            {
                Log.Warn("Another preprocessing pipeline is already installed; leaving it in place");
                return;
            }

            CapturePipelineManager.PreprocessorFactory = CreatePreprocessor;
        }

        /// <summary>
        /// The hook Dissonance calls to build its capture pipeline.
        /// </summary>
        /// <param name="format">Format the microphone is delivering.</param>
        /// <param name="isMobilePlatform">
        /// Dissonance's own judgement of whether this is a weak device, which it
        /// uses to pick cheaper audio processing. Ignored here: the browser does
        /// the processing and makes that call for itself.
        /// </param>
        private static IPreprocessingPipeline CreatePreprocessor(NAudio.Wave.WaveFormat format, bool isMobilePlatform)
        {
            return new WebPreprocessingPipeline(format);
        }

        private void InstallMicrophone()
        {
            // Dissonance finds the capture implementation with GetComponent, so it
            // only has to be present by the time DissonanceComms starts.
            if (!TryGetComponent(out _microphone))
                _microphone = gameObject.AddComponent<WebMicrophoneCapture>();

            _microphone.EchoCancellation = EchoCancellation;
            _microphone.NoiseSuppression = NoiseSuppression;
            _microphone.AutoGainControl = AutoGainControl;
            _microphone.RequestAccessOnStart = RequestMicrophoneAccessOnStart;

            if (TryGetComponent<BasicMicrophoneCapture>(out var unityCapture))
            {
                Log.Warn(
                    "Found Dissonance's BasicMicrophoneCapture on this game object as well. It cannot capture in a browser, and " +
                    "whichever component GetComponent returns first would win, so it has been disabled."
                );
                unityCapture.enabled = false;
            }
        }

        private void InstallPlaybackPrefab()
        {
            var comms = GetComponent<DissonanceComms>();

            if (WebPlaybackPrefab != null)
            {
                if (WebPlaybackPrefab.GetComponent<WebVoicePlayback>() == null)
                {
                    Log.Error(
                        "The assigned Web Playback Prefab has no WebVoicePlayback component, so it would play nothing in a browser. " +
                        "Falling back to a runtime built prefab."
                    );
                }
                else
                {
                    comms.PlaybackPrefab = WebPlaybackPrefab;
                    return;
                }
            }

            comms.PlaybackPrefab = BuildPlaybackPrefab();
        }

        /// <summary>
        /// Builds the minimal object Dissonance needs for playback.
        /// </summary>
        /// <remarks>
        /// Dissonance treats the playback prefab as a template to instantiate, and
        /// does not care whether it came from an asset, so building it here saves a
        /// project from having to carry a web specific prefab it would never edit.
        /// Assign Web Playback Prefab if you do want to edit one.
        /// </remarks>
        private GameObject BuildPlaybackPrefab()
        {
            var template = new GameObject("Dissonance Web Playback Template");
            template.transform.SetParent(transform, false);

            // Dissonance deactivates the template before instantiating from it, so
            // starting inactive just avoids a frame of it running as a real
            // playback object.
            template.SetActive(false);
            template.AddComponent<WebVoicePlayback>();

            return template;
        }
    }
}
