using System.Collections.Generic;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning for one Frost Trail throw, copied into every <see cref="ElsaIceTrailSegment"/> it lays.</summary>
    public struct ElsaIceTrailSettings
    {
        /// <summary>Distance (m) between two consecutive patches along the ground track of the ball.</summary>
        public float Spacing;
        /// <summary>Patch width (m) across the direction of travel.</summary>
        public float Width;
        /// <summary>Seconds a patch lasts (including its short melt at the end).</summary>
        public float Lifetime;
        /// <summary>Haste magnitude given to allies standing on the ice (0.2 = +20% movement speed).</summary>
        public float HasteMagnitude;
        /// <summary>Duration of each Haste refresh (s). Allies keep the buffer this long after stepping off.</summary>
        public float HasteRefresh;
        /// <summary>Extra planar margin (m) around the patch that still counts as "standing on it" (foot size).</summary>
        public float FootMargin;
        /// <summary>Whether the thrower (Elsa) also benefits from her own trail.</summary>
        public bool IncludeCaster;
        /// <summary>Upper bound of patches one throw may lay (performance guard).</summary>
        public int MaxSegmentsPerThrow;

        /// <summary>Spec defaults: 0.9 m spacing/width, 3 s lifetime, +20% haste.</summary>
        public static ElsaIceTrailSettings Default => new ElsaIceTrailSettings
        {
            Spacing = 0.9f,
            Width = 0.9f,
            Lifetime = 3f,
            HasteMagnitude = 0.2f,
            HasteRefresh = 0.35f,
            FootMargin = 0.15f,
            IncludeCaster = true,
            MaxSegmentsPerThrow = 48,
        };
    }

    /// <summary>
    /// One patch of Elsa's [Frost Trail]: a thin sheet of frosted ice on the court floor laid under a thrown ball's
    /// trajectory. While it exists, ALLIES of the thrower whose feet are on it receive <see cref="StatusEffectType.Haste"/>
    /// (refreshed continuously while inside, so the buff ends shortly after stepping off).
    /// <para>
    /// Look: an organically edged glossy ice sheet (opaque HDRP Lit, frost albedo texture, smoothness 0.8) sitting 4 mm
    /// above the floor, feathered into the floor by a soft cold haze. It "forms" with a quick 0.1 s growth and melts by
    /// shrinking over the last 0.6 s - no alpha tricks needed on the opaque surface.
    /// </para>
    /// <para>
    /// Segments are pooled (static pool, allocation-free after warm-up) and share one mesh/material. The haste check runs
    /// at 10 Hz per segment with staggered phases, against <see cref="PlayerRegistry.All"/> (no physics queries).
    /// All segments use one shared status <see cref="HasteSource"/> so overlapping patches never stack beyond +20%.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElsaIceTrailSegment : MonoBehaviour
    {
        // ------------------------------------------------------------------ static pool

        private const float SurfaceOffset = 0.004f;   // m above the floor (avoids z-fighting with court lines)
        private const float HazeOffset = 0.002f;
        private const float HazeScale = 1.35f;        // haze extends 35% beyond the ice for a soft rim
        private const float FormTime = 0.1f;          // s to grow in
        private const float MeltTime = 0.6f;          // s to shrink away at the end
        private const float CheckInterval = 0.1f;     // s between ally checks (10 Hz)
        private const float MaxStandingHeight = 0.35f; // m: feet must be this close to the ice to count as standing

        private static readonly Stack<ElsaIceTrailSegment> s_pool = new Stack<ElsaIceTrailSegment>(64);
        private static readonly List<ElsaIceTrailSegment> s_active = new List<ElsaIceTrailSegment>(64);
        private static Transform s_root;
        private static int s_spawnCounter;

        /// <summary>Shared status source so every Frost Trail patch refreshes one single Haste stack per player.</summary>
        public static readonly object HasteSource = new object();

        /// <summary>Currently visible patches (diagnostics / tests).</summary>
        public static int ActiveCount => s_active.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_pool.Clear();
            s_active.Clear();
            s_root = null;
            s_spawnCounter = 0;
        }

        // ------------------------------------------------------------------ instance state

        private Transform _ice;
        private Transform _haze;
        private DodgeballPlayer _owner;
        private TeamId _team;
        private ElsaIceTrailSettings _settings;
        private Vector3 _center;
        private Vector3 _forward;
        private Vector3 _right;
        private float _length;
        private float _age;
        private float _nextCheck;
        private float _yawJitter;
        private float _widthJitter;
        private bool _live;

        /// <summary>Player whose throw laid this patch.</summary>
        public DodgeballPlayer Owner => _owner;

        /// <summary>Team whose players are hasted by this patch.</summary>
        public TeamId Team => _team;

        /// <summary>0..1 fraction of the lifetime elapsed.</summary>
        public float NormalizedAge => _settings.Lifetime > 0f ? Mathf.Clamp01(_age / _settings.Lifetime) : 1f;

        // ------------------------------------------------------------------ API

        /// <summary>
        /// Lays a patch centred on <paramref name="groundPoint"/> (a point ON the floor), elongated along
        /// <paramref name="travelDirection"/> (planar). Returns the pooled segment.
        /// </summary>
        public static ElsaIceTrailSegment Spawn(Vector3 groundPoint, Vector3 travelDirection, DodgeballPlayer owner,
            in ElsaIceTrailSettings settings)
        {
            ElsaIceTrailSegment segment = null;
            while (s_pool.Count > 0 && segment == null) segment = s_pool.Pop(); // skips entries destroyed by scene unloads
            if (segment == null) segment = Create();

            segment.Begin(groundPoint, travelDirection, owner, settings);
            s_active.Add(segment);
            return segment;
        }

        /// <summary>Removes every patch immediately (round reset).</summary>
        public static void ClearAll()
        {
            for (int i = s_active.Count - 1; i >= 0; i--)
            {
                var s = s_active[i];
                if (s != null) s.Release();
                else s_active.RemoveAt(i);
            }
        }

        /// <summary>Removes the patches laid by <paramref name="owner"/> (hero unequipped / left the match).</summary>
        public static void ClearOwnedBy(DodgeballPlayer owner)
        {
            for (int i = s_active.Count - 1; i >= 0; i--)
            {
                var s = s_active[i];
                if (s == null) s_active.RemoveAt(i);
                else if (s._owner == owner) s.Release();
            }
        }

        /// <summary>True when <paramref name="worldPosition"/> (feet) is on this patch's current footprint.</summary>
        public bool Contains(Vector3 worldPosition)
        {
            if (!_live) return false;
            Vector3 d = worldPosition - _center;
            if (Mathf.Abs(d.y) > MaxStandingHeight) return false;

            float scale = CurrentScale();
            float halfWidth = 0.5f * _settings.Width * _widthJitter * scale + _settings.FootMargin;
            float halfLength = 0.5f * _length * scale + _settings.FootMargin;
            float lx = Vector3.Dot(d, _right);
            float lz = Vector3.Dot(d, _forward);
            // Elliptical footprint matches the oval mesh better than a rectangle.
            float ex = lx / Mathf.Max(0.01f, halfWidth);
            float ez = lz / Mathf.Max(0.01f, halfLength);
            return ex * ex + ez * ez <= 1f;
        }

        // ------------------------------------------------------------------ lifecycle

        private static ElsaIceTrailSegment Create()
        {
            if (s_root == null)
            {
                var rootGo = new GameObject("DU_ElsaIceTrails");
                s_root = rootGo.transform;
            }

            var go = new GameObject("ElsaIceTrailSegment");
            go.layer = GameLayers.Visual;
            go.transform.SetParent(s_root, false);
            var segment = go.AddComponent<ElsaIceTrailSegment>();

            segment._haze = CreateLayer(go.transform, "FrostHaze", ElsaFrostAssets.QuadMesh, ElsaFrostAssets.HazeMaterial);
            segment._ice = CreateLayer(go.transform, "Ice", ElsaFrostAssets.PatchMesh, ElsaFrostAssets.IceMaterial);

            go.SetActive(false);
            return segment;
        }

        private static Transform CreateLayer(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.layer = GameLayers.Visual;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off; // a 4 mm sheet casts no visible shadow
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes; // glossy ice should reflect the arena
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go.transform;
        }

        private void Begin(Vector3 groundPoint, Vector3 travelDirection, DodgeballPlayer owner, in ElsaIceTrailSettings settings)
        {
            _owner = owner;
            _team = owner != null ? owner.Team : TeamId.None;
            _settings = settings;
            _settings.Spacing = Mathf.Max(0.1f, _settings.Spacing);
            _settings.Width = Mathf.Max(0.1f, _settings.Width);
            _settings.Lifetime = Mathf.Max(0.2f, _settings.Lifetime);
            // Patches slightly longer than the spacing so consecutive ones overlap into a continuous band.
            _length = _settings.Spacing * 1.3f;

            travelDirection.y = 0f;
            _forward = travelDirection.sqrMagnitude > 1e-6f ? travelDirection.normalized : Vector3.forward;

            // Deterministic per-spawn variation (no UnityEngine.Random state disturbance).
            s_spawnCounter++;
            float h1 = Hash01(s_spawnCounter * 12.9898f);
            float h2 = Hash01(s_spawnCounter * 78.233f);
            _yawJitter = (h1 - 0.5f) * 16f;          // +-8 degrees
            _widthJitter = 0.9f + h2 * 0.2f;         // 0.9..1.1

            var rot = Quaternion.LookRotation(_forward, Vector3.up) * Quaternion.Euler(0f, _yawJitter, 0f);
            _forward = rot * Vector3.forward;
            _right = rot * Vector3.right;
            _center = groundPoint;

            transform.SetPositionAndRotation(groundPoint, rot);
            _ice.localPosition = new Vector3(0f, SurfaceOffset, 0f);
            _haze.localPosition = new Vector3(0f, HazeOffset, 0f);

            _age = 0f;
            // Stagger checks between segments so they do not all run on the same frame.
            _nextCheck = Hash01(s_spawnCounter * 3.17f) * CheckInterval;
            _live = true;
            ApplyScale(CurrentScale());
            gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!_live) return;

            float dt = Time.deltaTime; // scaled: hitstop pauses melting too
            _age += dt;
            if (_age >= _settings.Lifetime)
            {
                Release();
                return;
            }

            ApplyScale(CurrentScale());

            _nextCheck -= dt;
            if (_nextCheck <= 0f)
            {
                _nextCheck += CheckInterval;
                HasteAlliesOnIce();
            }
        }

        private void OnDestroy()
        {
            s_active.Remove(this);
        }

        private void Release()
        {
            _live = false;
            _owner = null;
            s_active.Remove(this);
            if (this == null) return;
            gameObject.SetActive(false);
            s_pool.Push(this);
        }

        // ------------------------------------------------------------------ behaviour

        /// <summary>Refreshes Haste on every ally whose feet are on the ice.</summary>
        private void HasteAlliesOnIce()
        {
            if (_settings.HasteMagnitude <= 0f || !_team.IsValid()) return;

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsInitialized || p.Team != _team || p.Status == null) continue;
                if (!_settings.IncludeCaster && p == _owner) continue;
                if (!Contains(p.Position)) continue;

                // Same (type, source) refreshes duration and keeps the larger magnitude -> never stacks.
                p.Status.Apply(StatusEffectType.Haste, _settings.HasteRefresh, _settings.HasteMagnitude, HasteSource);
            }
        }

        /// <summary>0..1 size factor: quick growth when forming, shrink while melting.</summary>
        private float CurrentScale()
        {
            float grow = Mathf.Clamp01(_age / FormTime);
            grow = 1f - (1f - grow) * (1f - grow); // ease-out
            float remaining = _settings.Lifetime - _age;
            float melt = Mathf.Clamp01(remaining / MeltTime);
            melt = melt * melt * (3f - 2f * melt); // smoothstep
            return Mathf.Max(0.001f, Mathf.Lerp(0.6f, 1f, grow) * melt);
        }

        private void ApplyScale(float scale)
        {
            float w = _settings.Width * _widthJitter * scale;
            float l = _length * Mathf.Lerp(0.85f, 1f, scale) * scale;
            _ice.localScale = new Vector3(w, 1f, l);
            _haze.localScale = new Vector3(w * HazeScale, 1f, l * HazeScale);
        }

        private static float Hash01(float x)
        {
            float s = Mathf.Sin(x) * 43758.5453f;
            return s - Mathf.Floor(s);
        }
    }
}
