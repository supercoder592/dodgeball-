using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.Rendering;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning of a <see cref="ScrewsGluePuddle"/> (copied from <see cref="ScrewsGlueTrapBall"/>).</summary>
    [Serializable]
    public struct ScrewsGluePuddleSettings
    {
        /// <summary>Puddle radius (m). Spec: 2 m.</summary>
        public float Radius;
        /// <summary>Lifetime (s), including the drying phase. Spec: 4 s.</summary>
        public float Duration;
        /// <summary>Slow magnitude applied to enemies inside (0.6 = 60 % slower). Spec: 60 %.</summary>
        public float SlowMagnitude;
        /// <summary>Duration (s) of each slow refresh; the slow lingers this long after the puddle vanishes abruptly.</summary>
        public float SlowRefresh;
        /// <summary>Extra planar margin (m) counted as "in the glue" (shoe size).</summary>
        public float FootMargin;
        /// <summary>Players whose feet are higher than this (m) above the glue (jumping) are not stuck.</summary>
        public float FootHeight;
        /// <summary>Viscous drag (1/s) on loose balls rolling through the puddle.</summary>
        public float BallDrag;
        /// <summary>Seconds the splat takes to spread to full size.</summary>
        public float SpreadTime;
        /// <summary>Seconds at the end during which the glue dries (loses gloss, shrinks) and disappears.</summary>
        public float DryTime;
        /// <summary>Glue albedo (dark, translucent-looking green).</summary>
        public Color Color;
        /// <summary>Wet smoothness (0..1).</summary>
        public float Smoothness;
        /// <summary>Surface wobble amplitude (fraction of the dome height / rim).</summary>
        public float Wobble;
        /// <summary>Number of small satellite droplets around the main splat.</summary>
        public int Droplets;

        /// <summary>Spec defaults: 2 m, 4 s, 60 % slow.</summary>
        public static ScrewsGluePuddleSettings Default => new ScrewsGluePuddleSettings
        {
            Radius = 2f,
            Duration = 4f,
            SlowMagnitude = 0.6f,
            SlowRefresh = 0.3f,
            FootMargin = 0.15f,
            FootHeight = 0.35f,
            BallDrag = 3.5f,
            SpreadTime = 0.2f,
            DryTime = 0.6f,
            Color = new Color(0.07f, 0.17f, 0.05f, 1f),
            Smoothness = 0.93f,
            Wobble = 0.035f,
            Droplets = 6,
        };
    }

    /// <summary>
    /// A viscous puddle left by Screws' [Glue Trap Ball]. For its lifetime every ENEMY of Screws' team standing in it
    /// (feet on the floor, within the radius) receives <see cref="StatusEffectType.Slow"/> 0.6, refreshed every frame and
    /// removed as soon as they step out (unless another of Screws' puddles still holds them). Loose balls rolling through it
    /// are bogged down by viscous drag.
    /// <para>
    /// Look: a glossy, dark-green, decal-like splat with an organic Perlin rim and a surface-tension meniscus (HDRP Lit via
    /// <see cref="MaterialFactory.CreateLit"/>, smoothness 0.93), a scatter of satellite droplets, a quick spreading splash,
    /// a slow viscous wobble of the surface and, at the end, drying: it loses its gloss and shrinks away. A trigger box on
    /// <see cref="GameLayers.AbilityVolume"/> lets other systems (AI) find it with physics queries.
    /// </para>
    /// <para>Cleared on round end / round start / match end. Gameplay object: scaled time.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ScrewsGluePuddle : MonoBehaviour
    {
        private static readonly List<ScrewsGluePuddle> s_active = new List<ScrewsGluePuddle>(8);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_active.Clear();

        /// <summary>Every live puddle.</summary>
        public static IReadOnlyList<ScrewsGluePuddle> Active => s_active;

        private const float WobbleRate = 1f / 30f; // mesh wobble update interval (s)

        private readonly List<DodgeballPlayer> _affected = new List<DodgeballPlayer>(4);
        private readonly List<Mesh> _meshes = new List<Mesh>(8);
        private ScrewsGluePuddleSettings _settings;
        private TeamId _ownerTeam = TeamId.None;
        private DodgeballPlayer _owner;
        private object _slowSource;
        private Material _material;
        private Transform _visualRoot;
        private Mesh _mainMesh;
        private Vector3[] _baseVertices;
        private Vector3[] _workVertices;
        private float _age;
        private float _wobbleTimer;
        private float _phase;
        private float _radiusFactor = 0.3f;
        private bool _ending;
        private int _smoothnessId = -1;

        private Action<RoundEndedEvent> _onRoundEnded;
        private Action<RoundStartedEvent> _onRoundStarted;
        private Action<MatchEndedEvent> _onMatchEnded;

        /// <summary>Team whose enemies are slowed.</summary>
        public TeamId OwnerTeam => _ownerTeam;

        /// <summary>Screws who created it (may be null).</summary>
        public DodgeballPlayer Owner => _owner;

        /// <summary>Key used for the slow status (one per Screws, so overlapping puddles never stack).</summary>
        public object SlowSource => _slowSource;

        /// <summary>Current effective radius (m), following the spread/dry animation.</summary>
        public float CurrentRadius => _settings.Radius * _radiusFactor;

        /// <summary>Seconds left.</summary>
        public float Remaining => Mathf.Max(0f, _settings.Duration - _age);

        // ------------------------------------------------------------------ factory / queries

        /// <summary>Spawns a puddle centred on <paramref name="floorPoint"/> (already on the floor).</summary>
        public static ScrewsGluePuddle Spawn(Vector3 floorPoint, in ScrewsGluePuddleSettings settings, TeamId ownerTeam,
            DodgeballPlayer owner, object slowSource)
        {
            var go = new GameObject("GluePuddle");
            go.layer = GameLayers.AbilityVolume;
            go.transform.SetPositionAndRotation(floorPoint, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var puddle = go.AddComponent<ScrewsGluePuddle>();
            puddle.Build(settings, ownerTeam, owner, slowSource);
            return puddle;
        }

        /// <summary>Removes every puddle created with <paramref name="slowSource"/> (null = all puddles).</summary>
        public static void DismissAll(object slowSource = null)
        {
            for (int i = s_active.Count - 1; i >= 0; i--)
            {
                var p = s_active[i];
                if (p == null) { s_active.RemoveAt(i); continue; }
                if (slowSource == null || ReferenceEquals(p._slowSource, slowSource)) p.Dismiss();
            }
        }

        /// <summary>True if <paramref name="player"/> stands in any live puddle keyed by <paramref name="slowSource"/> other than <paramref name="except"/>.</summary>
        public static bool IsInsideAny(DodgeballPlayer player, object slowSource, ScrewsGluePuddle except = null)
        {
            for (int i = 0; i < s_active.Count; i++)
            {
                var p = s_active[i];
                if (p == null || p == except || p._ending) continue;
                if (!ReferenceEquals(p._slowSource, slowSource)) continue;
                if (p.IsStandingIn(player)) return true;
            }
            return false;
        }

        /// <summary>Destroys the puddle now (removing its slows).</summary>
        public void Dismiss()
        {
            if (this == null) return;
            _ending = true;
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            _onRoundEnded = _ => Dismiss();
            _onRoundStarted = _ => Dismiss();
            _onMatchEnded = _ => Dismiss();
        }

        private void OnEnable()
        {
            if (!s_active.Contains(this)) s_active.Add(this);
            GameEvents.Subscribe(_onRoundEnded);
            GameEvents.Subscribe(_onRoundStarted);
            GameEvents.Subscribe(_onMatchEnded);
        }

        private void OnDisable()
        {
            s_active.Remove(this);
            GameEvents.Unsubscribe(_onRoundEnded);
            GameEvents.Unsubscribe(_onRoundStarted);
            GameEvents.Unsubscribe(_onMatchEnded);
            ReleaseAll();
        }

        private void OnDestroy()
        {
            ReleaseAll();
            ScrewsGadgetKit.DestroySafe(_material);
            for (int i = 0; i < _meshes.Count; i++) ScrewsGadgetKit.DestroySafe(_meshes[i]);
            _meshes.Clear();
        }

        private void Build(in ScrewsGluePuddleSettings s, TeamId ownerTeam, DodgeballPlayer owner, object slowSource)
        {
            _settings = s;
            _settings.Radius = Mathf.Max(0.2f, s.Radius);
            _settings.Duration = Mathf.Max(0.2f, s.Duration);
            _settings.SpreadTime = Mathf.Max(0.01f, s.SpreadTime);
            _settings.DryTime = Mathf.Clamp(s.DryTime, 0.01f, _settings.Duration);
            _ownerTeam = ownerTeam;
            _owner = owner;
            _slowSource = slowSource ?? this;
            _phase = UnityEngine.Random.value * 10f;

            // Trigger volume for queries (players' rigidbodies generate no gameplay here: slows are distance based).
            var trigger = gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 0.15f, 0f);
            trigger.size = new Vector3(_settings.Radius * 2f, 0.3f, _settings.Radius * 2f);

            // Glossy wet glue (physically based: dielectric, very smooth).
            _material = MaterialFactory.CreateLit("Screws Glue", s.Color, Mathf.Clamp01(s.Smoothness), 0f);
            try
            {
                string smoothness = ShaderProps.Smoothness;
                if (!string.IsNullOrEmpty(smoothness) && _material != null && _material.HasProperty(smoothness))
                    _smoothnessId = Shader.PropertyToID(smoothness);
            }
            catch (Exception)
            {
                _smoothnessId = -1; // rendering module unavailable: keep the gloss constant
            }

            _visualRoot = ScrewsGadgetKit.CreatePivot("GlueVisual", transform, new Vector3(0f, 0.004f, 0f), Quaternion.identity);
            _visualRoot.localScale = new Vector3(_radiusFactor, 1f, _radiusFactor);

            float seed = UnityEngine.Random.Range(0f, 1000f);
            _mainMesh = ScrewsGadgetKit.BuildSplat("GlueSplat", _settings.Radius, 0.12f, 56, 0.012f, seed, out _baseVertices);
            _meshes.Add(_mainMesh);
            _workVertices = (Vector3[])_baseVertices.Clone();
            ScrewsGadgetKit.CreateMeshPart("Splat", _mainMesh, _visualRoot, Vector3.zero, Quaternion.identity, Vector3.one, _material, false);

            // Satellite droplets flung past the rim.
            int droplets = Mathf.Clamp(s.Droplets, 0, 16);
            for (int i = 0; i < droplets; i++)
            {
                float angle = (i + UnityEngine.Random.Range(-0.35f, 0.35f)) / Mathf.Max(1, droplets) * Mathf.PI * 2f;
                float dist = _settings.Radius * UnityEngine.Random.Range(0.98f, 1.22f);
                float r = UnityEngine.Random.Range(0.06f, 0.2f) * Mathf.Clamp(_settings.Radius / 2f, 0.5f, 1.5f);
                var dropMesh = ScrewsGadgetKit.BuildSplat("GlueDrop", r, 0.2f, 16, 0.006f, seed + i * 13.7f, out _);
                _meshes.Add(dropMesh);
                ScrewsGadgetKit.CreateMeshPart("Droplet", dropMesh, _visualRoot,
                    new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist), Quaternion.identity, Vector3.one, _material, false);
            }

            VfxManager.Spawn(VfxId.GlueSplat, transform.position + Vector3.up * 0.05f, Quaternion.identity,
                Mathf.Clamp(_settings.Radius / 2f, 0.5f, 2f), s.Color);
            AudioManager.PlayAt(SfxId.Glue, transform.position, 1f, UnityEngine.Random.Range(0.92f, 1.05f));
        }

        // ------------------------------------------------------------------ simulation

        private void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            if (_age >= _settings.Duration)
            {
                Dismiss();
                return;
            }

            AnimateShape(dt);
            UpdateSlows();
        }

        private void FixedUpdate()
        {
            // Viscous drag on loose balls rolling through the glue.
            var manager = BallManager.Instance;
            if (manager == null || _settings.BallDrag <= 0f || _ending) return;
            var balls = manager.ActiveBalls;
            float r = CurrentRadius;
            float r2 = r * r;
            Vector3 centre = transform.position;
            float damping = Mathf.Exp(-_settings.BallDrag * Time.fixedDeltaTime);
            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || !ball.IsFree) continue;
                Vector3 p = ball.transform.position;
                if (p.y - centre.y > ball.Radius + 0.08f) continue; // only balls touching the glue
                float dx = p.x - centre.x, dz = p.z - centre.z;
                if (dx * dx + dz * dz > r2) continue;
                var body = ball.Body;
                if (body == null || body.isKinematic) continue;
                Vector3 v = body.GetVelocity();
                body.SetVelocity(new Vector3(v.x * damping, v.y, v.z * damping));
                body.SetAngularVelocity(body.GetAngularVelocity() * damping);
            }
        }

        private void AnimateShape(float dt)
        {
            float remaining = _settings.Duration - _age;
            float spread = Mathf.Clamp01(_age / _settings.SpreadTime);
            spread = 1f - (1f - spread) * (1f - spread) * (1f - spread); // splash: fast then settling
            float dry = 1f - Mathf.Clamp01(remaining / _settings.DryTime); // 0 wet .. 1 dried away

            // Gameplay radius follows the splash, and the glue loses its grip as it dries out at the very end.
            _radiusFactor = Mathf.Lerp(0.3f, 1f, spread) * Mathf.Lerp(1f, 0.85f, dry);
            if (dry > 0.75f) _radiusFactor *= Mathf.InverseLerp(1f, 0.75f, dry);

            float thickness = Mathf.Lerp(1f, 0.25f, dry);
            _visualRoot.localScale = new Vector3(Mathf.Max(0.001f, _radiusFactor), thickness, Mathf.Max(0.001f, _radiusFactor));

            if (_smoothnessId >= 0 && _material != null)
                _material.SetFloat(_smoothnessId, Mathf.Lerp(_settings.Smoothness, 0.45f, dry));

            // Slow viscous wobble of the surface (30 Hz mesh update is plenty for a 2 Hz motion).
            _wobbleTimer -= dt;
            if (_wobbleTimer > 0f || _mainMesh == null || _settings.Wobble <= 0f) return;
            _wobbleTimer = WobbleRate;
            float t = _age + _phase;
            float amp = _settings.Wobble * (1f - dry);
            for (int i = 0; i < _baseVertices.Length; i++)
            {
                Vector3 b = _baseVertices[i];
                float wave = Mathf.Sin(t * 2.2f + b.x * 2.1f + b.z * 1.7f) + 0.5f * Mathf.Sin(t * 3.7f - b.x * 3.3f + b.z * 2.9f);
                float rim = 1f + amp * 0.35f * wave;
                _workVertices[i] = new Vector3(b.x * rim, b.y * (1f + amp * 6f * wave), b.z * rim);
            }
            _mainMesh.vertices = _workVertices;
            _mainMesh.RecalculateNormals();
        }

        private void UpdateSlows()
        {
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                bool enemy = _ownerTeam.IsValid() && p.Team.IsValid() && p.Team != _ownerTeam;
                bool inside = enemy && !_ending && p.Health != null && p.Health.IsAlive && IsStandingIn(p);
                int index = _affected.IndexOf(p);

                if (inside)
                {
                    if (p.Status != null) p.Status.Apply(StatusEffectType.Slow, _settings.SlowRefresh, _settings.SlowMagnitude, _slowSource);
                    if (index < 0)
                    {
                        _affected.Add(p);
                        // Squelch as a foot lands in the glue.
                        AudioManager.PlayAt(SfxId.Glue, p.Position, 0.35f, UnityEngine.Random.Range(1.1f, 1.3f));
                        VfxManager.Spawn(VfxId.GlueSplat, p.Position + Vector3.up * 0.03f, Quaternion.identity, 0.35f, _settings.Color);
                    }
                }
                else if (index >= 0)
                {
                    _affected.RemoveAt(index);
                    Release(p);
                }
            }

            // Players that left the registry (destroyed) are dropped silently.
            for (int i = _affected.Count - 1; i >= 0; i--)
                if (_affected[i] == null) _affected.RemoveAt(i);
        }

        /// <summary>True if <paramref name="p"/>'s feet are in the glue (planar radius + foot margin, not jumping).</summary>
        public bool IsStandingIn(DodgeballPlayer p)
        {
            if (p == null) return false;
            Vector3 centre = transform.position;
            Vector3 pos = p.Position;
            if (pos.y - centre.y > _settings.FootHeight) return false;
            float r = CurrentRadius + _settings.FootMargin;
            float dx = pos.x - centre.x, dz = pos.z - centre.z;
            return dx * dx + dz * dz <= r * r;
        }

        private void Release(DodgeballPlayer p)
        {
            if (p == null || p.Status == null) return;
            if (IsInsideAny(p, _slowSource, this)) return; // another puddle of the same Screws still holds them
            p.Status.Remove(StatusEffectType.Slow, _slowSource);
        }

        private void ReleaseAll()
        {
            _ending = true;
            for (int i = 0; i < _affected.Count; i++) Release(_affected[i]);
            _affected.Clear();
        }
    }
}
