using System;
using System.Collections.Generic;
using Dissonance.Audio.Capture;
using JetBrains.Annotations;
using NAudio.Wave;
using UnityEngine;

namespace Dissonance.Web
{
    /// <summary>
    /// Captures the microphone in a browser, through getUserMedia and an
    /// AudioWorklet, and feeds the samples into Dissonance.
    /// </summary>
    /// <remarks>
    /// Unity's own <c>Microphone</c> class does exist in a Web player, but it is
    /// built on MediaRecorder: it records compressed chunks and only decodes them
    /// once recording stops, and <c>AudioClip.GetData</c> refuses outright while
    /// recording is in progress. That is fine for record-then-play, and useless
    /// for live voice, so this component goes to the browser APIs directly.
    ///
    /// Echo cancellation, noise suppression and gain control are left to the
    /// browser, asked for through getUserMedia constraints. The browser's echo
    /// canceller sits next to the real output device, so unlike Dissonance's own
    /// it also cancels the rest of the game's audio, not just voice.
    ///
    /// Add this to the same game object as <see cref="DissonanceComms"/> and it
    /// will be picked up automatically. <see cref="DissonanceWebAudio"/> does that
    /// for you on the platforms where it belongs.
    /// </remarks>
    public class WebMicrophoneCapture
        : MonoBehaviour, IMicrophoneCapture, IMicrophoneDeviceList
    {
        private static readonly Log Log = Logs.Create(LogCategory.Recording, "Web Microphone Capture");

        [Header("Browser audio constraints")]
        [Tooltip("Ask the browser to cancel echo of the output device from the captured signal. Leave this on unless the player is on headphones and you have measured that it hurts.")]
        public bool EchoCancellation = true;

        [Tooltip("Ask the browser to suppress steady background noise.")]
        public bool NoiseSuppression = true;

        [Tooltip("Ask the browser to normalise the input level.")]
        public bool AutoGainControl = true;

        [Header("Startup")]
        [Tooltip("Ask for microphone access as soon as this component starts. Turn this off to put the browser permission prompt behind a button of your own, then call RequestAccess().")]
        public bool RequestAccessOnStart = true;

        private readonly List<IMicrophoneSubscriber> _subscribers = new List<IMicrophoneSubscriber>();

        /// <summary>
        /// Samples read out of the browser each frame, before being handed on in
        /// fixed size blocks.
        /// </summary>
        private float[] _readBuffer;

        private float[] _block;
        private int _blockCount;
        private int _blockFilled;

        private WaveFormat _format;
        private bool _capturing;
        private bool _accessRequested;
        private bool _reportedFailure;
        private bool _warnedAboutSuspendedAudio;
        private WebMicrophoneState _lastState = WebMicrophoneState.Idle;

        private DissonanceComms _comms;

        public bool IsRecording => _capturing;

        public string Device { get; private set; }

        public TimeSpan Latency { get; private set; }

        /// <summary>
        /// What the browser is doing with the microphone right now. Useful for a
        /// UI that explains a denied permission prompt.
        /// </summary>
        public WebMicrophoneState State => (WebMicrophoneState)WebAudioNative.D4W_MicState();

        /// <summary>
        /// The browser's reason for refusing the microphone, or an empty string.
        /// </summary>
        public string Error => WebAudioNative.MicrophoneError;

        private void Awake()
        {
            _comms = GetComponent<DissonanceComms>();
        }

        private void Start()
        {
            if (!WebAudioNative.IsAvailable)
            {
                Log.Error(
                    "WebMicrophoneCapture only works in a WebGL player. Remove it from this game object, or add DissonanceWebAudio " +
                    "instead, which adds it only on the platforms that need it."
                );
                return;
            }

            if (WebAudioNative.D4W_MicSupported() == 0)
            {
                Log.Error("This browser does not expose getUserMedia and AudioWorklet, so voice capture is not possible here");
                return;
            }

            if (RequestAccessOnStart)
                RequestAccess();
        }

        private void OnDestroy()
        {
            if (_accessRequested)
                WebAudioNative.D4W_MicStop();
        }

        /// <summary>
        /// Ask the browser for microphone access, prompting the user if they have
        /// not decided yet.
        /// </summary>
        /// <remarks>
        /// Calling this from a click handler rather than on load gives the player
        /// some context for the prompt, and browsers remember a refusal for the
        /// rest of the session.
        /// </remarks>
        public void RequestAccess()
        {
            if (!WebAudioNative.IsAvailable)
                return;

            if (_accessRequested && State != WebMicrophoneState.Failed)
                return;

            Log.Debug("Requesting browser microphone access for device '{0}'", Device ?? "<default>");

            _accessRequested = true;
            _reportedFailure = false;

            WebAudioNative.D4W_MicStart(
                Device ?? string.Empty,
                EchoCancellation ? 1 : 0,
                NoiseSuppression ? 1 : 0,
                AutoGainControl ? 1 : 0
            );
        }

        public WaveFormat StartCapture(string name)
        {
            if (!WebAudioNative.IsAvailable)
                return null;

            // Dissonance uses null and empty interchangeably for "the default
            // device", so normalise before comparing or the pipeline would restart
            // on every reset.
            var requested = string.IsNullOrEmpty(name) ? null : name;

            // A change of device needs a new media stream, so the browser side has
            // to be restarted before capture can begin.
            if (requested != Device)
            {
                Log.Info($"Switching browser microphone to '{requested ?? "<default>"}'");

                Device = requested;
                WebAudioNative.D4W_MicStop();
                _accessRequested = false;
            }

            if (!_accessRequested)
                RequestAccess();

            var state = State;
            if (state != WebMicrophoneState.Running)
            {
                // Returning null tells Dissonance to leave transmission disabled
                // for now. Update watches for the browser granting access and asks
                // for a pipeline reset, which brings us back here.
                if (state == WebMicrophoneState.Failed)
                    ReportFailure();
                else
                    Log.Info("Waiting for the browser to grant microphone access; voice capture will start once it does");

                return null;
            }

            var sampleRate = WebAudioNative.D4W_MicSampleRate();
            if (sampleRate <= 0)
            {
                Log.Warn("The browser reported a running microphone with no sample rate; voice capture will retry");
                return null;
            }

            _format = new WaveFormat(sampleRate, 1);
            Latency = TimeSpan.FromMilliseconds(WebAudioNative.D4W_MicLatencyMs());

            // 10ms blocks. That matches the frame size the preprocessing pipeline
            // wants after resampling, so nothing downstream has to buffer up a
            // partial frame in the common case.
            _blockCount = Math.Max(64, sampleRate / 100);
            _block = new float[_blockCount];
            _blockFilled = 0;
            _readBuffer = new float[_blockCount * 16];

            // Anything the browser captured while Dissonance was not listening is
            // stale by definition.
            DiscardBufferedSamples();

            _capturing = true;

            Log.Info($"Started browser microphone capture: {sampleRate}Hz, {(int)Latency.TotalMilliseconds}ms latency");

            return _format;
        }

        public void StopCapture()
        {
            if (!_capturing)
                return;

            _capturing = false;

            // The media stream is deliberately left open. Stopping it would make
            // the browser drop the permission grant on some platforms, and the
            // capture pipeline is restarted often enough - on every device change
            // and every frame skip - that re-prompting would be intolerable.
            DiscardBufferedSamples();

            for (var i = 0; i < _subscribers.Count; i++)
                SafeReset(_subscribers[i]);

            Log.Debug("Stopped browser microphone capture");
        }

        public void Subscribe(IMicrophoneSubscriber listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            _subscribers.Add(listener);
        }

        public bool Unsubscribe(IMicrophoneSubscriber listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            return _subscribers.Remove(listener);
        }

        /// <summary>
        /// Watches for the browser granting access after Dissonance had given up
        /// on the microphone, and asks it to try again.
        /// </summary>
        /// <remarks>
        /// This cannot live in <see cref="UpdateSubscribers"/>, even though that is
        /// where Dissonance takes a reset request from, because Dissonance only
        /// calls it while <see cref="IsRecording"/> is true - and when it is
        /// waiting for a permission prompt, it is not. So the poll runs on this
        /// component's own Update instead.
        /// </remarks>
        private void Update()
        {
            if (!WebAudioNative.IsAvailable)
                return;

            var state = State;
            if (state == _lastState)
                return;

            _lastState = state;

            if (state == WebMicrophoneState.Failed)
            {
                ReportFailure();
                return;
            }

            if (state != WebMicrophoneState.Running || _capturing)
                return;

            Log.Info("The browser granted microphone access; restarting voice capture");

            // Dissonance disabled transmission when StartCapture returned null, and
            // only a forced reset clears that.
            if (_comms != null)
                _comms.ResetMicrophoneCapture();
        }

        /// <returns>true if the capture pipeline should be reset.</returns>
        public bool UpdateSubscribers()
        {
            if (!WebAudioNative.IsAvailable || !_capturing)
                return false;

            if (State != WebMicrophoneState.Running)
            {
                // The stream went away underneath us - an unplugged headset, or a
                // track the browser stopped. Ask for a restart; StartCapture will
                // then report the failure and leave transmission disabled, which
                // does not loop because Dissonance stops updating capture after
                // that.
                Log.Warn("The browser microphone stopped delivering audio; resetting the capture pipeline");
                return true;
            }

            WarnIfAudioSuspended();

            var overflowed = WebAudioNative.D4W_MicTakeOverflowCount();
            if (overflowed > 0)
                Log.Warn($"Dropped {overflowed} captured samples; nothing read them in time");

            DrainSamples();

            return false;
        }

        public void GetDevices(List<string> output)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            if (!WebAudioNative.IsAvailable)
                return;

            var count = WebAudioNative.D4W_MicDeviceCount();
            for (var i = 0; i < count; i++)
            {
                var name = WebAudioNative.MicrophoneDeviceName(i);
                if (!string.IsNullOrEmpty(name))
                    output.Add(name);
            }
        }

