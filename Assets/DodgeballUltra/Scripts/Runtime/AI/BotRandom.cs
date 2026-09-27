using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Per-bot deterministic random source (a seeded <see cref="System.Random"/>), so every bot has its own reproducible
    /// sequence independent of <see cref="UnityEngine.Random"/> and of the other bots. Allocation-free after construction.
    /// </summary>
    public sealed class BotRandom
    {
        private System.Random _rng;
        private bool _hasSpareGaussian;
        private float _spareGaussian;

        public int Seed { get; private set; }

        public BotRandom(int seed) => Reseed(seed);

        /// <summary>Restarts the sequence from <paramref name="seed"/>.</summary>
        public void Reseed(int seed)
        {
            Seed = seed;
            _rng = new System.Random(seed);
            _hasSpareGaussian = false;
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float Value => (float)_rng.NextDouble();

        /// <summary>Uniform float in [<paramref name="min"/>, <paramref name="max"/>).</summary>
        public float Range(float min, float max) => min + (max - min) * Value;

        /// <summary>Uniform float in [range.x, range.y).</summary>
        public float Range(Vector2 range) => Range(range.x, range.y);

        /// <summary>Uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
        public int Range(int minInclusive, int maxExclusive) =>
            maxExclusive <= minInclusive ? minInclusive : _rng.Next(minInclusive, maxExclusive);

        /// <summary>True with probability <paramref name="probability"/> (clamped to 0..1).</summary>
        public bool Chance(float probability)
        {
            if (probability <= 0f) return false;
            if (probability >= 1f) return true;
            return Value < probability;
        }

        /// <summary>-1 or +1 with equal probability.</summary>
        public float Sign() => Value < 0.5f ? -1f : 1f;

        /// <summary><paramref name="value"/> scaled by a uniform factor in [1 - fraction, 1 + fraction].</summary>
        public float Jitter(float value, float fraction) => value * (1f + Range(-fraction, fraction));

        /// <summary>Standard normal sample (mean 0, sigma 1) using the Box-Muller transform.</summary>
        public float Gaussian()
        {
            if (_hasSpareGaussian)
            {
                _hasSpareGaussian = false;
                return _spareGaussian;
            }

            double u1 = 1.0 - _rng.NextDouble(); // (0, 1] avoids log(0)
            double u2 = _rng.NextDouble();
            double mag = System.Math.Sqrt(-2.0 * System.Math.Log(u1));
            double angle = 2.0 * System.Math.PI * u2;
            _spareGaussian = (float)(mag * System.Math.Sin(angle));
            _hasSpareGaussian = true;
            return (float)(mag * System.Math.Cos(angle));
        }

        /// <summary>Normal sample with <paramref name="mean"/> and <paramref name="sigma"/>.</summary>
        public float Gaussian(float mean, float sigma) => mean + Gaussian() * sigma;

        /// <summary>Mixes two integers into a well-distributed, non-zero seed.</summary>
        public static int MakeSeed(int a, int b)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                int seed = (int)(h & 0x7FFFFFFF);
                return seed == 0 ? 1 : seed;
            }
        }
    }
}
