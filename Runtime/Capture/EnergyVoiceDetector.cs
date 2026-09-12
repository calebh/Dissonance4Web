using System;
using Dissonance.Audio.Capture;
using JetBrains.Annotations;

namespace Dissonance.Web
{
    /// <summary>
    /// Decides whether a frame of captured audio is speech, by energy relative to
    /// the noise floor.
    /// </summary>
    /// <remarks>
    /// This stands in for Dissonance's WebRTC voice detector, which lives in a
    /// native library with no WebAssembly build. It is a simpler thing than the
    /// original - no spectral model, so it cannot tell speech from a door slam -
    /// but it does not have to be as clever, because the signal reaching it has
    /// already been through the browser's noise suppressor and gain control.
    ///
    /// The design is the usual one for this job. A slowly adapting estimate of the
    /// noise floor, a threshold some distance above it, and hysteresis so a word
    /// with a quiet syllable in the middle does not chop into two: opening needs
    /// one loud frame, closing needs a run of quiet ones.
    /// </remarks>
    internal class EnergyVoiceDetector
    {
        /// <summary>
        /// How long the gate stays open after the signal drops back below the
        /// threshold. Long enough to ride out the gap between words.
        /// </summary>
        private const int HangoverFrames = 25;

        /// <summary>
        /// Frames above the threshold before the gate opens. Two frames of 10ms
        /// each is short enough not to clip a word, long enough to ignore a click.
        /// </summary>
        private const int AttackFrames = 2;

        /// <summary>
        /// Absolute floor, so a completely silent input never opens the gate no
        /// matter how low the noise estimate has drifted.
        /// </summary>
        private const float AbsoluteFloor = 0.0015f;

        /// <summary>
        /// How fast the noise estimate follows a quieter signal. Deliberately
        /// slower than <see cref="NoiseRiseRate"/>: coming down fast would let the
        /// estimate settle inside a pause between words and then treat the next
        /// word as noise.
        /// </summary>
        private const float NoiseFallRate = 0.002f;

        private const float NoiseRiseRate = 0.02f;

        private float _noiseFloor = AbsoluteFloor;
        private int _hangover;
        private int _attack;

        public bool IsSpeaking { get; private set; }

        /// <summary>The noise floor estimate, for diagnostics.</summary>
        public float NoiseFloor => _noiseFloor;

        public void Reset()
        {
            _noiseFloor = AbsoluteFloor;
            _hangover = 0;
            _attack = 0;
            IsSpeaking = false;
        }

        /// <summary>
        /// Classify one frame, updating <see cref="IsSpeaking"/>.
        /// </summary>
        public void Analyse([NotNull] float[] frame, VadSensitivityLevels sensitivity)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            var energy = RootMeanSquare(frame);

            // Track the noise floor only while the gate is shut. Adapting during
            // speech would pull the estimate up towards the speaker's own level
            // and then gate them out mid-sentence.
            if (!IsSpeaking)
            {
                var rate = energy > _noiseFloor ? NoiseRiseRate : NoiseFallRate;
                _noiseFloor += (energy - _noiseFloor) * rate;
                _noiseFloor = Math.Max(_noiseFloor, AbsoluteFloor * 0.25f);
            }

            var threshold = Math.Max(_noiseFloor * MarginFor(sensitivity), AbsoluteFloor);
            var loud = energy > threshold;

            if (loud)
            {
                _hangover = HangoverFrames;

                if (!IsSpeaking && ++_attack >= AttackFrames)
                    IsSpeaking = true;
            }
            else
            {
                _attack = 0;

                if (IsSpeaking && --_hangover <= 0)
                    IsSpeaking = false;
            }
        }

        /// <summary>
        /// How far above the noise floor the signal has to be, per sensitivity
        /// level. Higher sensitivity means a smaller margin, so quieter speech
        /// opens the gate and more non-speech gets through with it.
        /// </summary>
        private static float MarginFor(VadSensitivityLevels sensitivity)
        {
            switch (sensitivity)
            {
                case VadSensitivityLevels.LowSensitivity:
                    return 6f;
                case VadSensitivityLevels.MediumSensitivity:
                    return 4f;
                case VadSensitivityLevels.HighSensitivity:
                    return 2.5f;
                case VadSensitivityLevels.VeryHighSensitivity:
                    return 1.8f;
                default:
                    return 4f;
            }
        }

        private static float RootMeanSquare([NotNull] float[] frame)
        {
            double sum = 0;
            for (var i = 0; i < frame.Length; i++)
                sum += (double)frame[i] * frame[i];

            return (float)Math.Sqrt(sum / frame.Length);
        }
    }
}