        private void DrainSamples()
        {
            while (true)
            {
                var read = WebAudioNative.D4W_MicRead(_readBuffer, _readBuffer.Length);
                if (read <= 0)
                    return;

                var offset = 0;
                while (offset < read)
                {
                    // Fill up the current block, carrying a partial one over to
                    // the next frame rather than padding it with silence.
                    var take = Math.Min(_blockCount - _blockFilled, read - offset);
                    Array.Copy(_readBuffer, offset, _block, _blockFilled, take);

                    _blockFilled += take;
                    offset += take;

                    if (_blockFilled == _blockCount)
                    {
                        _blockFilled = 0;
                        SendBlock();
                    }
                }

                // A short read means the browser had nothing left.
                if (read < _readBuffer.Length)
                    return;
            }
        }

        private void SendBlock()
        {
            var segment = new ArraySegment<float>(_block, 0, _blockCount);

            for (var i = 0; i < _subscribers.Count; i++)
            {
                try
                {
                    _subscribers[i].ReceiveMicrophoneData(segment, _format);
                }
                catch (Exception ex)
                {
                    Log.Error($"Microphone subscriber '{_subscribers[i].GetType().Name}' threw: {ex}");
                }
            }
        }

        private static void SafeReset([NotNull] IMicrophoneSubscriber subscriber)
        {
            try
            {
                subscriber.Reset();
            }
            catch (Exception ex)
            {
                Log.Error($"Microphone subscriber '{subscriber.GetType().Name}' threw during Reset: {ex}");
            }
        }

