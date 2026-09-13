using System;
using System.Collections.Generic;
using Dissonance.Audio.Capture;
using JetBrains.Annotations;
using NAudio.Wave;
using UnityEngine;

namespace Dissonance.Web
{
    /// <summary>
    /// When a browser player is asked for microphone access.
    /// </summary>
    /// <remarks>
    /// The values are serialized as integers, so their numbers are part of the
    /// format: add new ones at the end.
    /// </remarks>
    public enum MicrophoneAccessRequest
    {
        /// <summary>
        /// As soon as the component starts. Voice is ready the moment the player
        /// talks, at the cost of prompting every player, including those who only
        /// ever listen.
        /// </summary>
        OnStart = 0,

        /// <summary>
        /// The first time this player tries to send voice: pressing push to talk,
        /// or being in an open channel. Players who only listen are never prompted,
        /// and hear voice chat either way.
        /// </summary>
        /// <remarks>
        /// A voice activation trigger counts as trying to send voice as soon as it
        /// is enabled and unmuted, because it can only hear the player through the
        /// microphone - waiting for it to detect speech would wait forever.
        /// </remarks>
        OnFirstTransmission = 1,

        /// <summary>
        /// Only when <see cref="WebMicrophoneCapture.RequestAccess"/> is called, for a
        /// project that puts the prompt behind a button of its own.
        /// </summary>
        Manual = 2
    }

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
    /// Opening the microphone is decoupled from Dissonance starting capture. As far
    /// as Dissonance can tell, capture starts as soon as it asks - with the format
    /// the microphone will have, which is the shared AudioContext's rate - and no
    /// audio arrives until the browser opens the microphone, which happens when
    /// <see cref="AccessRequest"/> says it should. A player who is never asked, or
    /// never answers, simply transmits nothing and still hears everyone.
    ///
    /// Add this to the same game object as <see cref="DissonanceComms"/> and it
    /// will be picked up automatically. <see cref="DissonanceWebAudio"/> does that
    /// for you on the platforms where it belongs.
    /// </remarks>
    public class WebMicrophoneCapture
        : MonoBehaviour, IMicrophoneCapture, IMicrophoneDeviceList
    {
        private static readonly Log Log = Logs.Create(LogCategory.Recording, "Web Microphone Capture");

        /// <summary>
        /// Seconds between searches for voice activation triggers while waiting for
        /// a first transmission. Finding every trigger in the scene is not free, and
        /// a trigger being enabled is not something that needs a same-frame answer.
        /// </summary>
        private const float TriggerScanInterval = 0.5f;

        [Header("Browser audio constraints")]
        [Tooltip("Ask the browser to cancel echo of the output device from the captured signal. Leave this on unless the player is on headphones and you have measured that it hurts.")]
        public bool EchoCancellation = true;

        [Tooltip("Ask the browser to suppress steady background noise.")]
        public bool NoiseSuppression = true;

        [Tooltip("Ask the browser to normalise the input level.")]
        public bool AutoGainControl = true;

        [Header("Microphone access")]
        [Tooltip("When to ask the player for microphone access. On First Transmission spares players who only listen the browser prompt; they still hear voice chat. Manual waits for RequestAccess(). Can be changed at runtime, up until access has been requested.")]
        public MicrophoneAccessRequest AccessRequest = MicrophoneAccessRequest.OnStart;

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

        /// <summary>
        /// Whether Dissonance believes capture is running.
        /// </summary>
        private bool _capturing;

        /// <summary>
        /// Capture is running from Dissonance's side, but the browser microphone
        /// is not open yet, so there is no audio to deliver.
        /// </summary>
        private bool _awaitingMicrophone;

        /// <summary>
        /// Latched once this player's microphone should be open, by whichever
        /// route: the <see cref="AccessRequest"/> policy, or an explicit call. Never
        /// cleared, so a device change reopens the microphone rather than going
        /// back to waiting.
        /// </summary>
        private bool _accessWanted;

        /// <summary>
        /// A getUserMedia request is in effect for the current device.
        /// </summary>
        private bool _accessRequested;

        private bool _unsupported;
        private bool _reportedFailure;
        private bool _warnedAboutSuspendedAudio;
        private WebMicrophoneState _lastState = WebMicrophoneState.Idle;
        private float _nextTriggerScan;

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
        /// The browser's reason for not opening the microphone, or an empty string.
        /// </summary>
        public string Error => WebAudioNative.MicrophoneError;

        /// <summary>
        /// Whether this player's microphone has been asked for, by any route. False
        /// means the player has not seen a browser prompt and is listening only.
        /// </summary>
        public bool IsAccessRequested => _accessWanted;

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
                _unsupported = true;
            }
        }

        private void OnDestroy()
        {
            if (_accessRequested)
                WebAudioNative.D4W_MicStop();
        }

        /// <summary>
        /// Ask the browser for microphone access, prompting the user if they have
        /// not decided yet. Works under any <see cref="AccessRequest"/> setting.
        /// </summary>
        /// <remarks>
        /// After a refusal this makes a fresh request, but whether the player sees a
        /// prompt again is the browser's decision: many remember a refusal for the
        /// page until the player changes it in the site settings, and fail the
        /// request straight away until then. <see cref="Error"/> says so when it
        /// happens.
        /// </remarks>
        public void RequestAccess()
        {
            if (!WebAudioNative.IsAvailable || _unsupported)
                return;

            _accessWanted = true;

            if (State == WebMicrophoneState.Failed)
            {
                // A fresh request, and a fresh look at the state it produces: Update
                // turns a later grant into the pipeline reset that clears Dissonance's
                // "cannot start mic" flag.
                _accessRequested = false;
                _lastState = WebMicrophoneState.Idle;
            }

            OpenMicrophone();
        }

        /// <summary>
        /// Try the microphone again after it failed to open. The same as
        /// <see cref="RequestAccess"/>, including its caveat about browsers that
        /// remember a refusal.
        /// </summary>
        public void Retry()
        {
            RequestAccess();
        }

        private void OpenMicrophone()
        {
            if (_accessRequested)
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

            if (requested != Device)
            {
                Device = requested;

                // A stream that is open, or opening, belongs to the old device. It is
                // reopened below if this player's microphone is wanted at all; if it
                // is not, there is nothing to close.
                if (_accessRequested)
                {
                    Log.Info($"Switching browser microphone to '{requested ?? "<default>"}'");

                    WebAudioNative.D4W_MicStop();
                    _accessRequested = false;
                }
            }

            ApplyAccessPolicy();
            if (_accessWanted && !_unsupported)
                OpenMicrophone();

            var state = State;

            if (state == WebMicrophoneState.Failed)
            {
                // Returning null makes Dissonance disable transmission. This player
                // keeps hearing voice chat; Retry is the way back.
                ReportFailure();
                return null;
            }

            if (state == WebMicrophoneState.Running)
            {
                var micRate = WebAudioNative.D4W_MicSampleRate();
                if (micRate <= 0)
                {
                    Log.Warn("The browser reported a running microphone with no sample rate; voice capture will retry");
                    return null;
                }

                return BeginCapture(micRate, awaitingMicrophone: false);
            }

            // The microphone is not open yet, because nothing has asked for it or the
            // browser is still waiting on the player. Start anyway, with the format
            // it will have: the capture graph runs in the same AudioContext as
            // playback, so the context's rate is the microphone's rate. The
            // alternative, returning null until the microphone opens, makes Dissonance
            // warn that transmission is disabled - which, for a player who is only
            // listening, is not news worth a warning.
            var contextRate = WebAudioNative.D4W_OutSampleRate();
            if (contextRate <= 0)
            {
                Log.Warn("The browser has no Web Audio context, so voice capture cannot start");
                return null;
            }

            if (state == WebMicrophoneState.Starting)
            {
                Log.Info("Waiting for the browser to grant microphone access; voice will be sent once it does");
            }
            else
            {
                Log.Info(
                    AccessRequest == MicrophoneAccessRequest.Manual
                        ? "Voice capture is ready; the microphone will be opened when RequestAccess is called"
                        : "Voice capture is ready; the microphone will be opened when this player first transmits"
                );
            }

            return BeginCapture(contextRate, awaitingMicrophone: true);
        }

        private WaveFormat BeginCapture(int sampleRate, bool awaitingMicrophone)
        {
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
            _awaitingMicrophone = awaitingMicrophone;

            if (!awaitingMicrophone)
                Log.Info($"Started browser microphone capture: {sampleRate}Hz, {(int)Latency.TotalMilliseconds}ms latency");

            return _format;
        }

        public void StopCapture()
        {
            if (!_capturing)
                return;

            _capturing = false;
            _awaitingMicrophone = false;

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
        /// Opens the microphone when <see cref="AccessRequest"/> says it is time, and
        /// watches for the browser granting access after Dissonance had given up.
        /// </summary>
        /// <remarks>
        /// This cannot all live in <see cref="UpdateSubscribers"/>, because Dissonance
        /// only calls that while <see cref="IsRecording"/> is true - and after a
        /// refusal, it is not. So the poll runs on this component's own Update.
        /// </remarks>
        private void Update()
        {
            if (!WebAudioNative.IsAvailable || _unsupported)
                return;

            ApplyAccessPolicy();
            if (_accessWanted)
                OpenMicrophone();

            var state = State;
            if (state == _lastState)
                return;

            _lastState = state;

            if (state == WebMicrophoneState.Failed)
            {
                ReportFailure();
                return;
            }

            // While capture is running, UpdateSubscribers handles the microphone
            // opening. This path is for the capture pipeline having been disabled by
            // an earlier failure, which only a forced reset clears.
            if (state != WebMicrophoneState.Running || _capturing)
                return;

            Log.Info("The browser granted microphone access; restarting voice capture");

            if (_comms != null)
                _comms.ResetMicrophoneCapture();
        }

        /// <summary>
        /// Latches <see cref="_accessWanted"/> if the <see cref="AccessRequest"/>
        /// policy says the microphone should be open by now.
        /// </summary>
        private void ApplyAccessPolicy()
        {
            if (_accessWanted)
                return;

            switch (AccessRequest)
            {
                case MicrophoneAccessRequest.OnStart:
                    _accessWanted = true;
                    break;

                case MicrophoneAccessRequest.OnFirstTransmission:
                    // Only while Dissonance is running a transmission pipeline, which
                    // is Dissonance's own precondition for sending voice. A channel
                    // opened in a menu before joining a session is not a player
                    // trying to talk to anyone.
                    if (_capturing && TryDetectTransmission(out var reason))
                    {
                        Log.Info($"Requesting microphone access: {reason}");
                        _accessWanted = true;
                    }
                    break;

                case MicrophoneAccessRequest.Manual:
                    break;
            }
        }

        /// <summary>
        /// Whether this player is trying to send voice, or would be if there were a
        /// microphone to hear them.
        /// </summary>
        private bool TryDetectTransmission(out string reason)
        {
            reason = null;

            if (_comms == null || _comms.IsMuted)
                return false;

            // The same test Dissonance uses to decide whether to feed its encoder:
            // not muted, and at least one channel open. That covers push to talk,
            // open mic, proximity triggers, and code that opens channels directly.
            if (_comms.RoomChannels.Count + _comms.PlayerChannels.Count > 0)
            {
                reason = "this player started transmitting";
                return true;
            }

            // Voice activation is the exception. Its triggers open a channel when
            // they hear speech, and they can only hear speech through the microphone
            // this is deciding whether to open. An enabled, unmuted voice activation
            // trigger is the player having opted in to transmitting, so it counts.
            var now = Time.unscaledTime;
            if (now < _nextTriggerScan)
                return false;

            _nextTriggerScan = now + TriggerScanInterval;

            // The FindObjectsSortMode overload is deprecated in newer editors but still
            // works in every Unity 6 release this package supports.
#pragma warning disable CS0618
            var broadcastTriggers = FindObjectsByType<VoiceBroadcastTrigger>(FindObjectsSortMode.None);
            var proximityTriggers = FindObjectsByType<VoiceProximityBroadcastTrigger>(FindObjectsSortMode.None);
#pragma warning restore CS0618

            return FindVoiceActivationTrigger(broadcastTriggers, out reason)
                || FindVoiceActivationTrigger(proximityTriggers, out reason);
        }

        private static bool FindVoiceActivationTrigger<T>([NotNull] T[] triggers, out string reason)
            where T : Behaviour, IVoiceBroadcastTrigger
        {
            for (var i = 0; i < triggers.Length; i++)
            {
                var trigger = triggers[i];

                if (trigger.isActiveAndEnabled && trigger.Mode == CommActivationMode.VoiceActivation && !trigger.IsMuted)
                {
                    reason = $"voice activation trigger '{trigger.name}' can only hear this player through the microphone";
                    return true;
                }
            }

            reason = null;
            return false;
        }

        /// <returns>true if the capture pipeline should be reset.</returns>
        public bool UpdateSubscribers()
        {
            if (!WebAudioNative.IsAvailable || !_capturing)
                return false;

            var state = State;

            if (_awaitingMicrophone)
            {
                switch (state)
                {
                    case WebMicrophoneState.Running:
                        // The microphone just opened. Restart rather than feeding the
                        // pipeline Dissonance built without it, so the encoder starts
                        // clean: one that was started and stopped while there was no
                        // audio has not finished stopping, and would otherwise do so on
                        // the first real audio. StartCapture then begins with the
                        // microphone's own sample rate.
                        Log.Info("The browser opened the microphone; restarting voice capture");
                        return true;

                    case WebMicrophoneState.Failed:
                        // Restarting lets StartCapture report the failure and disable
                        // transmission, leaving this player listening only.
                        return true;

                    default:
                        return false;
                }
            }

            if (state != WebMicrophoneState.Running)
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

            // Not "refused": the same state covers a missing device, an unplugged
            // one, and a browser that cannot capture. The browser's own reason says
            // which.
            var error = Error;
            Log.Error(
                string.IsNullOrEmpty(error)
                    ? "The browser did not open the microphone, so this player cannot transmit voice"
                    : $"The browser did not open the microphone, so this player cannot transmit voice: {error}"
            );
        }
    }
}
