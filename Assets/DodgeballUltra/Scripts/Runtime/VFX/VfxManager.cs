using System;
using System.Collections.Generic;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.VFX
{
    /// <summary>
    /// Every effect the game can spawn. Built procedurally (ParticleSystem) by the VFX module, overridable by prefabs.
    /// <para>
    /// Orientation convention: the spawn rotation's <b>up</b> (+Y) is the surface normal (use
    /// <see cref="VfxManager.NormalRotation"/>); directional effects emit along the rotation's <b>forward</b> (+Z).
    /// Sizes below are at <c>scale = 1</c>; scale multiplies all spatial dimensions (timing is unchanged).
    /// </para>
    /// </summary>
    public enum VfxId
    {
        /// <summary>Ball hits a player: chalk/compressed-air puff (~0.6 m) + crumbs. Auto-spawned on BallHitPlayerEvent.</summary>
        HitImpact = 0,
        /// <summary>Knock-out hit: large dust blast (~1.2 m), flash, debris. Auto-spawned on PlayerEliminatedEvent.</summary>
        EliminationBurst,
        /// <summary>Normal catch: small puff at the hands. Auto-spawned on BallCaughtEvent.</summary>
        CatchPuff,
        /// <summary>Perfect catch: warm flash, 1.4 m ring, light streaks, light flash. Auto-spawned on BallCaughtEvent.</summary>
        PerfectCatchBurst,
        /// <summary>Air wake along +Z at the release point of fast throws. Auto-spawned on BallThrownEvent.</summary>
        ThrowWhoosh,
        /// <summary>Low dust ring on hard floor bounces. Auto-spawned on BallBouncedEvent (floor, above a speed threshold).</summary>
        FloorImpactDust,
        /// <summary>Looping dust under a sliding player (attach at the feet). Auto-attached while in the Sliding state.</summary>
        SlideDust,
        /// <summary>Dust ring when landing from a jump. Auto-spawned when leaving the Airborne state.</summary>
        LandingDust,
        /// <summary>Tiny scuff of dust for heavy footsteps / sprint starts.</summary>
        FootstepDust,
        /// <summary>Rayne meteor AOE (3 m radius): fireball, ground shock, embers, smoke, light flash.</summary>
        Shockwave,
        /// <summary>Rayne meteor trail (attached, looping): flames, heat smoke, embers, flickering light.</summary>
        FireTrail,
        /// <summary>Rayne ultimate (attached, looping): soft cyan light ribbon, core glow, motes, light.</summary>
        BeamTrail,
        /// <summary>Beam-ball impact: cyan flash, ripple, sparks, vapour, light flash.</summary>
        BeamImpact,
        /// <summary>Shadow clone appears (human-sized inky smoke; spawn at the clone's feet).</summary>
        CloneSpawn,
        /// <summary>Shadow clone popped: rising ash flakes and smoke (spawn at the clone's feet).</summary>
        CloneDissolve,
        /// <summary>Gale cloak engage/disengage shimmer over the body (spawn at the feet).</summary>
        CloakShimmer,
        /// <summary>Gale / Houdini smoke-bomb teleport (spawn at the feet, origin and destination).</summary>
        TeleportPoof,
        /// <summary>Bear (attached, looping): 5 m field hemisphere centred on the root (attach at the feet; scale = radius / 5).</summary>
        MagneticField,
        /// <summary>Bear Aegis struck: ripple and crackle in the barrier plane (up = barrier normal).</summary>
        ShieldImpact,
        /// <summary>Gouki charge (forward = charge direction): dust kicked backwards, debris.</summary>
        TackleDust,
        /// <summary>Gouki slam: dust waves reaching ~8 m, pressure ring, debris chunks (scale = reach / 8).</summary>
        EarthquakeRing,
        /// <summary>Screws glue ball: droplets, blobs and a glossy 2 m puddle lasting 4 s (scale = puddle radius in m).</summary>
        GlueSplat,
        /// <summary>Screws turret shot (forward = barrel): compressed-air puff.</summary>
        TurretMuzzle,
        /// <summary>Houdini swap marker: violet light column, swirl, smoke (spawn at both players' feet).</summary>
        SwapFlash,
        /// <summary>Houdini Grand Vanish: stage smoke bomb with a little glitter.</summary>
        VanishSmoke,
        /// <summary>Elsa freezing ball impact: ice shards, sinking cold mist, glitter, cool light.</summary>
        IceBurst,
        /// <summary>Elsa frost trail (attached, looping) behind a thrown ball.</summary>
        IceTrail,
        /// <summary>Elsa freeze (attached, looping): cold vapour rolling off a frozen player (attach at the feet).</summary>
        FrozenMist,
        /// <summary>Specter dodge (forward = dodge direction): air streaks + scuff.</summary>
        DodgeAfterimage,
        /// <summary>Specter / Chrono rewind (attached, looping): time motes + light ribbon (attach at the feet).</summary>
        RewindTrail,
        /// <summary>Chrono stasis (attached, looping): ~0.8 m shimmering bubble around a ball.</summary>
        StasisBubble,
        /// <summary>Chrono ultimate: 5 x 5 m zone outline and motes (looping; scale = side / 5).</summary>
        TemporalZone,
        /// <summary>Return to the infield: warm 4.5 m light column, rising motes. Auto-attached on PlayerRevivedEvent.</summary>
        ReviveBeam,
        /// <summary>Ultimate charged: rising spiral in the hero's theme colour. Auto-attached on UltimateChargeChangedEvent.</summary>
        UltimateReady,
    }

    /// <summary>Handle to a spawned effect (for stopping looping/attached effects).</summary>
    public struct VfxHandle
    {
        public int Id;
        public GameObject Instance;
        public bool IsValid => Instance != null;
    }

    /// <summary>
    /// CONTRACT (kernel) - pooled particle effects. Realistic-leaning procedural effects (soft dust, sparks, heat distortion,
    /// ice shards) built from ParticleSystems with pipeline-appropriate materials. Prefab overrides can be assigned per id.
    /// Static helpers are null-safe so gameplay code never checks for the manager.
    /// <para>
    /// Every <see cref="VfxId"/> is built once at start-up by <see cref="ProceduralVfxLibrary"/> as an inactive template
    /// (materials from <c>Rendering.MaterialFactory.CreateParticle</c>), then instantiated into per-id pools. Instances are
    /// reused: finished effects return to the pool automatically, attached looping effects run until
    /// <see cref="Stop"/> or their duration, and stale handles (from an instance that was recycled) are ignored.
    /// </para>
    /// <para>
    /// The manager also listens to <see cref="GameEvents"/> and spawns the generic combat effects itself (hit impacts,
    /// catches, floor dust, eliminations, revives, ultimate-ready, slide/landing dust, fast-throw wakes), so gameplay code
    /// only spawns ability-specific effects. Effects simulate in scaled time: they freeze with the hitstop.
    /// </para>
    /// <para>Owner module: VFX.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VfxManager : MonoBehaviour
    {
        public static VfxManager Instance { get; private set; }

        private static readonly int s_idCount = Enum.GetValues(typeof(VfxId)).Length;

        // ------------------------------------------------------------------ inspector

        [Header("Library")]
        [Tooltip("Authored prefabs replacing procedural effects (optional). Later entries win for duplicate ids.")]
        [SerializeField] private List<VfxPrefabOverride> prefabOverrides = new List<VfxPrefabOverride>();

        [Tooltip("Instantiate each effect's prewarm count at start-up so first uses never instantiate mid-match.")]
        [SerializeField] private bool prewarmPools = true;

        [Tooltip("Hard cap on simultaneously active effect instances (oldest non-attached effects are recycled beyond it).")]
        [SerializeField, Min(8)] private int maxActiveEffects = 96;

        [Header("Automatic effects (GameEvents)")]
        [Tooltip("Spawn the generic combat effects from game events. Disable if another system spawns them.")]
        [SerializeField] private bool reactToGameEvents = true;

        [Tooltip("Ball speed range (km/h) mapped to the hit impact scale range.")]
        [SerializeField] private Vector2 hitSpeedRangeKmh = new Vector2(40f, 180f);

        [Tooltip("HitImpact scale at the low / high end of the speed range.")]
        [SerializeField] private Vector2 hitScaleRange = new Vector2(0.65f, 1.6f);

        [Tooltip("Scale multiplier for hits negated by shields / evasion.")]
        [SerializeField, Range(0f, 1f)] private float negatedHitScale = 0.55f;

        [Tooltip("Minimum impact speed (m/s) for a floor bounce to raise dust.")]
        [SerializeField, Min(0f)] private float floorDustMinSpeed = 4f;

        [Tooltip("Impact speed (m/s) at which floor dust reaches its maximum scale.")]
        [SerializeField, Min(0.1f)] private float floorDustFullSpeed = 25f;

        [Tooltip("FloorImpactDust scale at the minimum / full impact speed.")]
        [SerializeField] private Vector2 floorDustScaleRange = new Vector2(0.55f, 1.4f);

        [Tooltip("Minimum throw speed (km/h) that leaves a visible air wake (passes never do).")]
        [SerializeField, Min(0f)] private float throwWakeMinKmh = 95f;

        [Tooltip("Attach SlideDust while a player is in the Sliding state.")]
        [SerializeField] private bool autoSlideDust = true;

        [Tooltip("Safety limit (s) for an auto slide-dust effect if the slide end is never observed.")]
        [SerializeField, Min(0.2f)] private float slideDustMaxDuration = 2f;

        [Tooltip("Spawn LandingDust when a player lands from the Airborne state.")]
        [SerializeField] private bool autoLandingDust = true;

        [Tooltip("Scale of automatic landing dust.")]
        [SerializeField, Range(0.2f, 2f)] private float landingDustScale = 0.8f;

        [Tooltip("How much of the hero theme colour tints UltimateReady (0 = white, 1 = full theme colour).")]
        [SerializeField, Range(0f, 1f)] private float ultimateReadyTintStrength = 0.8f;

        [Header("Lights")]
        [Tooltip("Enable the small dynamic lights of fire/energy effects (flashes, fire trail). Disable on low-end hardware.")]
        [SerializeField] private bool enableEffectLights = true;

        // ------------------------------------------------------------------ runtime state

        private VfxDefinition[] _definitions;
        private List<VfxInstance>[] _free;
        private int[] _activePerId;
        private readonly List<VfxInstance> _active = new List<VfxInstance>(64);
        private readonly Dictionary<int, VfxInstance> _byGameObject = new Dictionary<int, VfxInstance>(128);
        private readonly Dictionary<DodgeballPlayer, VfxHandle> _slideEffects = new Dictionary<DodgeballPlayer, VfxHandle>(8);
        private Transform _templatesRoot;
        private Transform _poolRoot;
        private int _nextSerial = 1;
        private bool _built;

        // Cached delegates (subscribe/unsubscribe without allocating each time).
        private Action<BallHitPlayerEvent> _onBallHit;
        private Action<BallCaughtEvent> _onBallCaught;
        private Action<BallBouncedEvent> _onBallBounced;
        private Action<BallThrownEvent> _onBallThrown;
        private Action<PlayerEliminatedEvent> _onEliminated;
        private Action<PlayerRevivedEvent> _onRevived;
        private Action<UltimateChargeChangedEvent> _onUltCharge;
        private Action<PlayerStateChangedEvent> _onStateChanged;

        /// <summary>Number of effects currently playing (diagnostics).</summary>
        public int ActiveCount => _active.Count;

        /// <summary>When false, events no longer spawn the generic effects (explicit Play calls still work).</summary>
        public bool ReactToGameEvents
        {
            get => reactToGameEvents;
            set => reactToGameEvents = value;
        }

        /// <summary>Rotation whose up axis (+Y) is <paramref name="normal"/> - the spawn convention of every effect.</summary>
        public static Quaternion NormalRotation(Vector3 normal)
        {
            if (normal.sqrMagnitude < 1e-8f) return Quaternion.identity;
            return Quaternion.FromToRotation(Vector3.up, normal.normalized);
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VfxManager] A second VfxManager was created; the duplicate is disabled.", this);
                enabled = false;
                Destroy(this);
                return;
            }
            Instance = this;

            _onBallHit = OnBallHitPlayer;
            _onBallCaught = OnBallCaught;
            _onBallBounced = OnBallBounced;
            _onBallThrown = OnBallThrown;
            _onEliminated = OnPlayerEliminated;
            _onRevived = OnPlayerRevived;
            _onUltCharge = OnUltimateChargeChanged;
            _onStateChanged = OnPlayerStateChanged;

            BuildLibrary();
        }

        private void OnEnable()
        {
            if (Instance != this) return;
            GameEvents.Subscribe(_onBallHit);
            GameEvents.Subscribe(_onBallCaught);
            GameEvents.Subscribe(_onBallBounced);
            GameEvents.Subscribe(_onBallThrown);
            GameEvents.Subscribe(_onEliminated);
            GameEvents.Subscribe(_onRevived);
            GameEvents.Subscribe(_onUltCharge);
            GameEvents.Subscribe(_onStateChanged);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe(_onBallHit);
            GameEvents.Unsubscribe(_onBallCaught);
            GameEvents.Unsubscribe(_onBallBounced);
            GameEvents.Unsubscribe(_onBallThrown);
            GameEvents.Unsubscribe(_onEliminated);
            GameEvents.Unsubscribe(_onRevived);
            GameEvents.Unsubscribe(_onUltCharge);
            GameEvents.Unsubscribe(_onStateChanged);
            _slideEffects.Clear();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ public API (contract)

        /// <summary>
        /// Plays <paramref name="id"/> at a world position. The rotation's up axis is the surface normal. Looping effects
        /// played this way stop after their default loop duration. <paramref name="tint"/> multiplies the particle colours.
        /// </summary>
        public VfxHandle Play(VfxId id, Vector3 position, Quaternion rotation, float scale = 1f, Color? tint = null)
        {
            var inst = Spawn(id, null, position, rotation, scale, tint, -1f);
            return inst != null ? new VfxHandle { Id = inst.Serial, Instance = inst.Go } : default;
        }

        /// <summary>Attaches an effect to <paramref name="parent"/>. <paramref name="duration"/> &lt;= 0 keeps looping until Stop.</summary>
        public VfxHandle PlayAttached(VfxId id, Transform parent, Vector3 localOffset, float scale = 1f, Color? tint = null, float duration = 0f)
        {
            if (parent == null)
                return Play(id, localOffset, Quaternion.identity, scale, tint); // no parent: treat the offset as a world position

            var inst = Spawn(id, parent, localOffset, Quaternion.identity, scale, tint, duration);
            return inst != null ? new VfxHandle { Id = inst.Serial, Instance = inst.Go } : default;
        }

        /// <summary>
        /// Stops an effect. By default emission stops and live particles fade out naturally (the instance detaches from
        /// its parent so it survives the parent being destroyed); <paramref name="immediate"/> clears it at once.
        /// Stale handles (the instance was already recycled) are ignored.
        /// </summary>
        public void Stop(VfxHandle handle, bool immediate = false)
        {
            if (!TryResolve(handle, out var inst)) return;
            if (immediate) Release(inst);
            else BeginStop(inst);
        }

        /// <summary>Null-safe shortcut.</summary>
        public static VfxHandle Spawn(VfxId id, Vector3 position, Quaternion rotation, float scale = 1f, Color? tint = null) =>
            Instance != null ? Instance.Play(id, position, rotation, scale, tint) : default;

        /// <summary>Null-safe shortcut.</summary>
        public static VfxHandle SpawnAttached(VfxId id, Transform parent, Vector3 localOffset, float scale = 1f, Color? tint = null, float duration = 0f) =>
            Instance != null ? Instance.PlayAttached(id, parent, localOffset, scale, tint, duration) : default;

        /// <summary>Null-safe shortcut.</summary>
        public static void StopEffect(VfxHandle handle, bool immediate = false)
        {
            if (Instance != null && handle.IsValid) Instance.Stop(handle, immediate);
        }

        // ------------------------------------------------------------------ public API (additions)

        /// <summary>True while the effect behind <paramref name="handle"/> is still playing (emitting or fading out).</summary>
        public bool IsPlaying(VfxHandle handle) => TryResolve(handle, out _);

        /// <summary>Stops every active effect (round resets, scene transitions).</summary>
        public void StopAll(bool immediate = true)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (i >= _active.Count) continue;
                var inst = _active[i];
                if (immediate) Release(inst);
                else BeginStop(inst);
            }
        }

        /// <summary>
        /// Replaces (or with a null prefab restores) the effect for <paramref name="id"/> at runtime. Active instances of
        /// the old template finish normally; pooled ones are discarded.
        /// </summary>
        public void SetPrefabOverride(VfxId id, GameObject prefab, float lifetime = 0f, bool looping = false)
        {
            for (int i = prefabOverrides.Count - 1; i >= 0; i--)
                if (prefabOverrides[i].id == id) prefabOverrides.RemoveAt(i);
            if (prefab != null) prefabOverrides.Add(new VfxPrefabOverride { id = id, prefab = prefab, lifetime = lifetime, looping = looping });
            if (!_built) return;

            int index = (int)id;
            var pool = _free[index];
            for (int i = 0; i < pool.Count; i++) DestroyInstance(pool[i]);
            pool.Clear();
            if (_definitions[index] != null && _definitions[index].Template != null) Destroy(_definitions[index].Template);
            _definitions[index] = CreateDefinition(id);
        }

        // ------------------------------------------------------------------ building

        private void BuildLibrary()
        {
            if (_built) return;
            _built = true;

            var templates = new GameObject("VFX Templates");
            templates.transform.SetParent(transform, false);
            templates.SetActive(false);
            _templatesRoot = templates.transform;

            var pool = new GameObject("VFX Pool");
            pool.transform.SetParent(transform, false);
            _poolRoot = pool.transform;

            _definitions = new VfxDefinition[s_idCount];
            _free = new List<VfxInstance>[s_idCount];
            _activePerId = new int[s_idCount];
            for (int i = 0; i < s_idCount; i++)
            {
                _free[i] = new List<VfxInstance>(4);
                _definitions[i] = CreateDefinition((VfxId)i);
            }

            if (!prewarmPools) return;
            for (int i = 0; i < s_idCount; i++)
            {
                var def = _definitions[i];
                if (def == null) continue;
                int count = Mathf.Clamp(def.Prewarm, 0, def.MaxInstances);
                for (int n = 0; n < count; n++)
                {
                    var inst = CreateInstance(def);
                    if (inst != null) _free[i].Add(inst);
                }
            }
        }

        private VfxDefinition CreateDefinition(VfxId id)
        {
            try
            {
                for (int i = prefabOverrides.Count - 1; i >= 0; i--)
                {
                    var o = prefabOverrides[i];
                    if (o.id != id || o.prefab == null) continue;
                    var template = Instantiate(o.prefab, _templatesRoot, false);
                    template.name = "VFX_" + id + "_Override";
                    template.SetActive(false);
                    bool anyLoop = false;
                    foreach (var ps in template.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = ps.main;
                        main.playOnAwake = false;
                        anyLoop |= main.loop;
                    }
                    return new VfxDefinition
                    {
                        Id = id,
                        Template = template,
                        Looping = o.looping || anyLoop,
                        FixedLifetime = o.lifetime > 0f ? o.lifetime : 2f,
                        ScaleModules = false,
                        MaxInstances = 12,
                        Prewarm = 1,
                    };
                }
                return ProceduralVfxLibrary.Build(id, _templatesRoot);
            }
            catch (Exception e)
            {
                // One broken effect must never take the whole library (or the match) down.
                Debug.LogError($"[VfxManager] Failed to build effect {id}: {e}");
                return null;
            }
        }

        private VfxInstance CreateInstance(VfxDefinition def)
        {
            if (def == null || def.Template == null) return null;
            var go = Instantiate(def.Template, _poolRoot, false);
            go.name = def.Template.name;
            go.SetActive(false);

            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            var bases = new VfxSystemBase[systems.Length];
            for (int i = 0; i < systems.Length; i++) bases[i] = CaptureBase(systems[i]);

            var inst = new VfxInstance
            {
                Definition = def,
                Go = go,
                Transform = go.transform,
                GoId = go.GetInstanceID(),
                Systems = bases,
                Light = go.GetComponentInChildren<Light>(true),
                LightSeed = UnityEngine.Random.value * 100f,
            };
            if (inst.Light != null)
            {
                inst.LightBaseIntensity = inst.Light.intensity;
                inst.LightBaseRange = inst.Light.range;
                inst.LightBaseColor = inst.Light.color;
                inst.Light.enabled = false;
            }
            _byGameObject[inst.GoId] = inst;
            return inst;
        }

        private static VfxSystemBase CaptureBase(ParticleSystem ps)
        {
            var main = ps.main;
            var b = new VfxSystemBase
            {
                System = ps,
                Size3D = main.startSize3D,
                Speed = main.startSpeed,
                Gravity = main.gravityModifierMultiplier,
                Color = main.startColor,
            };
            if (b.Size3D)
            {
                b.SizeX = main.startSizeX;
                b.SizeY = main.startSizeY;
                b.SizeZ = main.startSizeZ;
            }
            else
            {
                b.Size = main.startSize;
            }

            var vel = ps.velocityOverLifetime;
            b.HasVelocity = vel.enabled;
            if (b.HasVelocity) b.VelocityModifier = vel.speedModifierMultiplier;

            var limit = ps.limitVelocityOverLifetime;
            b.HasLimit = limit.enabled;
            if (b.HasLimit) b.Limit = limit.limitMultiplier;

            var noise = ps.noise;
            b.HasNoise = noise.enabled;
            if (b.HasNoise) b.Noise = noise.strengthMultiplier;
            return b;
        }

        private void DestroyInstance(VfxInstance inst)
        {
            if (inst == null) return;
            _byGameObject.Remove(inst.GoId);
            if (inst.Go != null) Destroy(inst.Go);
        }

        // ------------------------------------------------------------------ spawning / pooling

        private VfxInstance Spawn(VfxId id, Transform parent, Vector3 positionOrOffset, Quaternion rotation, float scale, Color? tint, float duration)
        {
            if (!_built) BuildLibrary();
            int index = (int)id;
            if (index < 0 || index >= s_idCount) return null;
            var def = _definitions[index];
            if (def == null) return null;

            var inst = Acquire(def, index);
            if (inst == null) return null;

            scale = Mathf.Max(0.01f, scale);
            var tr = inst.Transform;
            if (parent != null)
            {
                tr.SetParent(parent, false);
                tr.localPosition = positionOrOffset;
                tr.localRotation = Quaternion.identity;
                inst.Attached = true;
            }
            else
            {
                tr.SetParent(_poolRoot, false);
                tr.SetPositionAndRotation(positionOrOffset, rotation);
                inst.Attached = false;
            }
            tr.localScale = new Vector3(scale, scale, scale);

            inst.Scale = scale;
            inst.Tinted = tint.HasValue;
            inst.Tint = tint ?? Color.white;
            if (def.ScaleModules) ApplyScaleAndTint(inst);

            float now = Time.time;
            inst.Serial = _nextSerial++;
            if (_nextSerial == int.MaxValue) _nextSerial = 1;
            inst.Active = true;
            inst.Stopping = false;
            inst.StartTime = now;
            inst.StopTime = 0f;
            // Timed stop: explicit duration, or the default loop length for looping effects played without an owner.
            if (duration > 0f) inst.StopAt = now + duration;
            else if (duration < 0f && def.Looping) inst.StopAt = now + Mathf.Max(0.1f, def.DefaultLoopDuration);
            else inst.StopAt = 0f;

            inst.Go.SetActive(true);
            var systems = inst.Systems;
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i].System;
                if (ps == null) continue;
                ps.Clear(false);
                ps.Play(false);
            }

            if (inst.Light != null)
            {
                bool useLight = enableEffectLights && (def.LightFlashDuration > 0f || def.LightLooping);
                inst.Light.enabled = useLight;
                if (useLight)
                {
                    inst.Light.range = inst.LightBaseRange * scale;
                    var c = inst.LightBaseColor;
                    if (inst.Tinted) c = new Color(c.r * inst.Tint.r, c.g * inst.Tint.g, c.b * inst.Tint.b, c.a);
                    inst.Light.color = c;
                    inst.Light.intensity = inst.LightBaseIntensity * scale;
                }
            }

            _active.Add(inst);
            _activePerId[index]++;
            EnforceGlobalCap();
            return inst;
        }

        private VfxInstance Acquire(VfxDefinition def, int index)
        {
            // Too many of this effect alive: recycle the oldest non-attached one (attached loops belong to gameplay).
            if (_activePerId[index] >= Mathf.Max(1, def.MaxInstances))
            {
                VfxInstance oldest = null;
                for (int i = 0; i < _active.Count; i++)
                {
                    var a = _active[i];
                    if (a.Definition != def || a.Go == null) continue;
                    if (a.Attached && !a.Stopping) continue;
                    if (oldest == null || a.StartTime < oldest.StartTime) oldest = a;
                }
                if (oldest != null) Release(oldest);
            }

            var pool = _free[index];
            while (pool.Count > 0)
            {
                var candidate = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);
                if (candidate.Go != null) return candidate;
                _byGameObject.Remove(candidate.GoId); // destroyed externally
            }
            return CreateInstance(def);
        }

        private void EnforceGlobalCap()
        {
            int guard = 8;
            while (_active.Count > maxActiveEffects && guard-- > 0)
            {
                VfxInstance oldest = null;
                for (int i = 0; i < _active.Count; i++)
                {
                    var a = _active[i];
                    if (a.Attached && !a.Stopping) continue;
                    if (oldest == null || a.StartTime < oldest.StartTime) oldest = a;
                }
                if (oldest == null) return;
                Release(oldest);
            }
        }

        private void ApplyScaleAndTint(VfxInstance inst)
        {
            if (Mathf.Approximately(inst.LastAppliedScale, inst.Scale) && inst.LastAppliedTinted == inst.Tinted &&
                (!inst.Tinted || inst.LastAppliedTint == inst.Tint))
                return; // unchanged since the previous play of this pooled instance

            float s = inst.Scale;
            var systems = inst.Systems;
            for (int i = 0; i < systems.Length; i++)
            {
                ref var b = ref systems[i];
                var ps = b.System;
                if (ps == null) continue;
                var main = ps.main;
                if (b.Size3D)
                {
                    main.startSizeX = ScaleCurve(b.SizeX, s);
                    main.startSizeY = ScaleCurve(b.SizeY, s);
                    main.startSizeZ = ScaleCurve(b.SizeZ, s);
                }
                else
                {
                    main.startSize = ScaleCurve(b.Size, s);
                }
                main.startSpeed = ScaleCurve(b.Speed, s);
                // Scaling gravity with size keeps trajectories geometrically similar (distance ~ v^2 / g).
                main.gravityModifierMultiplier = b.Gravity * s;
                main.startColor = inst.Tinted ? TintGradient(b.Color, inst.Tint) : b.Color;

                if (b.HasVelocity)
                {
                    var vel = ps.velocityOverLifetime;
                    vel.speedModifierMultiplier = b.VelocityModifier * s;
                }
                if (b.HasLimit)
                {
                    var limit = ps.limitVelocityOverLifetime;
                    limit.limitMultiplier = b.Limit * s;
                }
                if (b.HasNoise)
                {
                    var noise = ps.noise;
                    noise.strengthMultiplier = b.Noise * s;
                }
            }
            inst.LastAppliedScale = inst.Scale;
            inst.LastAppliedTinted = inst.Tinted;
            inst.LastAppliedTint = inst.Tint;
        }

        private static ParticleSystem.MinMaxCurve ScaleCurve(in ParticleSystem.MinMaxCurve c, float s)
        {
            switch (c.mode)
            {
                case ParticleSystemCurveMode.Constant: return new ParticleSystem.MinMaxCurve(c.constant * s);
                case ParticleSystemCurveMode.TwoConstants: return new ParticleSystem.MinMaxCurve(c.constantMin * s, c.constantMax * s);
                case ParticleSystemCurveMode.Curve: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * s, c.curve);
                default: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * s, c.curveMin, c.curveMax);
            }
        }

        private static ParticleSystem.MinMaxGradient TintGradient(in ParticleSystem.MinMaxGradient g, Color tint)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color: return new ParticleSystem.MinMaxGradient(g.color * tint);
                case ParticleSystemGradientMode.TwoColors: return new ParticleSystem.MinMaxGradient(g.colorMin * tint, g.colorMax * tint);
                default: return g; // gradient start colours are not tinted (would allocate); templates never use them
            }
        }

        private bool TryResolve(VfxHandle handle, out VfxInstance inst)
        {
            inst = null;
            if (handle.Instance == null) return false;
            if (!_byGameObject.TryGetValue(handle.Instance.GetInstanceID(), out inst)) return false;
            return inst.Active && inst.Serial == handle.Id;
        }

        private void BeginStop(VfxInstance inst)
        {
            if (!inst.Active || inst.Stopping) return;
            inst.Stopping = true;
            inst.StopTime = Time.time;
            var systems = inst.Systems;
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i].System;
                if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
            // Detach so the fading particles survive their owner being destroyed and the instance returns to the pool.
            if (inst.Attached && inst.Go != null)
            {
                inst.Transform.SetParent(_poolRoot, true);
                inst.Attached = false;
            }
        }

        /// <summary>Returns an instance to its pool immediately.</summary>
        private void Release(VfxInstance inst)
        {
            if (!inst.Active) return;
            inst.Active = false;
            inst.Stopping = false;
            int activeIndex = _active.IndexOf(inst);
            if (activeIndex >= 0) RemoveActiveAt(activeIndex); // also keeps the per-id counter in sync

            if (inst.Go == null)
            {
                _byGameObject.Remove(inst.GoId);
                return;
            }

            var systems = inst.Systems;
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i].System;
                if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            if (inst.Light != null) inst.Light.enabled = false;
            inst.Go.SetActive(false);
            inst.Transform.SetParent(_poolRoot, false);
            inst.Attached = false;

            // Discard instances of a replaced template instead of pooling them.
            int index = (int)inst.Definition.Id;
            if (_definitions[index] != inst.Definition)
            {
                DestroyInstance(inst);
                return;
            }
            _free[index].Add(inst);
        }

        private void RemoveActiveAt(int index)
        {
            var inst = _active[index];
            int last = _active.Count - 1;
            _active[index] = _active[last]; // swap-remove: order is irrelevant
            _active.RemoveAt(last);
            if (inst.Definition != null)
            {
                int id = (int)inst.Definition.Id;
                _activePerId[id] = Mathf.Max(0, _activePerId[id] - 1);
            }
        }

        // ------------------------------------------------------------------ per-frame upkeep

        private void LateUpdate()
        {
            if (_active.Count == 0) return;
            float now = Time.time;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (i >= _active.Count) continue;
                var inst = _active[i];

                // Destroyed together with the object it was attached to: forget it (the pool re-grows on demand).
                if (inst.Go == null)
                {
                    inst.Active = false;
                    RemoveActiveAt(i);
                    _byGameObject.Remove(inst.GoId);
                    continue;
                }

                var def = inst.Definition;
                if (!inst.Stopping && inst.StopAt > 0f && now >= inst.StopAt) BeginStop(inst);

                bool lightDone = UpdateLight(inst, def, now);

                float age = now - inst.StartTime;
                bool finished;
                if (inst.Systems.Length == 0)
                {
                    // Override prefab without particles: fixed lifetime (or until stopped for loops).
                    finished = inst.Stopping || (!def.Looping && age >= def.FixedLifetime);
                }
                else if (inst.Stopping || !def.Looping)
                {
                    finished = !AnyAlive(inst);
                    // Safety net: a system that never reports "dead" (e.g. misconfigured override) is recycled eventually.
                    float limit = inst.Stopping ? now - inst.StopTime : age;
                    if (limit > 15f) finished = true;
                }
                else
                {
                    finished = false;
                }

                if (finished && lightDone) Release(inst);
            }
        }

        private static bool AnyAlive(VfxInstance inst)
        {
            var systems = inst.Systems;
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i].System;
                if (ps != null && ps.IsAlive(false)) return true;
            }
            return false;
        }

        /// <summary>Animates the effect light. Returns true when the light no longer needs the instance.</summary>
        private bool UpdateLight(VfxInstance inst, VfxDefinition def, float now)
        {
            var light = inst.Light;
            if (light == null || !light.enabled) return true;

            float envelope;
            if (def.LightLooping)
            {
                envelope = 1f;
                if (inst.Stopping) envelope = Mathf.Clamp01(1f - (now - inst.StopTime) / 0.25f);
                if (def.LightFlicker > 0f)
                {
                    float n = Mathf.PerlinNoise(inst.LightSeed, (now - inst.StartTime) * 9f);
                    envelope *= 1f - def.LightFlicker + def.LightFlicker * 2f * n;
                }
            }
            else
            {
                float t = (now - inst.StartTime) / Mathf.Max(0.01f, def.LightFlashDuration);
                envelope = t >= 1f ? 0f : (1f - t) * (1f - t); // fast attack, quadratic decay
            }

            if (envelope <= 0.001f)
            {
                light.enabled = false;
                return true;
            }
            light.intensity = inst.LightBaseIntensity * inst.Scale * envelope;
            return false;
        }

        // ------------------------------------------------------------------ automatic effects (GameEvents)

        private void OnBallHitPlayer(BallHitPlayerEvent e)
        {
            if (!reactToGameEvents || e.Outcome == HitOutcome.Ignored) return;
            float t = Mathf.InverseLerp(hitSpeedRangeKmh.x, hitSpeedRangeKmh.y, e.SpeedKmh);
            float scale = Mathf.Lerp(hitScaleRange.x, hitScaleRange.y, t);
            if (e.Outcome == HitOutcome.Negated) scale *= negatedHitScale;

            // The puff blows out of the contact along the surface normal (back towards the thrower).
            Vector3 normal = e.Normal;
            if (normal.sqrMagnitude < 1e-4f) normal = e.BallVelocity.sqrMagnitude > 1e-4f ? -e.BallVelocity : Vector3.up;
            Play(VfxId.HitImpact, e.Point, NormalRotation(normal), scale);
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (!reactToGameEvents) return;
            if (e.Quality == CatchQuality.Perfect)
            {
                Play(VfxId.PerfectCatchBurst, e.Point, Quaternion.identity, 1f);
            }
            else if (e.Quality == CatchQuality.Normal)
            {
                float t = Mathf.InverseLerp(hitSpeedRangeKmh.x, hitSpeedRangeKmh.y, e.SpeedKmh);
                Play(VfxId.CatchPuff, e.Point, Quaternion.identity, Mathf.Lerp(0.8f, 1.35f, t));
            }
        }

        private void OnBallBounced(BallBouncedEvent e)
        {
            if (!reactToGameEvents || !e.HitFloor || e.ImpactSpeed < floorDustMinSpeed) return;
            float t = Mathf.InverseLerp(floorDustMinSpeed, floorDustFullSpeed, e.ImpactSpeed);
            Vector3 normal = e.Normal.sqrMagnitude > 1e-4f ? e.Normal : Vector3.up;
            Play(VfxId.FloorImpactDust, e.Point, NormalRotation(normal), Mathf.Lerp(floorDustScaleRange.x, floorDustScaleRange.y, t));
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            if (!reactToGameEvents || e.IsPass || e.SpeedKmh < throwWakeMinKmh || e.Velocity.sqrMagnitude < 1e-4f) return;
            float t = Mathf.InverseLerp(throwWakeMinKmh, GameConstants.MaxBallSpeedKmh, e.SpeedKmh);
            Play(VfxId.ThrowWhoosh, e.Origin, Quaternion.LookRotation(e.Velocity.normalized, Vector3.up), Mathf.Lerp(0.8f, 1.4f, t));
        }

        private void OnPlayerEliminated(PlayerEliminatedEvent e)
        {
            if (!reactToGameEvents || e.Player == null) return;
            Vector3 point = e.Point.sqrMagnitude > 1e-6f ? e.Point : e.Player.ChestPosition;
            // The dust blasts back against the impulse (towards the attacker); straight up if there is none.
            Vector3 normal = e.Impulse.sqrMagnitude > 1e-4f ? -e.Impulse : Vector3.up;
            Play(VfxId.EliminationBurst, point, NormalRotation(normal), 1f);
            StopSlideDust(e.Player);
        }

        private void OnPlayerRevived(PlayerRevivedEvent e)
        {
            if (!reactToGameEvents || e.Player == null) return;
            PlayAttached(VfxId.ReviveBeam, e.Player.transform, Vector3.zero, 1f, null, 0f);
        }

        private void OnUltimateChargeChanged(UltimateChargeChangedEvent e)
        {
            if (!reactToGameEvents || !e.BecameReady || e.Player == null) return;
            Color theme = e.Player.Character != null ? e.Player.Character.themeColor : Color.white;
            // Keep it tasteful: blend the theme colour towards white so it reads as light, not paint.
            Color tint = Color.Lerp(Color.white, theme, ultimateReadyTintStrength);
            tint.a = 1f;
            PlayAttached(VfxId.UltimateReady, e.Player.transform, Vector3.zero, 1f, tint, 0f);
        }

        private void OnPlayerStateChanged(PlayerStateChangedEvent e)
        {
            if (!reactToGameEvents || e.Player == null) return;
            var player = e.Player;

            if (autoSlideDust)
            {
                if (e.Current == PlayerStateId.Sliding && e.Previous != PlayerStateId.Sliding)
                {
                    StopSlideDust(player);
                    var handle = PlayAttached(VfxId.SlideDust, player.transform, new Vector3(0f, 0.03f, 0f), 1f, null, slideDustMaxDuration);
                    if (handle.IsValid) _slideEffects[player] = handle;
                }
                else if (e.Previous == PlayerStateId.Sliding && e.Current != PlayerStateId.Sliding)
                {
                    StopSlideDust(player);
                }
            }

            if (autoLandingDust && e.Previous == PlayerStateId.Airborne && e.Current != PlayerStateId.Airborne &&
                e.Current != PlayerStateId.Incapacitated)
            {
                Play(VfxId.LandingDust, player.Position, Quaternion.identity, landingDustScale);
            }
        }

        private void StopSlideDust(DodgeballPlayer player)
        {
            if (player == null || !_slideEffects.TryGetValue(player, out var handle)) return;
            _slideEffects.Remove(player);
            Stop(handle);
        }
    }
}
