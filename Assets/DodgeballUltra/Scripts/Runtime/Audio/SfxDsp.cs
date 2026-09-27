using System;

namespace DodgeballUltra.Audio
{
    /// <summary>Fast deterministic xorshift RNG for synthesis (no allocation, reproducible variants).</summary>
    public struct SfxRng
    {
        private uint _state;

        public SfxRng(uint seed)
        {
            _state = seed == 0u ? 0x9E3779B9u : seed;
            for (int i = 0; i < 4; i++) NextUInt(); // decorrelate nearby seeds
        }

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float Value => (NextUInt() & 0xFFFFFF) / 16777216f;

        /// <summary>Uniform in [-1, 1).</summary>
        public float Signed => Value * 2f - 1f;

        public float Range(float min, float max) => min + (max - min) * Value;

        /// <summary>Returns <paramref name="value"/> randomly scaled by +/- <paramref name="fraction"/>.</summary>
        public float Jitter(float value, float fraction) => value * (1f + Signed * fraction);
    }

    /// <summary>RBJ biquad filter (transposed direct form II). Coefficients can be updated while running (sweeps).</summary>
    public struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad LowPass(float sampleRate, float frequency, float q)
        {
            var f = new Biquad();
            f.SetLowPass(sampleRate, frequency, q);
            return f;
        }

        public static Biquad HighPass(float sampleRate, float frequency, float q)
        {
            var f = new Biquad();
            f.SetHighPass(sampleRate, frequency, q);
            return f;
        }

        public static Biquad BandPass(float sampleRate, float frequency, float q)
        {
            var f = new Biquad();
            f.SetBandPass(sampleRate, frequency, q);
            return f;
        }

        public void SetLowPass(float sampleRate, float frequency, float q)
        {
            Prepare(sampleRate, frequency, q, out float cs, out float alpha);
            float a0 = 1f + alpha;
            _b0 = (1f - cs) * 0.5f / a0;
            _b1 = (1f - cs) / a0;
            _b2 = _b0;
            _a1 = -2f * cs / a0;
            _a2 = (1f - alpha) / a0;
        }

        public void SetHighPass(float sampleRate, float frequency, float q)
        {
            Prepare(sampleRate, frequency, q, out float cs, out float alpha);
            float a0 = 1f + alpha;
            _b0 = (1f + cs) * 0.5f / a0;
            _b1 = -(1f + cs) / a0;
            _b2 = _b0;
            _a1 = -2f * cs / a0;
            _a2 = (1f - alpha) / a0;
        }

        /// <summary>Band-pass with 0 dB peak gain.</summary>
        public void SetBandPass(float sampleRate, float frequency, float q)
        {
            Prepare(sampleRate, frequency, q, out float cs, out float alpha);
            float a0 = 1f + alpha;
            _b0 = alpha / a0;
            _b1 = 0f;
            _b2 = -alpha / a0;
            _a1 = -2f * cs / a0;
            _a2 = (1f - alpha) / a0;
        }

        private static void Prepare(float sampleRate, float frequency, float q, out float cs, out float alpha)
        {
            float f = Math.Max(10f, Math.Min(frequency, sampleRate * 0.45f));
            double w = 2.0 * Math.PI * f / sampleRate;
            cs = (float)Math.Cos(w);
            alpha = (float)Math.Sin(w) / (2f * Math.Max(0.05f, q));
        }

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    /// <summary>Pink (1/f) noise generator (Paul Kellet's economy filter): natural-sounding rumble, crowds, air.</summary>
    public struct PinkNoise
    {
        private float _b0, _b1, _b2;

        public float Next(ref SfxRng rng)
        {
            float white = rng.Signed;
            _b0 = 0.99765f * _b0 + white * 0.0990460f;
            _b1 = 0.96300f * _b1 + white * 0.2965164f;
            _b2 = 0.57000f * _b2 + white * 1.0526913f;
            return (_b0 + _b1 + _b2 + white * 0.1848f) * 0.22f;
        }
    }

    /// <summary>Brown (1/f^2) noise: deep rumbles (earthquake, crowd stomps).</summary>
    public struct BrownNoise
    {
        private float _last;

        public float Next(ref SfxRng rng)
        {
            _last = (_last + 0.02f * rng.Signed) / 1.02f;
            return _last * 3.5f;
        }
    }

