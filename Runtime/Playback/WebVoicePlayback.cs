using System;
using Dissonance.Audio.Playback;
using UnityEngine;

namespace Dissonance.Web
{
    /// <summary>
    /// Plays one remote player's voice through the Web Audio API.
    /// </summary>
    /// <remarks>
    /// Dissonance normally plays voice by attaching a DSP filter to an AudioSource
    /// and filling it from <c>OnAudioFilterRead</c>. Unity's Web player has no
    /// software audio mixer - its audio layer is a thin wrapper over Web Audio
    /// nodes - so no DSP callback is ever raised there and that approach produces
    /// silence. This component keeps everything above the output - the jitter
    /// buffer, the Opus decoder, the drift correction, the volume and priority
    /// rules, all of which are Dissonance's and platform independent - and
    /// replaces only the last step, pushing decoded samples into an AudioWorklet
    /// instead.
    ///
    /// The cost of going around Unity's audio is that Unity's spatialisation goes
    /// with it. Distance attenuation and stereo panning are computed here from the
    /// AudioListener and applied by Web Audio nodes, which is an approximation of
    /// an AudioSource with linear rolloff - not of whatever rolloff curve, mixer
    /// group or spatialiser plugin the desktop build uses.
    /// </remarks>
    public class WebVoicePlayback
        : BaseVoicePlayback
    {
        private static readonly Log Log = Logs.Create(LogCategory.Playback, "Web Voice Playback");

        [Header("Buffering")]
        [Tooltip("How much decoded audio to keep queued in the browser, in milliseconds. Lower is more responsive; too low and a slow frame becomes an audible gap. One Unity frame at 30fps is 33ms.")]
        [Range(20, 250)]
        public float TargetBufferMs = 70;

        [Header("Positional playback")]
        [Tooltip("Distance at which a positional voice starts getting quieter.")]
        public float MinDistance = 1;

        [Tooltip("Distance at which a positional voice becomes silent.")]
        public float MaxDistance = 100;

        /// <summary>
        /// Frames to wait for the audio graph before saying something. Loading the
        /// worklet module takes a frame or two; several seconds means it is broken.
        /// </summary>
        private const int InitAttemptsBeforeComplaining = 300;

        private int _handle;
        private int _initAttempts;
        private int _outputRate;

        private float[] _buffer;

        private SpeechSession? _session;
        private SessionContext _lastSession;

        private float _arv;

        private IAudioOutputSubscriber[] _subscribers;

        private Transform _listener;

        public override float Amplitude => _arv;

        protected override SpeechSession? TryGetActiveSession()
        {
            return _session;
        }

        /// <summary>
        /// Whether the browser is actually able to play this voice. A browser
        /// suspends its AudioContext until the page has seen a user gesture.
        /// </summary>
        public bool IsOutputRunning => WebAudioNative.D4W_OutIsRunning() != 0;

        private void Awake()
        {
            _subscribers = GetComponentsInChildren<IAudioOutputSubscriber>();

            ((IVoicePlaybackInternal)this).Reset();
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            if (!WebAudioNative.IsAvailable)
            {
                Log.Error(
                    "WebVoicePlayback only works in a WebGL player. Use Dissonance's own VoicePlayback prefab elsewhere; " +
                    "DissonanceWebAudio picks the right one for the platform."
                );
                enabled = false;
                return;
            }

            _initAttempts = 0;
            TryOpenChannel();
        }

        /// <summary>
        /// Opens this speaker's output channel, if the browser is ready for one.
        /// </summary>
        /// <remarks>
        /// Loading the AudioWorklet module is asynchronous, so the first attempt
        /// usually comes back not-ready. That is not a failure and must not disable
        /// playback: a speaker who joins in the first few frames would be silent
        /// for the rest of the session. Update keeps asking.
        /// </remarks>
        private bool TryOpenChannel()
        {
            if (_handle != 0)
                return true;

            _initAttempts++;

            if (WebAudioNative.D4W_OutInit() == 0)
            {
                // The plugin logs the reason to the browser console when the module
                // genuinely fails; this says the same thing in Unity's log, once,
                // for a wait that has gone on much too long to be module loading.
                if (_initAttempts == InitAttemptsBeforeComplaining)
                {
                    Log.Error(
                        "The browser audio output graph is still not ready after many frames, so remote voice cannot be played. " +
                        "The browser console has the reason; the usual one is a page that is not a secure context, which has no AudioWorklet."
                    );
                }

                return false;
            }

            _outputRate = WebAudioNative.D4W_OutSampleRate();
            if (_outputRate <= 0)
            {
                Log.Error("The browser reported no output sample rate, so remote voice cannot be played");
                enabled = false;
                return false;
            }

            // Enough for a full top-up at the largest buffer the inspector allows,
            // so one Update never needs more than one read. TopUp clamps to this
            // length anyway, so a larger TargetBufferMs set from code degrades to
            // a shorter buffer rather than reading out of bounds.
            if (_buffer == null || _buffer.Length < Mathf.CeilToInt(_outputRate * 0.26f))
                _buffer = new float[Mathf.CeilToInt(_outputRate * 0.26f)];

            _handle = WebAudioNative.D4W_OutCreate();
            if (_handle == 0)
            {
                Log.Error("Could not create a browser audio output channel, so this player's voice cannot be played");
                enabled = false;
                return false;
            }

            return true;
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            _session = null;
            _arv = 0;

            if (_handle != 0)
            {
                WebAudioNative.D4W_OutDestroy(_handle);
                _handle = 0;
            }
        }

        protected override void Update()
        {
            base.Update();

            if (!TryOpenChannel())
                return;

            UpdateSpatialisation();

            if (!_session.HasValue)
            {
                var next = TryDequeueSession(_outputRate);
                if (!next.HasValue)
                    return;

                _session = next;
                _lastSession = next.Value.Context;
                _arv = 0;

                // Whatever is left in the browser belongs to the previous session.
                WebAudioNative.D4W_OutReset(_handle);

                Log.Debug("Began playback of speech session {0} for {1}", _lastSession.Id, _lastSession.PlayerName);
            }

            var underruns = WebAudioNative.D4W_OutTakeUnderrunCount(_handle);
            if (underruns > 0)
                Log.Debug("Browser audio output ran dry {0} times for {1}", underruns, PlayerName);

            TopUp(_session.Value);
        }

        /// <summary>
        /// Decode and hand over however much audio the browser needs to stay
        /// <see cref="TargetBufferMs"/> ahead.
        /// </summary>
        private void TopUp(SpeechSession session)
        {
            var target = Mathf.CeilToInt(TargetBufferMs * _outputRate / 1000f);
            var queued = WebAudioNative.D4W_OutQueued(_handle);

            var wanted = Mathf.Min(target - queued, _buffer.Length);
            if (wanted <= 0)
                return;

            var segment = new ArraySegment<float>(_buffer, 0, wanted);

            // Read always fills the whole segment, padding with silence when the
            // stream has run out, and reports whether that was the last of it.
            var complete = session.Read(segment);

            if (_subscribers != null)
            {
                for (var i = 0; i < _subscribers.Length; i++)
                {
                    try
                    {
                        _subscribers[i].OnAudioPlayback(segment, complete);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Audio output subscriber '{_subscribers[i].GetType().Name}' threw: {ex}");
                    }
                }
            }

            _arv = AverageRectifiedValue(segment);

            var accepted = WebAudioNative.D4W_OutWrite(_handle, _buffer, wanted);
            if (accepted < wanted)
            {
                // The browser side buffer is bounded, so this means the estimate of
                // what was queued was stale. Dropping the tail is the right call:
                // holding it back would only push playback further behind.
                Log.Debug("Browser audio output took {0} of {1} samples for {2}", accepted, wanted, PlayerName);
            }

            if (complete)
            {
                Log.Debug("Finished playback of speech session {0} for {1}", _lastSession.Id, _lastSession.PlayerName);

                _session = null;
                _arv = 0;
            }
        }

        protected override void ForceReset()
        {
            base.ForceReset();

            _session = null;
            _arv = 0;

            if (_handle != 0)
                WebAudioNative.D4W_OutReset(_handle);
        }

        /// <summary>
        /// Works out distance attenuation and stereo pan for this speaker and
        /// hands them to the browser.
        /// </summary>
        private void UpdateSpatialisation()
        {
            var positional = ((IVoicePlaybackInternal)this).AllowPositionalPlayback
                          && (LatestPlaybackOptions?.IsPositional ?? false);

            if (!positional)
            {
                WebAudioNative.D4W_OutSetGain(_handle, 1, 0);
                return;
            }

            var listener = FindListener();
            if (listener == null)
            {
                WebAudioNative.D4W_OutSetGain(_handle, 1, 0);
                return;
            }

            var offset = transform.position - listener.position;
            var distance = offset.magnitude;

            // Linear rolloff, matching the default Dissonance playback prefab
            // rather than Unity's logarithmic default.
            var span = Mathf.Max(0.0001f, MaxDistance - MinDistance);
            var gain = Mathf.Clamp01(1 - (distance - MinDistance) / span);

            var pan = distance > 0.0001f
                ? Mathf.Clamp(Vector3.Dot(offset / distance, listener.right), -1, 1)
                : 0;

            WebAudioNative.D4W_OutSetGain(_handle, gain, pan);
        }

        private Transform FindListener()
        {
            if (_listener != null)
                return _listener;

#if UNITY_2023_1_OR_NEWER
            var listener = FindAnyObjectByType<AudioListener>();
#else
            var listener = FindObjectOfType<AudioListener>();
#endif
            if (listener != null)
                _listener = listener.transform;

            return _listener;
        }

        private static float AverageRectifiedValue(ArraySegment<float> samples)
        {
            if (samples.Array == null || samples.Count == 0)
                return 0;

            float sum = 0;
            for (var i = 0; i < samples.Count; i++)
                sum += Math.Abs(samples.Array[samples.Offset + i]);

            return sum / samples.Count;
        }
    }
}
