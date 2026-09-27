using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// Deterministic, thread-safe, tileable noise used by <see cref="ArenaTextureGenerator"/>. Integer hashing only (no
    /// UnityEngine.Random, no Mathf.PerlinNoise), so the same seed produces bit-identical textures on every platform and
    /// the functions can run on worker threads.
    /// </summary>
    /// <remarks>
    /// Tileability: every lattice coordinate is wrapped by a period (in cells). Sampling <c>x in [0, period)</c> across a
    /// texture therefore tiles seamlessly. fBm doubles frequency and period per octave, which preserves tiling.
    /// </remarks>
    internal static class ArenaNoise
    {
        /// <summary>32-bit avalanche hash of a lattice point.</summary>
        public static uint Hash(int x, int y, uint seed)
        {
            unchecked
            {
                uint h = seed * 0x9E3779B1u + 0x7F4A7C15u;
                h ^= (uint)x * 0x85EBCA77u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)y * 0xC2B2AE3Du;
                h = (h << 17) | (h >> 15);
                h *= 0x27D4EB2Fu;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return h;
            }
        }

        /// <summary>Hash of a lattice point mapped to [0, 1).</summary>
        public static float Hash01(int x, int y, uint seed) => (Hash(x, y, seed) & 0x00FFFFFFu) * (1f / 16777216f);

        /// <summary>Second independent hash channel of the same point (for cell jitter etc.).</summary>
        public static float Hash01B(int x, int y, uint seed) => (Hash(x, y, seed ^ 0xA5A5A5A5u) & 0x00FFFFFFu) * (1f / 16777216f);

        public static int Wrap(int v, int period)
        {
            if (period <= 0) return v;
            int r = v % period;
            return r < 0 ? r + period : r;
        }

        public static int FastFloor(float v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }

        /// <summary>Quintic fade (C2 continuous) for lattice interpolation.</summary>
        public static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        /// <summary>Tileable value noise in [0, 1]. Periods are in lattice cells.</summary>
        public static float Value(float x, float y, int periodX, int periodY, uint seed)
        {
            int xi = FastFloor(x);
            int yi = FastFloor(y);
            float fx = x - xi;
            float fy = y - yi;
            int x0 = Wrap(xi, periodX), x1 = Wrap(xi + 1, periodX);
            int y0 = Wrap(yi, periodY), y1 = Wrap(yi + 1, periodY);

            float a = Hash01(x0, y0, seed);
            float b = Hash01(x1, y0, seed);
            float c = Hash01(x0, y1, seed);
            float d = Hash01(x1, y1, seed);
            float u = Fade(fx);
            float v = Fade(fy);
            float ab = a + (b - a) * u;
            float cd = c + (d - c) * u;
            return ab + (cd - ab) * v;
        }

        /// <summary>Tileable fractal Brownian motion in [0, 1] (octaves double frequency and period).</summary>
        public static float Fbm(float x, float y, int periodX, int periodY, int octaves, float gain, uint seed)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            int px = periodX, py = periodY;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Value(x * freq, y * freq, px, py, seed + (uint)i * 1013u);
                norm += amp;
                amp *= gain;
                freq *= 2f;
                px *= 2;
                py *= 2;
            }
            return norm > 0f ? sum / norm : 0.5f;
        }

        /// <summary>
        /// Tileable Worley (cellular) noise: distance (in cell units) to the nearest jittered feature point and that
        /// cell's hash, so callers can give every cell (pebble, pore, chip) its own random attributes.
        /// </summary>
        public static float Cellular(float x, float y, int periodX, int periodY, uint seed, float jitter, out uint cellHash)
        {
            int xi = FastFloor(x);
            int yi = FastFloor(y);
            float best = float.MaxValue;
            uint bestHash = 0;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = xi + ox, cy = yi + oy;
                    int wx = Wrap(cx, periodX), wy = Wrap(cy, periodY);
                    uint h = Hash(wx, wy, seed);
                    float jx = ((h & 0xFFFFu) * (1f / 65535f) - 0.5f) * jitter + 0.5f;
                    float jy = (((h >> 16) & 0xFFFFu) * (1f / 65535f) - 0.5f) * jitter + 0.5f;
                    float dx = cx + jx - x;
                    float dy = cy + jy - y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < best)
                    {
                        best = d2;
                        bestHash = h;
                    }
                }
            }
            cellHash = bestHash;
            return Mathf.Sqrt(best);
        }

        /// <summary>Smoothstep without Mathf's argument order surprises.</summary>
        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