        private void DiscardBufferedSamples()
        {
            WebAudioNative.D4W_MicFlush();
            _blockFilled = 0;
        }

        /// <summary>
        /// Says so, once, when the browser has audio suspended.
        /// </summary>
        /// <remarks>
        /// A suspended AudioContext does not run the audio thread at all, so the
        /// capture worklet is never called and no samples arrive. Everything looks
        /// healthy from the outside - the microphone is open, the permission was
        /// granted - and the player is simply inaudible, which is worth one line
        /// in the log rather than a puzzle.
        /// </remarks>
        private void WarnIfAudioSuspended()
        {
            var suspended = WebAudioNative.D4W_OutIsRunning() == 0;

            if (suspended == _warnedAboutSuspendedAudio)
                return;

            _warnedAboutSuspendedAudio = suspended;

            if (suspended)
            {
                Log.Warn(
                    "The browser has suspended audio, so no voice will be captured or heard. Browsers only start audio after the page " +
                    "has seen a click or a key press; call DissonanceWebAudio.ResumeAudio() from one if the player has not interacted yet."
                );
            }
            else
            {
                Log.Info("The browser has resumed audio");
            }
        }

        private void ReportFailure()
        {
            if (_reportedFailure)
                return;
            _reportedFailure = true;

            var error = Error;
            Log.Error(
                string.IsNullOrEmpty(error)
                    ? "The browser refused microphone access, so this player cannot transmit voice"
                    : $"The browser refused microphone access, so this player cannot transmit voice: {error}"
            );
        }

        /// <summary>
        /// Try the microphone again after a refusal, prompting the user once more.
        /// </summary>
        /// <remarks>
        /// Browsers only re-prompt from a user gesture, so call this from a button.
        /// </remarks>
        public void Retry()
        {
            if (!WebAudioNative.IsAvailable)
                return;

            _accessRequested = false;
            _lastState = WebMicrophoneState.Idle;

            // Update takes it from here: it sees the state reach Running and forces
            // the pipeline reset that clears Dissonance's "cannot start mic" flag.
            RequestAccess();
        }
    }
}
