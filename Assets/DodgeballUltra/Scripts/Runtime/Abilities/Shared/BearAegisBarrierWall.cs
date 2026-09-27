using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using DodgeballUltra.Rendering;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning of one <see cref="BearAegisBarrierWall"/> (copied from <see cref="BearAegisBarrier"/>).</summary>
    [Serializable]
    public struct BearAegisBarrierSettings
    {
        /// <summary>Wall width (m) along the centre line.</summary>
        public float Width;
        /// <summary>Wall height (m).</summary>
        public float Height;
        /// <summary>Thickness (m) of the blocking collider.</summary>
        public float Thickness;
        /// <summary>Seconds the wall blocks before it starts fading (the ultimate's duration).</summary>
        public float Lifetime;
        /// <summary>Seconds the energy sheet takes to rise from the emitter rail.</summary>
        public float GrowTime;
        /// <summary>Seconds the sheet takes to flicker out.</summary>
        public float FadeTime;
        /// <summary>Energy colour (rgb) and base opacity (a).</summary>
        public Color EnergyTint;
        /// <summary>Overall brightness multiplier of the energy sheet.</summary>
        public float Intensity;
        /// <summary>Hexagon circumradius (m) of the lattice.</summary>
        public float HexSize;
        /// <summary>Upward drift of the lattice (m/s): energy "flowing" out of the emitter.</summary>
        public float ScrollSpeed;
        /// <summary>Lifetime (s) of an impact ripple.</summary>
        public float RippleDuration;
        /// <summary>Radius (m) an impact ripple expands to.</summary>
        public float RippleMaxRadius;
        /// <summary>Peak out-of-plane displacement (m) of the sheet under a ripple.</summary>
        public float RippleAmplitude;
        /// <summary>Extra brightness on impact (1 = doubles for an instant).</summary>
        public float HitFlashBoost;
        /// <summary>HDR colour of the emitter light strips (the physical projector hardware).</summary>
        public Color EmitterTint;

        /// <summary>Defaults: 10 x 3.2 m, 6 s, pale cyan-blue energy.</summary>
        public static BearAegisBarrierSettings Default => new BearAegisBarrierSettings
        {
            Width = 10f,
            Height = 3.2f,
            Thickness = 0.35f,
            Lifetime = 6f,
            GrowTime = 0.35f,
            FadeTime = 0.45f,
            EnergyTint = new Color(0.42f, 0.78f, 1f, 0.26f),
            Intensity = 1.2f,
            HexSize = 0.22f,
            ScrollSpeed = 0.12f,
            RippleDuration = 0.55f,
            RippleMaxRadius = 1.6f,
            RippleAmplitude = 0.05f,
            HitFlashBoost = 0.9f,
            EmitterTint = new Color(0.5f, 0.85f, 1f, 1f) * 3.5f,
        };
    }

    /// <summary>
    /// Bear's [Aegis Barrier]: a large energy wall standing at centre court on Bear's side that blocks every opponent throw.
    /// <para>
    /// Gameplay: a <see cref="BoxCollider"/> on <see cref="GameLayers.Hittable"/> implementing <see cref="IBallHittable"/>.
    /// Live balls thrown by the enemy team get <see cref="BallHitResponse.Block"/> (the ball reflects and publishes
    /// <see cref="BallBlockedEvent"/> itself); balls of Bear's team pass straight through; unblockable balls (Rayne's beam)
    /// are left to the ball, which ignores blocks - the wall only ripples to show it was pierced.
    /// Players are never blocked (Hittable does not collide with players).
    /// </para>
    /// <para>
    /// Look (projected energy, not a cartoon force field): a physical emitter rail and two end pylons in dark machined
    /// metal with thin HDR light strips, and a translucent additive sheet showing a fine hexagonal lattice that drifts
    /// upward, brightest at the emitter (glow ramp) with a crisp top edge. Impacts spawn expanding ripple rings, a
    /// travelling bulge in the subdivided sheet and a brightness flicker; the sheet rises out of the rail on deploy,
    /// stutters during its last second and flickers out when it expires. Uses scaled time (freezes during hitstop).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BearAegisBarrierWall : MonoBehaviour, IBallHittable
    {
        private const int MaxRipples = 6;

        private enum Phase { Growing, Holding, Fading }

        private struct Ripple
        {
            public bool Active;
            public Vector2 Centre;   // local XY on the sheet
            public float Age;
            public float Strength;
            public Transform Ring;
            public Material RingMaterial;
        }

        private readonly List<Material> _materials = new List<Material>(16);
        private readonly List<Mesh> _meshes = new List<Mesh>(4);
        private readonly Ripple[] _ripples = new Ripple[MaxRipples];

        private DodgeballPlayer _owner;
        private TeamId _team = TeamId.None;
        private BearAegisBarrierSettings _settings;
        private BoxCollider _collider;
        private Transform _sheetRoot;
        private Material _sheetMaterial;
        private Material _glowMaterial;
        private Material _edgeMaterial;
        private Material _stripMaterial;
        private Mesh _sheetMesh;
        private Vector3[] _baseVertices;
        private Vector3[] _workVertices;
        private bool _meshDisplaced;
        private Phase _phase;
        private float _age;
        private float _lifeRemaining;
        private float _fadeTime;
        private float _hitFlash;
        private float _noiseSeed;
        private bool _dismissed;

        private Action<RoundEndedEvent> _onRoundEnded;
        private Action<RoundStartedEvent> _onRoundStarted;
        private Action<MatchEndedEvent> _onMatchEnded;

        /// <inheritdoc />
        public TeamId OwnerTeam => _team;

        /// <summary>The Bear who raised the barrier (may be null if he left the match).</summary>
        public DodgeballPlayer Owner => _owner;

        /// <summary>True while the wall stops enemy balls.</summary>
        public bool IsBlocking => !_dismissed && _phase != Phase.Fading;

        /// <summary>Enemy throws stopped so far.</summary>
        public int BallsBlocked { get; private set; }

        /// <summary>Seconds of blocking left.</summary>
        public float RemainingLifetime => IsBlocking ? Mathf.Max(0f, _lifeRemaining) : 0f;

        // ------------------------------------------------------------------ creation

        /// <summary>
        /// Raises a barrier whose bottom-centre is <paramref name="floorCentre"/>; its local +Z (<paramref name="rotation"/>)
        /// points toward the enemy half.
        /// </summary>
        public static BearAegisBarrierWall Create(DodgeballPlayer owner, Vector3 floorCentre, Quaternion rotation,
            in BearAegisBarrierSettings settings)
        {
            var go = new GameObject(owner != null ? $"AegisBarrier_{owner.PlayerId}" : "AegisBarrier");
            go.layer = GameLayers.Hittable;
            go.transform.SetPositionAndRotation(floorCentre, rotation);
            var wall = go.AddComponent<BearAegisBarrierWall>();
            wall.Build(owner, settings);
            return wall;
        }

        private void Awake()
        {
            _onRoundEnded = _ => Dismiss(true);
            _onRoundStarted = _ => Dismiss(true);
            _onMatchEnded = _ => Dismiss(true);
        }

        private void OnEnable()
        {
            GameEvents.Subscribe(_onRoundEnded);
            GameEvents.Subscribe(_onRoundStarted);
            GameEvents.Subscribe(_onMatchEnded);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe(_onRoundEnded);
            GameEvents.Unsubscribe(_onRoundStarted);
            GameEvents.Unsubscribe(_onMatchEnded);
        }

        private void Build(DodgeballPlayer owner, in BearAegisBarrierSettings s)
        {
            _owner = owner;
            _team = owner != null ? owner.Team : TeamId.None;
            _settings = s;
            _settings.Width = Mathf.Max(1f, s.Width);
            _settings.Height = Mathf.Max(0.5f, s.Height);
            _settings.GrowTime = Mathf.Max(0.01f, s.GrowTime);
            _settings.FadeTime = Mathf.Max(0.01f, s.FadeTime);
            _settings.RippleDuration = Mathf.Max(0.05f, s.RippleDuration);
            _lifeRemaining = Mathf.Max(0.1f, s.Lifetime);
            _noiseSeed = UnityEngine.Random.value * 100f;
            _phase = Phase.Growing;

            float w = _settings.Width, h = _settings.Height;

            // ---- gameplay collider (Hittable)
            _collider = gameObject.AddComponent<BoxCollider>();
            _collider.isTrigger = false; // ball sweeps usually ignore triggers
            _collider.center = new Vector3(0f, h * 0.5f, 0f);
            _collider.size = new Vector3(w, h, Mathf.Max(0.05f, s.Thickness));

            // ---- emitter hardware (physically based metal + thin light strips)
            var metal = Track(MaterialFactory.CreateLit("Aegis Emitter Metal", new Color(0.19f, 0.2f, 0.22f), 0.62f, 0.9f));
            _stripMaterial = Track(MaterialFactory.CreateUnlit("Aegis Emitter Strip", s.EmitterTint, false));
            ScrewsGadgetKit.CreatePart("EmitterRail", PrimitiveType.Cube, transform, new Vector3(0f, 0.04f, 0f), Quaternion.identity,
                new Vector3(w, 0.08f, 0.16f), metal);
            ScrewsGadgetKit.CreatePart("EmitterStrip", PrimitiveType.Cube, transform, new Vector3(0f, 0.083f, 0f), Quaternion.identity,
                new Vector3(w - 0.12f, 0.01f, 0.03f), _stripMaterial, false);
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (w * 0.5f + 0.05f);
                ScrewsGadgetKit.CreatePart(side < 0 ? "PylonLeft" : "PylonRight", PrimitiveType.Cube, transform,
                    new Vector3(x, (h + 0.12f) * 0.5f, 0f), Quaternion.identity, new Vector3(0.1f, h + 0.12f, 0.18f), metal);
                ScrewsGadgetKit.CreatePart("PylonStrip", PrimitiveType.Cube, transform,
                    new Vector3(x - side * 0.055f, h * 0.5f + 0.05f, 0f), Quaternion.identity, new Vector3(0.012f, h - 0.2f, 0.035f),
                    _stripMaterial, false);
            }

            // ---- energy sheet (rises out of the rail: scaled on Y by the grow animation)
            _sheetRoot = ScrewsGadgetKit.CreatePivot("EnergySheet", transform, new Vector3(0f, 0.09f, 0f), Quaternion.identity);
            _sheetRoot.localScale = new Vector3(1f, 0.001f, 1f);
            float sheetH = h - 0.09f;
            float hex = Mathf.Max(0.05f, s.HexSize);
            int segX = Mathf.Clamp(Mathf.CeilToInt(w / 0.25f), 4, 64);
            int segY = Mathf.Clamp(Mathf.CeilToInt(sheetH / 0.25f), 2, 24);
            _sheetMesh = BuildTrackedMesh(ScrewsGadgetKit.BuildSheet("AegisSheet", w, sheetH, segX, segY,
                ScrewsGadgetKit.HexPeriodX * hex, ScrewsGadgetKit.HexPeriodY * hex, out _baseVertices));
            _workVertices = (Vector3[])_baseVertices.Clone();
            _sheetMaterial = Track(MaterialFactory.CreateParticle("Aegis Energy", s.EnergyTint, true, ScrewsGadgetKit.HexLatticeTexture));
            ScrewsGadgetKit.CreateMeshPart("Lattice", _sheetMesh, _sheetRoot, Vector3.zero, Quaternion.identity, Vector3.one,
                _sheetMaterial, false);

            // Brighter band just above the emitter (ramp texture), and a crisp top edge.
            var glowMesh = BuildTrackedMesh(ScrewsGadgetKit.BuildSheet("AegisGlow", w, 0.9f, 1, 1, w, 0.9f, out _));
            _glowMaterial = Track(MaterialFactory.CreateParticle("Aegis Glow", s.EnergyTint, true, ScrewsGadgetKit.GlowRampTexture));
            ScrewsGadgetKit.CreateMeshPart("EmitterGlow", glowMesh, _sheetRoot, new Vector3(0f, 0f, 0.002f), Quaternion.identity,
                Vector3.one, _glowMaterial, false);
            _edgeMaterial = Track(MaterialFactory.CreateUnlit("Aegis Edge", s.EnergyTint, true));
            ScrewsGadgetKit.CreatePart("TopEdge", PrimitiveType.Cube, _sheetRoot, new Vector3(0f, sheetH - 0.01f, 0f),
                Quaternion.identity, new Vector3(w, 0.018f, 0.018f), _edgeMaterial, false);

            // ---- ripple ring pool (one material each so they fade independently)
            var ringMesh = BuildTrackedMesh(ScrewsGadgetKit.BuildDoubleSidedQuad("AegisRipple"));
            for (int i = 0; i < MaxRipples; i++)
            {
                var mat = Track(MaterialFactory.CreateParticle("Aegis Ripple", s.EnergyTint, true, ScrewsGadgetKit.RingTexture));
                var ring = ScrewsGadgetKit.CreateMeshPart("Ripple", ringMesh, transform, Vector3.zero, Quaternion.identity,
                    Vector3.one, mat, false);
                ring.gameObject.SetActive(false);
                _ripples[i] = new Ripple { Ring = ring, RingMaterial = mat };
            }

            ApplyVisuals(0f);
        }

        // ------------------------------------------------------------------ gameplay

        /// <inheritdoc />
        public BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit)
        {
            if (!IsBlocking || ball == null || !ball.IsLive) return BallHitResponse.PassThrough;
            if (!ScrewsGadgetKit.IsEnemyBall(_team, ball)) return BallHitResponse.PassThrough;

            Vector3 point = hit.point;
            if (hit.distance <= 0f && point == Vector3.zero) point = ball.transform.position;

            if (ball.Unblockable)
            {
                // The beam tears through: show it, but let the ball keep going.
                AddRipple(point, 0.6f);
                return BallHitResponse.PassThrough;
            }

            BallsBlocked++;
            AddRipple(point, 1f);
            _hitFlash = 1f;

            Vector3 normal = hit.normal.sqrMagnitude > 1e-4f ? hit.normal : -ball.Velocity.normalized;
            if (normal.sqrMagnitude < 1e-4f) normal = transform.forward;
            float scale = Mathf.Clamp(0.6f + ball.SpeedKmh / 160f, 0.6f, 1.6f);
            VfxManager.Spawn(VfxId.ShieldImpact, point, Quaternion.LookRotation(normal, Vector3.up), scale, _settings.EnergyTint);
            AudioManager.PlayAt(SfxId.Shield, point, Mathf.Clamp01(0.55f + ball.SpeedKmh / 250f), UnityEngine.Random.Range(0.94f, 1.06f));
            return BallHitResponse.Block;
        }

        /// <summary>
        /// Ends the barrier: stops blocking at once, then flickers out (or disappears immediately when
        /// <paramref name="immediate"/>, e.g. round reset).
        /// </summary>
        public void Dismiss(bool immediate = false)
        {
            if (this == null) return;
            if (_collider != null) _collider.enabled = false;
            if (immediate)
            {
                _dismissed = true;
                Destroy(gameObject);
                return;
            }
            if (_dismissed || _phase == Phase.Fading) return;
            _phase = Phase.Fading;
            _fadeTime = 0f;
            AudioManager.PlayAt(SfxId.Shield, transform.position + Vector3.up * (_settings.Height * 0.5f), 0.45f, 0.7f);
        }

        // ------------------------------------------------------------------ animation

        private void Update()
        {
            float dt = Time.deltaTime; // gameplay object: freezes during hitstop
            _age += dt;
            float envelope = 1f;

            switch (_phase)
            {
                case Phase.Growing:
                {
                    float g = Mathf.Clamp01(_age / _settings.GrowTime);
                    float eased = 1f - (1f - g) * (1f - g) * (1f - g);
                    _sheetRoot.localScale = new Vector3(1f, Mathf.Max(0.001f, eased), 1f);
                    envelope = 0.4f + 0.6f * eased;
                    _lifeRemaining -= dt;
                    if (g >= 1f) _phase = Phase.Holding;
                    break;
                }
                case Phase.Holding:
                    _lifeRemaining -= dt;
                    if (_lifeRemaining < 1f)
                    {
                        // Last second: the projector stutters so players can read that it is about to drop.
                        float stutter = Mathf.PerlinNoise(_age * 18f, _noiseSeed);
                        envelope = stutter > 0.62f ? 0.35f : 1f;
                    }
                    if (_lifeRemaining <= 0f) Dismiss();
                    break;
                case Phase.Fading:
                {
                    _fadeTime += dt;
                    float f = Mathf.Clamp01(_fadeTime / _settings.FadeTime);
                    float dropout = Mathf.PerlinNoise(_age * 30f, _noiseSeed + 5f) > 0.45f ? 1f : 0.3f;
                    envelope = (1f - f) * (1f - f) * dropout;
                    _sheetRoot.localScale = new Vector3(1f, Mathf.Max(0.001f, 1f - 0.15f * f), 1f);
                    if (f >= 1f)
                    {
                        _dismissed = true;
                        Destroy(gameObject);
                        return;
                    }
                    break;
                }
            }

            _hitFlash = Mathf.Max(0f, _hitFlash - dt * 6f);
            ApplyVisuals(envelope);
            UpdateRipples(dt);
        }

        private void ApplyVisuals(float envelope)
        {
            var tint = _settings.EnergyTint;
            // Gentle breathing + a little electrical noise, plus the impact flash.
            float breathe = 1f + 0.06f * Mathf.Sin(_age * 2f * Mathf.PI * 1.3f) + 0.08f * (Mathf.PerlinNoise(_age * 9f, _noiseSeed) - 0.5f);
            float k = Mathf.Max(0f, _settings.Intensity * breathe * envelope * (1f + _settings.HitFlashBoost * _hitFlash));

            var sheet = new Color(tint.r * k, tint.g * k, tint.b * k, Mathf.Clamp01(tint.a * envelope * (1f + _hitFlash)));
            ScrewsGadgetKit.SetTint(_sheetMaterial, sheet);
            ScrewsGadgetKit.SetTint(_glowMaterial, new Color(sheet.r, sheet.g, sheet.b, Mathf.Clamp01(sheet.a * 2.2f)));
            ScrewsGadgetKit.SetTint(_edgeMaterial, new Color(tint.r * k * 2.5f, tint.g * k * 2.5f, tint.b * k * 2.5f, Mathf.Clamp01(0.7f * envelope)));
            ScrewsGadgetKit.SetTint(_stripMaterial, _settings.EmitterTint * Mathf.Lerp(0.15f, 1f, envelope));

            // Lattice drifts upward out of the emitter (UVs are in lattice periods).
            float period = ScrewsGadgetKit.HexPeriodY * Mathf.Max(0.05f, _settings.HexSize);
            ScrewsGadgetKit.SetMainTextureOffset(_sheetMaterial, new Vector2(0f, -(_age * _settings.ScrollSpeed / period) % 1f));
        }

        private void AddRipple(Vector3 worldPoint, float strength)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            var centre = new Vector2(
                Mathf.Clamp(local.x, -_settings.Width * 0.5f, _settings.Width * 0.5f),
                Mathf.Clamp(local.y - 0.09f, 0f, _settings.Height));

            // Reuse a free slot, else the oldest ripple.
            int slot = 0;
            float oldest = -1f;
            for (int i = 0; i < MaxRipples; i++)
            {
                if (!_ripples[i].Active) { slot = i; oldest = float.MaxValue; break; }
                if (_ripples[i].Age > oldest) { oldest = _ripples[i].Age; slot = i; }
            }

            ref var r = ref _ripples[slot];
            r.Active = true;
            r.Centre = centre;
            r.Age = 0f;
            r.Strength = strength;
            if (r.Ring != null)
            {
                r.Ring.localPosition = new Vector3(centre.x, centre.y + 0.09f, 0f);
                r.Ring.localRotation = Quaternion.identity;
                r.Ring.localScale = Vector3.one * 0.1f;
                r.Ring.gameObject.SetActive(true);
            }
        }

        private void UpdateRipples(float dt)
        {
            bool any = false;
            float duration = _settings.RippleDuration;
            for (int i = 0; i < MaxRipples; i++)
            {
                ref var r = ref _ripples[i];
                if (!r.Active) continue;
                r.Age += dt;
                float t = r.Age / duration;
                if (t >= 1f)
                {
                    r.Active = false;
                    if (r.Ring != null) r.Ring.gameObject.SetActive(false);
                    continue;
                }
                any = true;
                float front = RippleFront(t);
                if (r.Ring != null)
                {
                    // Ring texture radius is 0.8 of the quad half-size.
                    r.Ring.localScale = Vector3.one * Mathf.Max(0.05f, front * 2f / 0.8f);
                    float a = (1f - t) * (1f - t) * r.Strength;
                    var tint = _settings.EnergyTint;
                    float k = _settings.Intensity * 2.2f * a;
                    ScrewsGadgetKit.SetTint(r.RingMaterial, new Color(tint.r * k, tint.g * k, tint.b * k, Mathf.Clamp01(a)));
                }
            }

            if (any) DisplaceSheet();
            else if (_meshDisplaced) RestoreSheet();
        }

        private float RippleFront(float t) => _settings.RippleMaxRadius * (1f - (1f - t) * (1f - t));

        /// <summary>Travelling bulge: every active ripple pushes the sheet out of plane around its expanding wave front.</summary>
        private void DisplaceSheet()
        {
            if (_sheetMesh == null || _baseVertices == null) return;
            float amp = _settings.RippleAmplitude;
            float duration = _settings.RippleDuration;
            const float width = 0.28f; // wave-front thickness (m)
            float yScale = _sheetRoot != null ? Mathf.Max(0.001f, _sheetRoot.localScale.y) : 1f;

            for (int v = 0; v < _baseVertices.Length; v++)
            {
                Vector3 p = _baseVertices[v];
                float z = 0f;
                for (int i = 0; i < MaxRipples; i++)
                {
                    ref var r = ref _ripples[i];
                    if (!r.Active) continue;
                    float t = r.Age / duration;
                    float dx = p.x - r.Centre.x, dy = p.y * yScale - r.Centre.y;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float d = (dist - RippleFront(t)) / width;
                    if (d * d > 9f) continue;
                    z += amp * r.Strength * (1f - t) * (1f - t) * Mathf.Exp(-d * d);
                }
                p.z = z;
                _workVertices[v] = p;
            }
            _sheetMesh.vertices = _workVertices;
            _meshDisplaced = true;
        }

        private void RestoreSheet()
        {
            if (_sheetMesh != null && _baseVertices != null) _sheetMesh.vertices = _baseVertices;
            _meshDisplaced = false;
        }

        // ------------------------------------------------------------------ bookkeeping

        private Material Track(Material material)
        {
            if (material != null) _materials.Add(material);
            return material;
        }

        private Mesh BuildTrackedMesh(Mesh mesh)
        {
            if (mesh != null) _meshes.Add(mesh);
            return mesh;
        }

        private void OnDestroy()
        {
            _dismissed = true;
            for (int i = 0; i < _materials.Count; i++) ScrewsGadgetKit.DestroySafe(_materials[i]);
            for (int i = 0; i < _meshes.Count; i++) ScrewsGadgetKit.DestroySafe(_meshes[i]);
            _materials.Clear();
            _meshes.Clear();
        }
    }
}
