using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// CONTRACT (kernel) - procedural, photo-plausible PBR textures (wood planks with grain and lacquer, sports vinyl,
    /// concrete block, padding fabric, brushed metal...). Deterministic (fixed seeds) so editor-saved PNGs match runtime.
    /// <para>Owner module: Arena.</para>
    /// </summary>
    /// <remarks>
    /// <para>Every surface is modelled in real-world units: a height field in metres (joint depth, plank cupping, pore
    /// depth...) drives a Sobel tangent-space normal map, and albedo / smoothness / AO are derived from the same features so
    /// the maps agree with each other. All textures tile seamlessly; <see cref="ArenaTextureSet.Tiling"/> is the number of
    /// repeats per metre of surface, meant for meshes whose UVs are expressed in metres (see <see cref="ArenaMeshBuilder"/>).</para>
    /// <para>Texture formats: BaseColor is sRGB, Normal and Mask are linear (HDRP mask layout: R metallic, G AO, B detail
    /// mask, A smoothness). All textures have mipmaps, trilinear + anisotropic filtering and repeat wrapping.</para>
    /// <para>Performance: pixel work runs on worker threads (sequential on WebGL); generating the full set at the default
    /// resolution (1024) takes well under a second on desktop. Results are cached per surface/resolution.</para>
    /// </remarks>
    public static class ArenaTextureGenerator
    {
        /// <summary>Smallest texture edge produced.</summary>
        public const int MinResolution = 32;
        /// <summary>Largest accepted <c>resolution</c> argument.</summary>
        public const int MaxResolution = 4096;

        // ---- Hardwood ------------------------------------------------------------------------------------------------
        /// <summary>Width of one maple strip (2-1/4" face, the standard for sports floors), metres.</summary>
        public const float WoodStripWidth = 0.057f;
        /// <summary>Strips across one texture tile.</summary>
        public const int WoodStripsPerTile = 32;
        /// <summary>Metres covered by one hardwood tile (32 strips).</summary>
        public const float WoodTileSize = WoodStripWidth * WoodStripsPerTile;

        private static readonly Dictionary<int, ArenaTextureSet> s_cache = new Dictionary<int, ArenaTextureSet>();

        /// <summary>Generates (or returns the cached) readable texture set for <paramref name="surface"/>.</summary>
        /// <param name="resolution">Edge length of the most detailed map (the hardwood); other surfaces scale down
        /// proportionally (see <see cref="GetTextureSize"/>).</param>
        public static ArenaTextureSet Generate(ArenaSurface surface, int resolution = 1024) => Generate(surface, resolution, true);

        /// <summary>
        /// Generates (or returns the cached) texture set. <paramref name="keepReadable"/> = false releases the CPU copies
        /// after upload (halves memory; use at runtime), true keeps them so editor tools can EncodeToPNG.
        /// </summary>
        public static ArenaTextureSet Generate(ArenaSurface surface, int resolution, bool keepReadable)
        {
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);
            int key = ((int)surface << 16) | (resolution << 1) | (keepReadable ? 1 : 0);
            if (s_cache.TryGetValue(key, out ArenaTextureSet cached) && IsAlive(cached)) return cached;

            ArenaTextureSet set = Create(surface, resolution, keepReadable);
            s_cache[key] = set;
            return set;
        }

        /// <summary>Texture edge length used for <paramref name="surface"/> at a given base <paramref name="resolution"/>.</summary>
        public static int GetTextureSize(ArenaSurface surface, int resolution)
        {
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);
            int divisor;
            switch (surface)
            {
                case ArenaSurface.CourtWood:
                case ArenaSurface.OutfieldZone:
                    divisor = 1; break;
                case ArenaSurface.CourtLinePaint:
                case ArenaSurface.Seats:
                    divisor = 4; break;
                case ArenaSurface.LightEmitter:
                    divisor = 8; break;
                default:
                    divisor = 2; break;
            }
            return Mathf.Max(MinResolution, resolution / divisor);
        }

        /// <summary>Real-world size (metres) one texture tile covers on the surface (u, v).</summary>
        public static Vector2 GetTileSize(ArenaSurface surface)
        {
            switch (surface)
            {
                case ArenaSurface.CourtWood:
                case ArenaSurface.OutfieldZone: return new Vector2(WoodTileSize, WoodTileSize);
                case ArenaSurface.CourtLinePaint: return new Vector2(0.5f, 0.5f);
                case ArenaSurface.RunOffFloor: return new Vector2(0.5f, 0.5f);
                case ArenaSurface.Wall: return new Vector2(1.6f, 1.6f);                // 4 blocks x 8 courses (400 x 200 mm module)
                case ArenaSurface.WallPadding: return new Vector2(PadPanelWidth * 2f, PadPanelHeight);
                case ArenaSurface.Bleachers: return new Vector2(2f, 2f);
                case ArenaSurface.Seats: return new Vector2(0.25f, 0.25f);
                case ArenaSurface.Metal: return new Vector2(0.5f, 0.5f);
                case ArenaSurface.LightEmitter: return new Vector2(0.6f, 0.6f);      // 6 x 6 LED optics, 100 mm pitch
                case ArenaSurface.Ceiling: return new Vector2(1.2f, 1.2f);           // 4 deck ribs, 300 mm pitch
                default: return Vector2.one;
            }
        }

        /// <summary>Wall pad panel width (2 ft), metres.</summary>
        public const float PadPanelWidth = 0.61f;
        /// <summary>Wall pad panel height (6 ft), metres.</summary>
        public const float PadPanelHeight = 1.83f;

        /// <summary>Drops cached sets (optionally destroying their textures). Editor tools call this after saving assets.</summary>
        public static void ClearCache(bool destroyTextures)
        {
            if (destroyTextures)
            {
                var destroyed = new HashSet<Texture2D>();
                foreach (var kv in s_cache)
                {
                    DestroyTex(kv.Value.BaseColor, destroyed);
                    DestroyTex(kv.Value.Normal, destroyed);
                    DestroyTex(kv.Value.Mask, destroyed);
                }
            }
            s_cache.Clear();
        }

        private static void DestroyTex(Texture2D t, HashSet<Texture2D> destroyed)
        {
            if (t == null || !destroyed.Add(t)) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(t);
            else UnityEngine.Object.DestroyImmediate(t);
        }

        private static bool IsAlive(in ArenaTextureSet s) => s.BaseColor != null && s.Normal != null && s.Mask != null;

        // =============================================================================================================
        // Surface dispatch
        // =============================================================================================================

        private static ArenaTextureSet Create(ArenaSurface surface, int resolution, bool readable)
        {
            int size = GetTextureSize(surface, resolution);
            Vector2 tile = GetTileSize(surface);

            if (surface == ArenaSurface.OutfieldZone)
            {
                // Stained wood: same planks (normal + mask shared with CourtWood), own albedo.
                ArenaTextureSet wood = Generate(ArenaSurface.CourtWood, resolution, readable);
                var stainCanvas = new Canvas(size, size, tile.x, tile.y);
                FillWood(stainCanvas, stained: true);
                var stained = wood;
                stained.BaseColor = CreateTexture($"DU_Arena_{surface}_BaseColor_{size}", stainCanvas.W, stainCanvas.H,
                    stainCanvas.Albedo, false, readable);
                stained.Smoothness = 0.78f;
                return stained;
            }

            var cv = new Canvas(size, size, tile.x, tile.y);
            float normalStrength = 1f;
            float emissiveNits = 0f;
            switch (surface)
            {
                case ArenaSurface.CourtWood: FillWood(cv, stained: false); normalStrength = 2f; break;
                case ArenaSurface.CourtLinePaint: FillLinePaint(cv); break;
                case ArenaSurface.RunOffFloor: FillVinyl(cv); normalStrength = 1.5f; break;
                case ArenaSurface.Wall: FillBlockWall(cv); normalStrength = 1.2f; break;
                case ArenaSurface.WallPadding: FillPadding(cv); break;
                case ArenaSurface.Bleachers: FillConcrete(cv); normalStrength = 1.5f; break;
                case ArenaSurface.Seats: FillPlastic(cv); break;
                case ArenaSurface.Metal: FillBrushedMetal(cv); break;
                case ArenaSurface.LightEmitter: FillLedArray(cv); emissiveNits = FloodlightLensNits; break;
                case ArenaSurface.Ceiling: FillSteelDeck(cv); break;
                default: FillFlat(cv, new Color(0.5f, 0.5f, 0.5f), 0.4f); break;
            }

            Color32[] normals = BuildNormals(cv, normalStrength);
            Color32[] mask = BuildMask(cv, out float avgSmooth, out float avgMetal);
            string prefix = $"DU_Arena_{surface}_";

            return new ArenaTextureSet
            {
                BaseColor = CreateTexture(prefix + "BaseColor_" + size, cv.W, cv.H, cv.Albedo, false, readable),
                Normal = CreateTexture(prefix + "Normal_" + size, cv.W, cv.H, normals, true, readable),
                Mask = CreateTexture(prefix + "Mask_" + size, cv.W, cv.H, mask, true, readable),
                Tiling = new Vector2(1f / tile.x, 1f / tile.y),
                Smoothness = avgSmooth,
                Metallic = avgMetal,
                Tint = Color.white,
                NormalStrength = 1f,   // strength is baked into the normal map
                EmissiveColor = emissiveNits > 0f ? new Color(1f, 0.97f, 0.92f, 1f) : Color.black,
                EmissiveIntensity = emissiveNits,
            };
        }

        /// <summary>Luminance of an LED floodlight lens (nits) used for the LightEmitter set. Bright enough to bloom under
        /// arena exposure (EV100 ~9-10) without the physically real ~10^6 nits that would white out the frame.</summary>
        public const float FloodlightLensNits = 25000f;

        // =============================================================================================================
        // Canvas / helpers
        // =============================================================================================================

        /// <summary>Per-pixel working buffers of one surface (height in metres).</summary>
        private sealed class Canvas
        {
            public readonly int W, H;
            public readonly float TileX, TileY;
            public readonly Color32[] Albedo;
            public readonly float[] Height;
            public readonly float[] Smooth;
            public readonly float[] AO;
            public readonly float[] Metal;

            public Canvas(int w, int h, float tileX, float tileY)
            {
                W = w;
                H = h;
                TileX = tileX;
                TileY = tileY;
                int n = w * h;
                Albedo = new Color32[n];
                Height = new float[n];
                Smooth = new float[n];
                AO = new float[n];
                Metal = new float[n];
            }

            public float PixelX => TileX / W;
            public float PixelY => TileY / H;

            public void Put(int i, float r, float g, float b, float height, float smooth, float ao, float metal)
            {
                Albedo[i] = new Color32(ToByte(r), ToByte(g), ToByte(b), 255);
                Height[i] = height;
                Smooth[i] = smooth;
                AO[i] = ao;
                Metal[i] = metal;
            }
        }

        private static byte ToByte(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 1f) return 255;
            return (byte)(v * 255f + 0.5f);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>Runs <paramref name="body"/> for every row, on worker threads where the platform has them.</summary>
        private static void ParallelRows(int rows, Action<int> body)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            for (int y = 0; y < rows; y++) body(y);