    /// <summary>Smoothed random modulator (random walk low-passed): syllable-like amplitude movement for crowds, flutter.</summary>
    public struct RandomLfo
    {
        private float _value, _target, _countdown;

        /// <summary>Advances by one sample. <paramref name="rateHz"/> = how often a new target is picked.</summary>
        public float Next(ref SfxRng rng, float rateHz, float sampleRate, float smoothing = 0.0015f)
        {
            _countdown -= 1f;
            if (_countdown <= 0f)
            {
                _target = rng.Value;
                _countdown = sampleRate / Math.Max(0.01f, rateHz) * rng.Range(0.6f, 1.4f);
            }
            _value += (_target - _value) * smoothing;
            return _value;
        }
    }

    /// <summary>
    /// Synthesis helpers operating on mono float buffers (44.1 kHz by default). All functions add into the buffer so
    /// layers can be summed, then <see cref="Finish"/> normalises, de-clicks and optionally adds gym reverb.
    /// </summary>
    public static class SfxDsp
    {
        public const float TwoPi = (float)(2.0 * Math.PI);

        public enum Filter
        {
            None = 0,
            LowPass,
            HighPass,
            BandPass,
        }

        public static float[] Alloc(float seconds, int sampleRate) => new float[Math.Max(1, (int)(seconds * sampleRate))];

        /// <summary>Linear attack then exponential decay (time constant <paramref name="decay"/>).</summary>
        public static float AttackDecay(float t, float attack, float decay)
        {
            if (t < 0f) return 0f;
            if (t < attack) return t / Math.Max(1e-5f, attack);
            return (float)Math.Exp(-(t - attack) / Math.Max(1e-5f, decay));
        }

        /// <summary>Attack, sustain for <paramref name="hold"/>, then linear release - for whistles, hums, beeps.</summary>
        public static float Gate(float t, float attack, float hold, float release)
        {
            if (t < 0f) return 0f;
            if (t < attack) return t / Math.Max(1e-5f, attack);
            t -= attack;
            if (t < hold) return 1f;
            t -= hold;
            return t < release ? 1f - t / Math.Max(1e-5f, release) : 0f;
        }

        /// <summary>Smooth bell (raised cosine) over <paramref name="duration"/>.</summary>
        public static float Bell(float t, float duration)
        {
            if (t < 0f || t > duration) return 0f;
            return 0.5f - 0.5f * (float)Math.Cos(TwoPi * t / duration);
        }

        /// <summary>
        /// Adds a (pitch-gliding) sine tone: frequency moves exponentially from <paramref name="f0"/> to <paramref name="f1"/>
        /// with time constant <paramref name="glide"/> (0 = constant). Optional 2nd/3rd harmonics, attack/decay envelope.
        /// </summary>
        public static void Tone(float[] buffer, int sr, float start, float f0, float f1, float glide, float amp, float attack, float decay,
            float h2 = 0f, float h3 = 0f, float maxDuration = 10f)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(buffer.Length - s0, (int)(maxDuration * sr));
            double phase = 0.0;
            float inv = 1f / sr;
            for (int i = 0; i < n; i++)
            {
                float t = i * inv;
                float env = AttackDecay(t, attack, decay);
                if (t > attack && env < 1e-4f) break;
                float f = glide > 0f ? f1 + (f0 - f1) * (float)Math.Exp(-t / glide) : f0;
                phase += TwoPi * f * inv;
                if (phase > TwoPi * 1000.0) phase -= TwoPi * 1000.0;
                float s = (float)Math.Sin(phase);
                if (h2 != 0f) s += h2 * (float)Math.Sin(phase * 2.0);
                if (h3 != 0f) s += h3 * (float)Math.Sin(phase * 3.0);
                buffer[s0 + i] += amp * env * s;
            }
        }

