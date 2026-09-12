using System;
using System.Collections.Generic;
using Dissonance.Audio;
using Dissonance.Audio.Capture;
using Dissonance.Config;
using Dissonance.VAD;
using JetBrains.Annotations;
using NAudio.Wave;

namespace Dissonance.Web
{
    /// <summary>
    /// The capture side of Dissonance's audio pipeline, rebuilt for a browser:
    /// no background thread, and no WebRTC audio processing library.
    /// </summary>
    /// <remarks>
    /// Dissonance's own pipeline cannot run in a WebGL player for two separate
    /// reasons. It drives preprocessing from a dedicated thread, and a Web player
    /// is single threaded - <c>Thread.Start</c> throws. It also calls into the
    /// AudioPluginDissonance native library for echo cancellation, noise
    /// suppression and voice detection, and that library has no WebAssembly build.
    ///
    /// Neither loss costs much here, because the browser already does the same
    /// work. Echo cancellation, noise suppression and gain control are requested
    /// through getUserMedia constraints in <see cref="WebMicrophoneCapture"/>, and
    /// the browser's echo canceller is in a better position than Dissonance's:
    /// it sits next to the output device and so cancels all of the game's audio.
    ///
    /// What is left for this class is the part that has to match Dissonance
    /// exactly - resampling the browser's capture rate to the 48kHz, 480 sample
    /// frames the encoder expects - plus a voice activity detector to stand in for
    /// the native one.
    ///
    /// Everything runs inline on the Unity main thread, pushed along by each call
    /// to <see cref="ReceiveMicrophoneData"/>.
    /// </remarks>
    internal class WebPreprocessingPipeline
        : IPreprocessingPipeline
    {
        /// <summary>Frame size the encoder pipeline is fed, at 48kHz. 10ms.</summary>
        public const int FrameSize = 480;

        public const int SampleRate = 48000;

        private static readonly Log Log = Logs.Create(LogCategory.Recording, "Web Preprocessing Pipeline");

        private readonly BufferedSampleProvider _input;
        private readonly Resampler _resampler;
        private readonly SampleToFrameProvider _frames;
        private readonly float[] _frame = new float[FrameSize];

        private readonly List<IMicrophoneSubscriber> _micSubscribers = new List<IMicrophoneSubscriber>();
        private readonly List<IVoiceActivationListener> _vadSubscribers = new List<IVoiceActivationListener>();

        private readonly EnergyVoiceDetector _vad = new EnergyVoiceDetector();

        private ArvCalculator _arv;

        private bool _resetRequested;

        public WaveFormat OutputFormat { get; }

        public int OutputFrameSize => FrameSize;

        public float Amplitude => _arv.ARV;

        /// <summary>
        /// Ignored. Dissonance reports capture latency so that an echo canceller
        /// can line the captured and played signals up, and the browser's canceller
        /// works that out for itself.
        /// </summary>
        public TimeSpan UpstreamLatency
        {
            set { }
        }

        /// <summary>
        /// Ignored. Dissonance passes this on to the WebRTC gain control so it can
        /// stop adapting while nobody is listening. There is no equivalent to tell
        /// here; the browser's gain control is not ours to drive.
        /// </summary>
        public bool IsOutputMuted
        {
            set { }
        }

        public WebPreprocessingPipeline([NotNull] WaveFormat inputFormat)
        {
            if (inputFormat == null)
                throw new ArgumentNullException(nameof(inputFormat));

            OutputFormat = new WaveFormat(SampleRate, 1);

            // Sized like Dissonance's own pipeline: enough slack that a frame
            // hitch does not lose audio, and small enough that recovering from one
            // does not add lasting latency.
            _input = new BufferedSampleProvider(inputFormat, FrameSize * 32);
            _resampler = new Resampler(_input, SampleRate);
            _frames = new SampleToFrameProvider(_resampler, (uint)FrameSize);

            Log.Debug("Created web preprocessing pipeline: {0}Hz in, {1}Hz out", inputFormat.SampleRate, SampleRate);
        }

        public void Start()
        {
            ApplyReset();
        }

        public void Dispose()
        {
            _micSubscribers.Clear();

            // Tell anyone listening for voice activation that it has stopped, so a
            // trigger does not stay latched open across a pipeline restart.
            if (_vad.IsSpeaking)
            {
                for (var i = 0; i < _vadSubscribers.Count; i++)
                    NotifyStopped(_vadSubscribers[i]);
            }

            _vadSubscribers.Clear();
        }

        /// <summary>
        /// Requests that buffered audio be thrown away and the stream treated as
        /// starting fresh.
        /// </summary>
        public void Reset()
        {
            // Deferred so a reset arriving in the middle of a batch of samples
            // does not discard the samples that came with it.
            _resetRequested = true;
        }

        private void ApplyReset()
        {
            _input.Reset();
            _resampler.Reset();
            _frames.Reset();

            _arv.Reset();
            _vad.Reset();

            for (var i = 0; i < _micSubscribers.Count; i++)
            {
                try
                {
                    _micSubscribers[i].Reset();
                }
                catch (Exception ex)
                {
                    Log.Error($"Microphone subscriber '{_micSubscribers[i].GetType().Name}' threw during Reset: {ex}");
                }
            }

            _resetRequested = false;
        }

        public void ReceiveMicrophoneData(ArraySegment<float> buffer, [NotNull] WaveFormat format)
        {
            if (buffer.Array == null)
                throw new ArgumentNullException(nameof(buffer));
            if (format == null)
                throw new ArgumentNullException(nameof(format));

            if (!format.Equals(_input.WaveFormat))
                throw new ArgumentException($"Expected {_input.WaveFormat} from the microphone but got {format}", nameof(format));

            if (_resetRequested)
                ApplyReset();

            var remaining = buffer;
            while (remaining.Count > 0)
            {
                // Drain before writing. A block larger than the input buffer is
                // then consumed in pieces instead of overflowing, and Write always
                // has at least a frame of room to make progress with.
                DrainFrames();

                var written = _input.Write(remaining);
                if (written == 0)
                {
                    Log.Warn($"Dropped {remaining.Count} captured samples; the preprocessing buffer would not accept them");
                    break;
                }

                remaining = new ArraySegment<float>(remaining.Array, remaining.Offset + written, remaining.Count - written);
            }

            DrainFrames();
        }

        private void DrainFrames()
        {
            var wasSpeaking = _vad.IsSpeaking;

            while (_frames.Read(new ArraySegment<float>(_frame)))
            {
                _vad.Analyse(_frame, VoiceSettings.Instance.VadSensitivity);

                // Dissonance mutes by dropping the encoder subscription rather than
                // by zeroing the signal, so a muted stream still has to be measured
                // and analysed - that is what keeps the amplitude meter and the
                // voice activation triggers alive while muted.
                SendFrame();
            }

            if (wasSpeaking != _vad.IsSpeaking)
            {
                for (var i = 0; i < _vadSubscribers.Count; i++)
                {
                    if (_vad.IsSpeaking)
                        NotifyStarted(_vadSubscribers[i]);
                    else
                        NotifyStopped(_vadSubscribers[i]);
                }
            }
        }

        private void SendFrame()
        {
            var segment = new ArraySegment<float>(_frame);

            _arv.Update(segment);

            for (var i = 0; i < _micSubscribers.Count; i++)
            {
                try
                {
                    _micSubscribers[i].ReceiveMicrophoneData(segment, OutputFormat);
                }
                catch (Exception ex)
                {
                    Log.Error($"Microphone subscriber '{_micSubscribers[i].GetType().Name}' threw: {ex}");
                }
            }
        }

        public void Subscribe(IMicrophoneSubscriber listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            _micSubscribers.Add(listener);
        }

        public bool Unsubscribe(IMicrophoneSubscriber listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            return _micSubscribers.Remove(listener);
        }

        public void Subscribe(IVoiceActivationListener listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            _vadSubscribers.Add(listener);

            if (_vad.IsSpeaking)
                NotifyStarted(listener);
        }

        public bool Unsubscribe(IVoiceActivationListener listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            if (!_vadSubscribers.Remove(listener))
                return false;

            if (_vad.IsSpeaking)
                NotifyStopped(listener);

            return true;
        }

        private static void NotifyStarted([NotNull] IVoiceActivationListener listener)
        {
            try
            {
                listener.VoiceActivationStart();
            }
            catch (Exception ex)
            {
                Log.Error($"Voice activation subscriber '{listener.GetType().Name}' threw: {ex}");
            }
        }

        private static void NotifyStopped([NotNull] IVoiceActivationListener listener)
        {
            try
            {
                listener.VoiceActivationStop();
            }
            catch (Exception ex)
            {
                Log.Error($"Voice activation subscriber '{listener.GetType().Name}' threw: {ex}");
            }
        }
    }
}