#else
            if (rows < 64)
            {
                for (int y = 0; y < rows; y++) body(y);
                return;
            }
            System.Threading.Tasks.Parallel.For(0, rows, body);
#endif
        }

        private static Texture2D CreateTexture(string name, int w, int h, Color32[] pixels, bool linear, bool readable)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, !readable);
            return tex;
        }

        /// <summary>Tangent-space normals from the height field via a wrapping Sobel filter (physical slopes x strength).</summary>
        private static Color32[] BuildNormals(Canvas cv, float strength)
        {
            int w = cv.W, h = cv.H;
            float[] hf = cv.Height;
            var result = new Color32[w * h];
            float sx = strength / (8f * cv.PixelX);
            float sy = strength / (8f * cv.PixelY);

            ParallelRows(h, y =>
            {
                int ym = (y - 1 + h) % h, yp = (y + 1) % h;
                int rowM = ym * w, row = y * w, rowP = yp * w;
                for (int x = 0; x < w; x++)
                {
                    int xm = (x - 1 + w) % w, xp = (x + 1) % w;
                    float tl = hf[rowP + xm], t = hf[rowP + x], tr = hf[rowP + xp];
                    float l = hf[row + xm], r = hf[row + xp];
                    float bl = hf[rowM + xm], b = hf[rowM + x], br = hf[rowM + xp];

                    float dhdx = ((tr + 2f * r + br) - (tl + 2f * l + bl)) * sx;   // +u
                    float dhdy = ((tl + 2f * t + tr) - (bl + 2f * b + br)) * sy;   // +v (texture rows grow with v)
                    float nx = -dhdx, ny = -dhdy, nz = 1f;
                    float inv = 1f / Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    result[row + x] = new Color32(
                        ToByte(nx * inv * 0.5f + 0.5f),
                        ToByte(ny * inv * 0.5f + 0.5f),
                        ToByte(nz * inv * 0.5f + 0.5f),
                        255); // A = 1 so RG and AG (DXT5nm style) unpacking both read X from R
                }
            });
            return result;
        }

        /// <summary>HDRP mask layout: R metallic, G AO, B detail mask, A smoothness.</summary>
        private static Color32[] BuildMask(Canvas cv, out float avgSmooth, out float avgMetal)
        {
            int n = cv.W * cv.H;
            var result = new Color32[n];
            double s = 0, m = 0;
            for (int i = 0; i < n; i++)
            {
                float sm = cv.Smooth[i], mt = cv.Metal[i];
                s += sm;
                m += mt;
                result[i] = new Color32(ToByte(mt), ToByte(cv.AO[i]), 255, ToByte(sm));
            }
            avgSmooth = (float)(s / n);
            avgMetal = (float)(m / n);
            return result;
        }

        private static void FillFlat(Canvas cv, Color c, float smooth)
        {
            for (int i = 0; i < cv.W * cv.H; i++) cv.Put(i, c.r, c.g, c.b, 0f, smooth, 1f, 0f);
        }

        // =============================================================================================================
        // Lacquered maple strip flooring (CourtWood) and stained outfield zones (OutfieldZone)
        // =============================================================================================================

        private const uint SeedWoodLayout = 0x57A1D001u;
        private const uint SeedWoodGrain = 0x57A1D002u;
        private const uint SeedWoodFine = 0x57A1D003u;
        private const uint SeedWoodFleck = 0x57A1D004u;
        private const uint SeedWoodWear = 0x57A1D005u;
        private const uint SeedWoodMineral = 0x57A1D006u;

        /// <summary>
        /// Maple strips run along v (the court's long axis when UVs are (x, z)). Each strip is cut into 1-3 boards of
        /// staggered length (wrapping across the tile edge so the pattern tiles); every board gets its own tone, grain
        /// phase, crown and occasional mineral streak. Hairline gaps between strips and at butt joints are recessed and
        /// darker; the lacquer gives a high, gently varying smoothness.
        /// </summary>
        private static void FillWood(Canvas cv, bool stained)
        {
            const int strips = WoodStripsPerTile;
            const int maxJoints = 3;
            var joints = new float[strips * maxJoints];
            var jointCount = new int[strips];

            for (int s = 0; s < strips; s++)
            {
                int k = 1 + Mathf.Min(2, (int)(ArenaNoise.Hash01(s, 0, SeedWoodLayout) * 3f));
                float start = ArenaNoise.Hash01(s, 1, SeedWoodLayout);
                float total = 0f;
                var w = new float[k];
                for (int i = 0; i < k; i++)
                {
                    w[i] = 0.35f + ArenaNoise.Hash01(s, 2 + i, SeedWoodLayout);
                    total += w[i];
                }
                float acc = start;
                for (int i = 0; i < k; i++)
                {
                    joints[s * maxJoints + i] = acc - Mathf.Floor(acc);
                    acc += w[i] / total;
                }
                Array.Sort(joints, s * maxJoints, k);
                jointCount[s] = k;
            }

            int W = cv.W, H = cv.H;
            float tile = cv.TileY;
            float px = cv.PixelX;
            const int nv = 6;   // grain lattice cells along a tile (~30 cm streaks)

            // Lacquered maple palette (sRGB): honey sapwood -> amber -> occasional darker heart boards.
            var light = new Color(0.86f, 0.69f, 0.47f);
            var mid = new Color(0.80f, 0.60f, 0.38f);
            var dark = new Color(0.70f, 0.49f, 0.30f);
            var heart = new Color(0.60f, 0.41f, 0.24f);
            var mineral = new Color(0.50f, 0.44f, 0.36f);
            // Outfield stain: deep court blue, semi-transparent so grain still shows (like a real stained key).
            var stain = new Color(0.14f, 0.28f, 0.50f);
            const float stainOpacity = 0.82f;

            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float sf = u * strips;
                    int s = Mathf.Min(strips - 1, (int)sf);
                    float a = sf - s;   // 0..1 across the strip

                    // Board segment and distance to the nearest butt joint (tile units).
                    int k = jointCount[s];
                    int baseIdx = s * maxJoints;
                    int seg = 0;
                    float jointDist;
                    float j0 = joints[baseIdx], jLast = joints[baseIdx + k - 1];
                    if (v < j0 || v >= jLast)
                    {
                        seg = 0;
                        jointDist = Mathf.Min(CyclicDist(v, j0), CyclicDist(v, jLast));
                    }
                    else
                    {
                        seg = 1;
                        while (seg < k && v >= joints[baseIdx + seg]) seg++;
                        jointDist = Mathf.Min(v - joints[baseIdx + seg - 1], joints[baseIdx + seg] - v);
                    }

                    uint boardHash = ArenaNoise.Hash(s, seg, SeedWoodLayout ^ 0x1234u);
                    float tone = (boardHash & 0xFFFF) / 65535f;
                    float special = ((boardHash >> 16) & 0xFFFF) / 65535f;
                    float phase = ArenaNoise.Hash01(s, seg + 31, SeedWoodLayout);
                    tone = Mathf.Pow(tone, 1.25f);   // bias toward the lighter boards typical of first-grade maple

                    Color c = tone < 0.5f ? Color.Lerp(light, mid, tone * 2f) : Color.Lerp(mid, dark, (tone - 0.5f) * 2f);
                    if (special < 0.06f) c = Color.Lerp(c, heart, 0.65f);

                    // Grain: long low-contrast streaks + fine fibre noise + thin growth-ring lines + ray flecks.
                    float gx = a * 6f + s * 7.31f + seg * 3.7f;
                    float gy = v * nv + phase * nv;
                    float streak = ArenaNoise.Fbm(gx, gy, 1 << 16, nv, 3, 0.55f, SeedWoodGrain);
                    float fine = ArenaNoise.Value(a * 24f + s * 13.1f, v * nv * 10f, 1 << 16, nv * 10, SeedWoodFine);
                    float ringArg = (a * 1.6f + streak * 2.4f + phase * 3f) * (Mathf.PI * 2f * 1.4f);
                    float ringLine = ArenaNoise.SmoothStep(0.82f, 1f, Mathf.Abs(Mathf.Sin(ringArg)));
                    float fleckN = ArenaNoise.Value(a * 50f + s * 3.3f, v * nv * 60f, 1 << 16, nv * 60, SeedWoodFleck);
                    float fleck = ArenaNoise.SmoothStep(0.80f, 0.93f, fleckN);

                    float lum = 1f + 0.16f * (streak - 0.5f) + 0.06f * (fine - 0.5f) - 0.05f * ringLine - 0.07f * fleck;
                    float r = c.r * lum, g = c.g * lum, b = c.b * lum;

                    if (special >= 0.06f && special < 0.13f)
                    {
                        // Mineral streak: a narrow grey-brown band along part of the board.
                        float centre = 0.25f + 0.5f * phase;
                        float band = Mathf.Exp(-Sq((a - centre) / 0.12f));
                        float along = ArenaNoise.SmoothStep(0.35f, 0.7f,
                            ArenaNoise.Value(v * nv * 2f + phase * 17f, s * 1.7f, nv * 2, 1 << 16, SeedWoodMineral));
                        float m = 0.55f * band * along;
                        r = Lerp(r, mineral.r * lum, m);
                        g = Lerp(g, mineral.g * lum, m);
                        b = Lerp(b, mineral.b * lum, m);
                    }

                    if (stained)
                    {
                        float stainLum = lum * (0.92f + 0.16f * (1f - tone));
                        r = Lerp(r, stain.r * stainLum, stainOpacity);
                        g = Lerp(g, stain.g * stainLum, stainOpacity);
                        b = Lerp(b, stain.b * stainLum, stainOpacity);
                    }

                    // Hairline gaps: side seams between strips and butt joints between boards (0.8 / 0.6 mm wide).
                    float sideDist = Mathf.Min(a, 1f - a) * WoodStripWidth;
                    float endDist = jointDist * tile;
                    float sideGap = 1f - ArenaNoise.SmoothStep(0.0004f, 0.0004f + px, sideDist);
                    float endGap = 1f - ArenaNoise.SmoothStep(0.0003f, 0.0003f + px, endDist);
                    float gap = Mathf.Max(sideGap, endGap);
                    float nearGap = 1f - ArenaNoise.SmoothStep(0f, 0.0025f, Mathf.Min(sideDist, endDist));
                    float darken = (1f - 0.5f * gap) * (1f - 0.07f * nearGap);
                    r *= darken; g *= darken; b *= darken;

                    // Height (m): slight crown per strip, board-to-board lippage, lacquered fibre relief, recessed gaps.
                    float crown = 0.00008f * 4f * a * (1f - a);
                    float lippage = (((boardHash >> 8) & 0xFF) / 255f - 0.5f) * 0.00006f;
                    float height = crown + lippage + (fine - 0.5f) * 0.000015f - 0.0005f * gap;

                    float wear = ArenaNoise.Fbm(u * 3f, v * 3f, 3, 3, 3, 0.5f, SeedWoodWear);
                    float smooth = 0.80f + 0.06f * (wear - 0.5f) + 0.02f * (fine - 0.5f) - 0.30f * gap - 0.04f * nearGap;
                    float ao = 1f - 0.5f * gap - 0.08f * nearGap;

                    cv.Put(y * W + x, r, g, b, height, smooth, ao, 0f);
                }
            });
        }

        private static float CyclicDist(float a, float b)
        {
            float d = Mathf.Abs(a - b);
            return Mathf.Min(d, 1f - d);
        }

        private static float Sq(float v) => v * v;

        // =============================================================================================================
        // Line paint
        // =============================================================================================================

        private const uint SeedPaint = 0xA1170001u;

        /// <summary>Game-line paint sealed under the floor lacquer: near white, faint roller stipple, lacquer gloss.</summary>
        private static void FillLinePaint(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float streak = ArenaNoise.Fbm(u * 8f, v * 8f, 8, 8, 4, 0.5f, SeedPaint);
                    float roller = ArenaNoise.Value(u * 120f, v * 120f, 120, 120, SeedPaint + 7u);
                    float lum = 1f + 0.03f * (streak - 0.5f) + 0.02f * (roller - 0.5f);
                    cv.Put(y * W + x, 0.93f * lum, 0.93f * lum, 0.91f * lum,
                        0.00002f * roller + 0.00001f * streak,
                        0.76f + 0.04f * (streak - 0.5f),
                        1f, 0f);
                }
            });
        }

        // =============================================================================================================
        // Sports vinyl run-off
        // =============================================================================================================

        private const uint SeedVinyl = 0xB1A70001u;

        /// <summary>Point-elastic sports vinyl: fine embossed pebble texture (~4 mm), coloured chips, satin finish.</summary>
        private static void FillVinyl(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            const int pebbles = 128;   // cells per 0.5 m tile -> 3.9 mm pebbles
            const int chips = 96;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float d = ArenaNoise.Cellular(u * pebbles, v * pebbles, pebbles, pebbles, SeedVinyl, 0.9f, out uint _);
                    float dome = 1f - ArenaNoise.SmoothStep(0f, 0.75f, d);

                    float cd = ArenaNoise.Cellular(u * chips, v * chips, chips, chips, SeedVinyl + 3u, 1f, out uint chipHash);
                    float chipShape = 1f - ArenaNoise.SmoothStep(0.18f, 0.30f, cd);
                    uint chipKind = chipHash & 0xFFu;

                    float mottle = ArenaNoise.Fbm(u * 4f, v * 4f, 4, 4, 3, 0.5f, SeedVinyl + 11u);
                    float lum = 1f + 0.12f * (mottle - 0.5f) + 0.06f * (dome - 0.5f);
                    float r = 0.20f * lum, g = 0.22f * lum, b = 0.26f * lum;
                    if (chipKind < 14u)
                    {
                        r = Lerp(r, 0.52f, chipShape); g = Lerp(g, 0.55f, chipShape); b = Lerp(b, 0.60f, chipShape);
                    }
                    else if (chipKind < 30u)
                    {
                        r = Lerp(r, 0.07f, chipShape); g = Lerp(g, 0.075f, chipShape); b = Lerp(b, 0.085f, chipShape);
                    }

                    cv.Put(y * W + x, r, g, b,
                        0.00012f * dome,
                        0.30f + 0.12f * dome,
                        0.82f + 0.18f * dome, 0f);
                }
            });
        }

        // =============================================================================================================
        // Painted concrete block wall
        // =============================================================================================================

        private const uint SeedBlock = 0xC0B10001u;

        /// <summary>
        /// Painted concrete masonry units (390 x 190 mm faces, 10 mm concave-tooled mortar joints) in running bond. Paint
        /// evens the colour but the porous block face and the joints stay visible in the normals, AO and gloss.
        /// </summary>
        private static void FillBlockWall(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            const float blockW = 0.4f, blockH = 0.2f, halfJoint = 0.005f;
            float tileX = cv.TileX, tileY = cv.TileY;
            int blocksPerRow = Mathf.RoundToInt(tileX / blockW);
            float px = Mathf.Max(cv.PixelX, cv.PixelY);
            var paint = new Color(0.70f, 0.69f, 0.66f);

            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                float ym = v * tileY;
                int course = (int)(ym / blockH);
                float by = ym - course * blockH;
                float offset = (course & 1) == 1 ? blockW * 0.5f : 0f;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float xm = u * tileX + offset;
                    int blockX = (int)(xm / blockW);
                    float bx = xm - blockX * blockW;
                    blockX %= blocksPerRow;

                    float dj = Mathf.Min(Mathf.Min(bx, blockW - bx), Mathf.Min(by, blockH - by));
                    float joint = 1f - ArenaNoise.SmoothStep(halfJoint - px * 0.5f, halfJoint + px * 0.5f, dj);
                    // Concave tooled joint (~4 mm deep at the centre) and a small rounded arris on the block edge.
                    float t = Mathf.Clamp01(dj / halfJoint);
                    float jointDepth = -0.004f * (1f - t * t);
                    float arris = -0.0012f * (1f - ArenaNoise.SmoothStep(halfJoint, halfJoint + 0.004f, dj));

                    uint bh = ArenaNoise.Hash(blockX, course % Mathf.RoundToInt(tileY / blockH), SeedBlock);
                    float blockTone = 1f + (((bh & 0xFF) / 255f) - 0.5f) * 0.05f;

                    float pore = ArenaNoise.SmoothStep(0.70f, 0.88f, ArenaNoise.Value(u * 256f, v * 256f, 256, 256, SeedBlock + 5u));
                    float face = ArenaNoise.Fbm(u * 64f, v * 64f, 64, 64, 3, 0.5f, SeedBlock + 9u);
                    float grime = ArenaNoise.Fbm(u * 2f, v * 2f, 2, 2, 3, 0.55f, SeedBlock + 13u);

                    float lum = blockTone * (1f - 0.12f * pore) * (1f + 0.05f * (face - 0.5f)) * (1f - 0.05f * grime) * (1f - 0.10f * joint);
                    float height = joint > 0f ? Mathf.Lerp(arris, jointDepth, joint) : arris;
                    height += -0.0008f * pore + 0.0003f * (face - 0.5f);

                    cv.Put(y * W + x, paint.r * lum, paint.g * lum, paint.b * lum, height,
                        0.32f - 0.12f * joint - 0.15f * pore + 0.03f * (face - 0.5f),
                        1f - 0.40f * joint - 0.25f * pore, 0f);
                }
            });
        }

        // =============================================================================================================
        // Wall padding
        // =============================================================================================================

        private const uint SeedPad = 0x9ADD0001u;

        /// <summary>
        /// Vinyl-covered foam wall pads (2 x 6 ft panels): pillowed edges that roll off into 5 mm seams, soft creases near
        /// the edges, a leather-like vinyl grain and a semi-gloss finish.
        /// </summary>
        private static void FillPadding(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            float tileX = cv.TileX, tileY = cv.TileY;
            float px = Mathf.Max(cv.PixelX, cv.PixelY);
            var vinyl = new Color(0.10f, 0.15f, 0.30f);   // navy vinyl

            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                float ym = v * tileY;
                float ey = Mathf.Min(ym, tileY - ym);
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float xm = u * tileX;
                    int panel = (int)(xm / PadPanelWidth);
                    float xp = xm - panel * PadPanelWidth;
                    float ex = Mathf.Min(xp, PadPanelWidth - xp);
                    float e = Mathf.Min(ex, ey);

                    float seam = 1f - ArenaNoise.SmoothStep(0.0025f, 0.0025f + px, e);
                    float pillow = ArenaNoise.SmoothStep(0f, 0.035f, e);
                    float edgeZone = 1f - ArenaNoise.SmoothStep(0.02f, 0.14f, e);
                    float crease = ArenaNoise.Value(u * 48f + panel * 3.1f, v * 8f, 48, 8, SeedPad) - 0.5f;
                    float grain = ArenaNoise.Value(u * 300f, v * 450f, 300, 450, SeedPad + 3u);
                    float fade = ArenaNoise.Fbm(u * 2f, v * 2f, 2, 2, 3, 0.5f, SeedPad + 7u);

                    float panelTone = 1f + (ArenaNoise.Hash01(panel, 0, SeedPad + 11u) - 0.5f) * 0.06f;
                    float lum = panelTone * (1f + 0.06f * (fade - 0.5f) + 0.04f * (grain - 0.5f)) * (1f - 0.55f * seam) * (0.88f + 0.12f * pillow);

                    float height = 0.012f * pillow + 0.0008f * crease * edgeZone + 0.00004f * grain;
                    cv.Put(y * W + x, vinyl.r * lum, vinyl.g * lum, vinyl.b * lum, height,
                        0.50f + 0.06f * (grain - 0.5f) - 0.30f * seam,
                        1f - 0.50f * seam - 0.12f * (1f - pillow), 0f);
                }
            });
        }

        // =============================================================================================================
        // Bleacher concrete
        // =============================================================================================================

        private const uint SeedConcrete = 0xC0C00001u;

        /// <summary>Sealed cast concrete: blotchy cement tone, fine aggregate, occasional bug holes, low gloss.</summary>
        private static void FillConcrete(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            const int holes = 120;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float blotch = ArenaNoise.Fbm(u * 4f, v * 4f, 4, 4, 5, 0.55f, SeedConcrete);
                    float aggregate = ArenaNoise.Value(u * 200f, v * 200f, 200, 200, SeedConcrete + 3u);
                    float fine = ArenaNoise.Fbm(u * 64f, v * 64f, 64, 64, 3, 0.5f, SeedConcrete + 5u);

                    float hd = ArenaNoise.Cellular(u * holes, v * holes, holes, holes, SeedConcrete + 9u, 1f, out uint holeHash);
                    float hole = (holeHash & 0xFFu) < 30u ? 1f - ArenaNoise.SmoothStep(0.12f, 0.24f, hd) : 0f;

                    float lum = (1f + 0.20f * (blotch - 0.5f)) * (1f + 0.10f * (aggregate - 0.5f)) * (1f - 0.35f * hole);
                    cv.Put(y * W + x, 0.55f * lum, 0.55f * lum, 0.53f * lum,
                        0.0004f * (fine - 0.5f) + 0.0002f * aggregate - 0.0015f * hole,
                        0.22f + 0.06f * (blotch - 0.5f) - 0.12f * hole,
                        1f - 0.40f * hole - 0.06f * (1f - fine), 0f);
                }
            });
        }

        // =============================================================================================================
        // Moulded plastic seats
        // =============================================================================================================

        private const uint SeedPlastic = 0x5EA70001u;

        /// <summary>Injection-moulded polypropylene: deep red, fine mould stipple, satin gloss.</summary>
        private static void FillPlastic(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float stipple = ArenaNoise.Value(u * 100f, v * 100f, 100, 100, SeedPlastic);
                    float tone = ArenaNoise.Fbm(u * 3f, v * 3f, 3, 3, 3, 0.5f, SeedPlastic + 3u);
                    float lum = 1f + 0.05f * (tone - 0.5f) + 0.03f * (stipple - 0.5f);
                    cv.Put(y * W + x, 0.46f * lum, 0.07f * lum, 0.08f * lum,
                        0.00003f * stipple,
                        0.55f + 0.08f * (tone - 0.5f) - 0.05f * stipple,
                        1f, 0f);
                }
            });
        }

        // =============================================================================================================
        // Brushed steel
        // =============================================================================================================

        private const uint SeedMetal = 0x3E7A0001u;

        /// <summary>Brushed steel: fine directional scratches along u, handling smudges, metallic = 1.</summary>
        private static void FillBrushedMetal(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float streak = ArenaNoise.Fbm(u * 3f, v * 300f, 3, 300, 3, 0.6f, SeedMetal);
                    float smudge = ArenaNoise.Fbm(u * 3f, v * 3f, 3, 3, 4, 0.5f, SeedMetal + 3u);
                    float lum = (1f + 0.07f * (streak - 0.5f)) * (1f - 0.05f * smudge);
                    // Steel reflectance (F0) ~0.56 linear = ~0.77 sRGB.
                    cv.Put(y * W + x, 0.77f * lum, 0.78f * lum, 0.79f * lum,
                        0.000006f * streak,
                        0.62f + 0.16f * (streak - 0.5f) - 0.10f * (smudge - 0.5f),
                        1f, 1f);
                }
            });
        }

        // =============================================================================================================
        // LED floodlight lens
        // =============================================================================================================

        /// <summary>6 x 6 LED optics (100 mm pitch) behind a glass cover; also used as the emission map.</summary>
        private static void FillLedArray(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            const int cells = 6;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float cx = u * cells, cy = v * cells;
                    float fx = cx - (int)cx - 0.5f, fy = cy - (int)cy - 0.5f;
                    float r = Mathf.Sqrt(fx * fx + fy * fy);   // cell units (0.1 m)
                    float emitter = 1f - ArenaNoise.SmoothStep(0.14f, 0.20f, r);
                    float optic = 1f - ArenaNoise.SmoothStep(0.30f, 0.42f, r);
                    float lum = 0.20f + 0.40f * optic + 0.40f * emitter;
                    float warm = Lerp(1f, 0.96f, emitter);
                    cv.Put(y * W + x, lum, lum * warm, lum * warm * warm,
                        0.004f * Mathf.Sqrt(Mathf.Max(0f, 1f - Sq(r / 0.42f))),
                        0.92f,
                        1f - 0.2f * (1f - optic), 0f);
                }
            });
        }

        // =============================================================================================================
        // Ceiling (painted trapezoidal steel deck)
        // =============================================================================================================

        private const uint SeedDeck = 0xDEC00001u;

        /// <summary>Dark-painted trapezoidal roof deck (300 mm rib pitch, 45 mm deep) as seen from below.</summary>
        private static void FillSteelDeck(Canvas cv)
        {
            int W = cv.W, H = cv.H;
            float tileX = cv.TileX;
            const float pitch = 0.3f;
            ParallelRows(H, y =>
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float xm = u * tileX;
                    float p = xm - Mathf.Floor(xm / pitch) * pitch;
                    float profile;
                    if (p < 0.08f) profile = 1f;
                    else if (p < 0.12f) profile = 1f - ArenaNoise.SmoothStep(0.08f, 0.12f, p);
                    else if (p < 0.26f) profile = 0f;
                    else profile = ArenaNoise.SmoothStep(0.26f, 0.30f, p);
                    float bead = Mathf.Exp(-Sq((p - 0.19f) / 0.004f));

                    float mottle = ArenaNoise.Fbm(u * 4f, v * 4f, 4, 4, 4, 0.5f, SeedDeck);
                    float lum = (1f + 0.10f * (mottle - 0.5f)) * (0.85f + 0.15f * profile);
                    cv.Put(y * W + x, 0.16f * lum, 0.16f * lum, 0.17f * lum,
                        0.045f * profile + 0.004f * bead,
                        0.35f + 0.05f * (mottle - 0.5f),
                        0.65f + 0.35f * profile, 0f);
                }
            });
        }
    }
}