        /// <summary>
        /// Adds filtered noise with an attack/decay envelope. The filter frequency sweeps exponentially from
        /// <paramref name="f0"/> to <paramref name="f1"/> over <paramref name="sweepTime"/> seconds (whooshes, explosions).
        /// </summary>
        public static void Noise(float[] buffer, int sr, ref SfxRng rng, float start, float amp, float attack, float decay,
            Filter filter, float f0, float f1 = -1f, float sweepTime = 0.3f, float q = 0.9f, bool pink = false, float maxDuration = 10f)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(buffer.Length - s0, (int)(maxDuration * sr));
            if (f1 <= 0f) f1 = f0;
            var bq = new Biquad();
            var pn = new PinkNoise();
            float inv = 1f / sr;
            float ratio = f1 / Math.Max(1f, f0);
            for (int i = 0; i < n; i++)
            {
                float t = i * inv;
                float env = AttackDecay(t, attack, decay);
                if (t > attack && env < 1e-4f) break;
                if ((i & 31) == 0 && filter != Filter.None)
                {
                    float k = Math.Min(1f, t / Math.Max(1e-4f, sweepTime));
                    float f = f0 * (float)Math.Pow(ratio, k);
                    switch (filter)
                    {
                        case Filter.LowPass: bq.SetLowPass(sr, f, q); break;
                        case Filter.HighPass: bq.SetHighPass(sr, f, q); break;
                        default: bq.SetBandPass(sr, f, q); break;
                    }
                }
                float x = pink ? pn.Next(ref rng) * 3f : rng.Signed;
                float y = filter == Filter.None ? x : bq.Process(x);
                buffer[s0 + i] += amp * env * y;
            }
        }

        /// <summary>
        /// Sparse random impulses ("crackle"): each impulse is a tiny noise tick filtered by a high-pass. Density moves
        /// linearly from <paramref name="densityStart"/> to <paramref name="densityEnd"/> impulses per second.
        /// Used for ice cracking, fire crackle, claps, debris rattles.
        /// </summary>
        public static void Crackle(float[] buffer, int sr, ref SfxRng rng, float start, float duration, float densityStart, float densityEnd,
            float amp, float tickDecay, float highPass, float lowPass = 0f)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(buffer.Length - s0, (int)(duration * sr));
            var hp = Biquad.HighPass(sr, highPass, 0.7f);
            var lp = lowPass > 0f ? Biquad.LowPass(sr, lowPass, 0.7f) : default;
            float decayK = (float)Math.Exp(-1.0 / Math.Max(1e-5f, tickDecay * sr));
            float excite = 0f;
            float inv = 1f / sr;
            for (int i = 0; i < n; i++)
            {
                float k = n > 1 ? (float)i / (n - 1) : 0f;
                float density = densityStart + (densityEnd - densityStart) * k;
                if (rng.Value < density * inv) excite = amp * rng.Range(0.35f, 1f);
                float x = excite * rng.Signed;
                excite *= decayK;
                float y = hp.Process(x);
                if (lowPass > 0f) y = lp.Process(y);
                buffer[s0 + i] += y;
            }
        }

        /// <summary>Soft saturation (adds weight to impacts without harsh clipping).</summary>
        public static void Saturate(float[] buffer, float drive)
        {
            if (drive <= 0f) return;
            float norm = 1f / (float)Math.Tanh(1f + drive);
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (float)Math.Tanh(buffer[i] * (1f + drive)) * norm;
        }

        /// <summary>Applies a biquad over the whole buffer (in place).</summary>
        public static void ApplyFilter(float[] buffer, int sr, Filter filter, float frequency, float q = 0.707f)
        {
            if (filter == Filter.None) return;
            var bq = filter == Filter.LowPass ? Biquad.LowPass(sr, frequency, q)
                : filter == Filter.HighPass ? Biquad.HighPass(sr, frequency, q) : Biquad.BandPass(sr, frequency, q);
            for (int i = 0; i < buffer.Length; i++) buffer[i] = bq.Process(buffer[i]);
        }

        /// <summary>
        /// Small-hall reverb (Schroeder/Freeverb-style: 4 damped combs + 2 all-passes, with pre-delay) giving the hard,
        /// slightly boomy early reflections of a sports hall. <paramref name="mix"/> 0..1 wet level.
        /// </summary>
        public static void Room(float[] buffer, int sr, float mix, float rt60 = 1.1f, float damping = 0.35f, float preDelay = 0.012f)
        {
            if (mix <= 0f) return;
            float scale = sr / 44100f * 1.25f; // slightly larger than Freeverb's default room
            int[] combLengths = { (int)(1116 * scale), (int)(1188 * scale), (int)(1277 * scale), (int)(1356 * scale) };
            int[] apLengths = { (int)(556 * scale), (int)(441 * scale) };
            var combs = new float[4][];
            var combIdx = new int[4];
            var combFb = new float[4];
            var combStore = new float[4];
            for (int c = 0; c < 4; c++)
            {
                combs[c] = new float[combLengths[c]];
                // Feedback for a -60 dB decay in rt60 seconds.
                combFb[c] = (float)Math.Pow(10.0, -3.0 * combLengths[c] / sr / Math.Max(0.1f, rt60));
            }
            var aps = new float[2][];
            var apIdx = new int[2];
            for (int a = 0; a < 2; a++) aps[a] = new float[apLengths[a]];
            int pre = Math.Max(1, (int)(preDelay * sr));
            var preBuf = new float[pre];
            int preIdx = 0;

            for (int i = 0; i < buffer.Length; i++)
            {
                float dry = buffer[i];
                float input = preBuf[preIdx];
                preBuf[preIdx] = dry;
                preIdx = (preIdx + 1) % pre;

                float wet = 0f;
                for (int c = 0; c < 4; c++)
                {
                    var line = combs[c];
                    int idx = combIdx[c];
                    float y = line[idx];
                    combStore[c] = y * (1f - damping) + combStore[c] * damping;
                    line[idx] = input + combStore[c] * combFb[c];
                    combIdx[c] = idx + 1 >= line.Length ? 0 : idx + 1;
                    wet += y;
                }
                wet *= 0.25f;
                for (int a = 0; a < 2; a++)
                {
                    var line = aps[a];
                    int idx = apIdx[a];
                    float buf = line[idx];
                    float o = -wet + buf;
                    line[idx] = wet + buf * 0.5f;
                    apIdx[a] = idx + 1 >= line.Length ? 0 : idx + 1;
                    wet = o;
                }
                buffer[i] = dry + wet * mix;
            }
        }

        /// <summary>Reverses the buffer in place ("rewind" sounds).</summary>
        public static void Reverse(float[] buffer) => Array.Reverse(buffer);

        /// <summary>
        /// Normalises to <paramref name="peak"/>, applies short linear fades (no clicks) and trims trailing near-silence.
        /// Returns the (possibly shorter) buffer.
        /// </summary>
        public static float[] Finish(float[] buffer, int sr, float peak = 0.9f, float fadeIn = 0.001f, float fadeOut = 0.01f)
        {
            float max = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float a = Math.Abs(buffer[i]);
                if (a > max) max = a;
            }
            if (max < 1e-6f) return buffer;
            float g = peak / max;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= g;

            // Trim the silent tail (below -66 dB) to keep clips short.
            int end = buffer.Length;
            const float threshold = 0.0005f;
            while (end > sr / 50 && Math.Abs(buffer[end - 1]) < threshold) end--;
            end = Math.Min(buffer.Length, end + sr / 100);
            if (end < buffer.Length)
            {
                var trimmed = new float[end];
                Array.Copy(buffer, trimmed, end);
                buffer = trimmed;
            }

            int fi = Math.Min(buffer.Length / 2, (int)(fadeIn * sr));
            for (int i = 0; i < fi; i++) buffer[i] *= (float)i / fi;
            int fo = Math.Min(buffer.Length / 2, (int)(fadeOut * sr));
            for (int i = 0; i < fo; i++) buffer[buffer.Length - 1 - i] *= (float)i / fo;
            return buffer;
        }

        /// <summary>
        /// Makes a seamless loop: the audio after the loop point is cross-faded (equal power) into the start, so the last
        /// sample flows into the first. Returns a buffer <paramref name="crossfadeSeconds"/> shorter than the input.
        /// </summary>
        public static float[] MakeLoop(float[] buffer, int sr, float crossfadeSeconds)
        {
            int x = Math.Max(1, (int)(crossfadeSeconds * sr));
            int length = buffer.Length - x;
            if (length <= x) return buffer;
            var result = new float[length];
            Array.Copy(buffer, result, length);
            for (int i = 0; i < x; i++)
            {
                float k = (float)i / x;
                float fadeIn = (float)Math.Sin(k * Math.PI * 0.5);
                float fadeOut = (float)Math.Cos(k * Math.PI * 0.5);
                result[i] = buffer[i] * fadeIn + buffer[length + i] * fadeOut;
            }
            return result;
        }

        /// <summary>Peak-normalises without trimming or fading (loops must keep their exact length).</summary>
        public static void Normalize(float[] buffer, float peak)
        {
            float max = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float a = Math.Abs(buffer[i]);
                if (a > max) max = a;
            }
            if (max < 1e-6f) return;
            float g = peak / max;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= g;
        }
    }
}
