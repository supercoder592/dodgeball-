using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Characters;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>What a <see cref="ShadowClone"/> is made of.</summary>
    public enum ShadowCloneKind
    {
        /// <summary>A fully animated copy of a player's realistic model that follows a formation (Night Parade, Mirage Formation).</summary>
        Animated = 0,

        /// <summary>A static, fading afterimage baked from the current pose (Decoy Dash).</summary>
        Afterimage = 1,
    }

    /// <summary>
    /// A Shadow illusion on the court: a realistic body that looks exactly like the player it mimics, with a hittable
    /// capsule on <see cref="GameLayers.Hittable"/>. Enemy balls that touch it make it pop (CloneDissolve smoke) and are
    /// absorbed (they drop dead at the contact point); piercing balls pop it and keep going.
    /// <para>
    /// Animated clones copy the mimicked player's animator parameters every frame
    /// (<see cref="CharacterVisual.CopyAnimatorStateTo"/>), drive their own locomotion speed from their actual movement (no
    /// foot sliding while shuffling), and mirror the upper-body pose of the real player after IK every LateUpdate, so the
    /// procedural throw wind-up and catch reach are reproduced exactly. <see cref="MirrorThrow"/> launches a harmless
    /// <see cref="ShadowIllusionBall"/> alongside the real one.
    /// </para>
    /// <para>
    /// <b>AI:</b> <see cref="ActiveClones"/> lists every live illusion so bots can be fooled the same way humans are:
    /// use <see cref="PickPerceivedAimPoint"/> when aiming at a player that has clones.
    /// </para>
    /// Hierarchy: <c>ShadowClone (this, CapsuleCollider, kinematic Rigidbody; layer Hittable) └ model / afterimage</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ShadowClone : MonoBehaviour, IBallHittable
    {
        // ------------------------------------------------------------------ registry

        private static readonly List<ShadowClone> s_active = new List<ShadowClone>(16);

        /// <summary>Every live Shadow illusion (animated clones and Decoy Dash afterimages).</summary>
        public static IReadOnlyList<ShadowClone> ActiveClones => s_active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_active.Clear();

        /// <summary>Bones whose local rotation is mirrored from the real player (spine up + arms: throw / catch IK).</summary>
        private static readonly HumanBodyBones[] s_mirroredBones =
        {
            HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        // ------------------------------------------------------------------ tuning

        [Header("Motion")]
        [Tooltip("Exponential follow sharpness (1/s) toward the desired pose. Lower = more lag behind the formation slot.")]
        [SerializeField, Min(0.1f)] private float followSharpness = 10f;

        [Tooltip("Exponential turn sharpness (1/s).")]
        [SerializeField, Min(0.1f)] private float turnSharpness = 12f;

        [Tooltip("Face the direction of travel when the clone moves this much faster (m/s) than the player it mimics " +
                 "(e.g. while shuffling), so it runs instead of gliding sideways.")]
        [SerializeField, Min(0f)] private float faceTravelSpeedMargin = 1.2f;

        [Header("Feedback")]
        [Tooltip("Tint of the smoke when the illusion dissolves.")]
        [SerializeField] private Color dissolveTint = new Color(0.3f, 0.28f, 0.38f, 1f);

        [Tooltip("Volume of the pop sound when a ball bursts the illusion.")]
        [SerializeField, Range(0f, 1f)] private float popVolume = 0.8f;

        [Tooltip("Camera shake amplitude when a ball bursts the illusion (distance falloff applies).")]
        [SerializeField, Range(0f, 1f)] private float popShake = 0.12f;

        [Header("Safety")]
        [Tooltip("Hard cap (s) for illusions whose lifetime is controlled by an ability, in case that ability is torn down " +
                 "without dissolving them (never reached in normal play: the longest illusion ability lasts 6 s).")]
        [SerializeField, Min(1f)] private float maxAbilityControlledLifetime = 20f;

        // ------------------------------------------------------------------ state

        private DodgeballPlayer _mimic;
        private DodgeballPlayer _caster;
        private bool _hadCaster;
        private TeamId _team = TeamId.None;
        private ShadowCloneKind _kind;
        private CapsuleCollider _collider;
        private Animator _animator;
        private Transform[] _sourceBones;
        private Transform[] _cloneBones;
        private int _boneCount;
        private bool _hasSpeedParam;
        private bool _hasThrowTrigger;
        private bool _hasCatchTrigger;
        private Vector3 _desiredPosition;
        private Quaternion _desiredRotation = Quaternion.identity;
        private float _sharpnessOverride = -1f;
        private float _age;
        private float _lifetime;
        private float _armDistance;
        private float _chestHeight = 1.3f;
        private float _planarSpeed;
        private Vector3 _travelDirection;

        /// <summary>The player this illusion looks like.</summary>
        public DodgeballPlayer Mimic => _mimic;

        /// <summary>The Shadow who created it.</summary>
        public DodgeballPlayer Caster => _caster;

        public ShadowCloneKind Kind => _kind;

        /// <summary>Team of the mimicked player: that team's balls pass through the illusion.</summary>
        public TeamId OwnerTeam => _team;

        /// <summary>False once popped, dissolved or expired.</summary>
        public bool IsAlive { get; private set; }

        /// <summary>True while enemy balls can burst it.</summary>
        public bool IsHittable => IsAlive && _collider != null && _collider.enabled;

        public Vector3 Position => transform.position;

        /// <summary>Chest-height point (what an aiming thrower would target).</summary>
        public Vector3 ChestPosition => transform.position + Vector3.up * _chestHeight;

        /// <summary>The clone's animator (Animated clones only).</summary>
        public Animator Animator => _animator;

        /// <summary>Seconds since spawn (scaled time).</summary>
        public float Age => _age;

        /// <summary>Remaining life (s); +inf when the owner ability controls the lifetime.</summary>
        public float RemainingLifetime => _lifetime > 0f ? Mathf.Max(0f, _lifetime - _age) : float.PositiveInfinity;

        /// <summary>Estimated planar speed of the clone (m/s).</summary>
        public float PlanarSpeed => _planarSpeed;

        public float FollowSharpness
        {
            get => followSharpness;
            set => followSharpness = Mathf.Max(0.1f, value);
        }

        /// <summary>(clone, poppedByBall) - raised once when the illusion disappears for any reason.</summary>
        public event Action<ShadowClone, bool> Vanished;

        // ------------------------------------------------------------------ spawning

        /// <summary>
        /// Spawns an animated copy of <paramref name="mimic"/> at <paramref name="position"/>. Returns null when the mimic has no
        /// visual to copy. <paramref name="lifetime"/> &lt;= 0 means the owning ability dissolves it explicitly.
        /// </summary>
        public static ShadowClone SpawnAnimated(DodgeballPlayer mimic, DodgeballPlayer caster, Vector3 position, Quaternion rotation,
            float lifetime, bool playSpawnEffects = true)
        {
            if (mimic == null || mimic.Visual == null) return null;

            var model = mimic.Visual.CreateAnimatedClone(mimic.DisplayName + " (Illusion)");
            if (model == null) return null;

            var root = new GameObject("ShadowClone_" + mimic.DisplayName) { layer = GameLayers.Hittable };
            root.transform.SetPositionAndRotation(position, rotation);
            var clone = root.AddComponent<ShadowClone>();
            clone.Setup(mimic, caster, ShadowCloneKind.Animated, lifetime);

            // Keep the model's own local scale (hero modelScale); feet on the root.
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            clone.BindModel(model);
            clone.SetArmed(true);

            if (playSpawnEffects)
            {
                VfxManager.Spawn(VfxId.CloneSpawn, clone.ChestPosition, rotation, 1f, clone.dissolveTint);
                AudioManager.PlayAt(SfxId.Clone, position, 0.7f, UnityEngine.Random.Range(0.95f, 1.05f));
            }
            return clone;
        }

        /// <summary>
        /// Wraps a fading afterimage (from <see cref="CharacterVisual.SpawnAfterimage"/>) into a hittable decoy that lives
        /// <paramref name="lifetime"/> seconds. The decoy only becomes hittable once <paramref name="mimic"/> is at least
        /// <paramref name="armDistance"/> metres away, so it never shields the real body it was just baked from.
        /// </summary>
        public static ShadowClone SpawnDecoy(DodgeballPlayer mimic, GameObject afterimage, float lifetime, float armDistance)
        {
            if (mimic == null) return null;

            var root = new GameObject("ShadowDecoy_" + mimic.DisplayName) { layer = GameLayers.Hittable };
            root.transform.SetPositionAndRotation(mimic.Position, mimic.Rotation);
            var clone = root.AddComponent<ShadowClone>();
            clone.Setup(mimic, mimic, ShadowCloneKind.Afterimage, Mathf.Max(0.05f, lifetime));
            clone._armDistance = Mathf.Max(0f, armDistance);
            if (afterimage != null)
            {
                afterimage.transform.SetParent(root.transform, true);
                DisableColliders(afterimage);
            }
            clone.SetArmed(clone._armDistance <= 0f);
            return clone;
        }

        // ------------------------------------------------------------------ control (called by the owning ability)

        /// <summary>Where the clone should be. It eases there with <see cref="FollowSharpness"/> (or <paramref name="sharpness"/>).</summary>
        public void SetDesiredPose(Vector3 position, Quaternion rotation, float sharpness = -1f)
        {
            _desiredPosition = position;
            _desiredRotation = rotation;
            _sharpnessOverride = sharpness;
        }

        /// <summary>Teleports the clone (no easing).</summary>
        public void SnapTo(Vector3 position, Quaternion rotation)
        {
            _desiredPosition = position;
            _desiredRotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            _planarSpeed = 0f;
        }

        /// <summary>
        /// Mirrors a throw of the real player: plays the throw on the clone and launches a harmless illusion ball from the
        /// clone's hand with the same velocity (relative to facing) that vanishes after <paramref name="illusionLifetime"/>.
        /// </summary>
        public void MirrorThrow(Vector3 realReleasePoint, Vector3 realVelocity, float gravityScale, float illusionLifetime)
        {
            if (!IsAlive || _kind != ShadowCloneKind.Animated || _mimic == null) return;

            if (_hasThrowTrigger && _animator != null) _animator.SetTrigger(AnimatorParams.ThrowHash);

            // Same release point and throw direction relative to the body (yaw only).
            Quaternion yawDelta = YawOnly(transform.rotation) * Quaternion.Inverse(YawOnly(_mimic.Rotation));
            Vector3 localRelease = Quaternion.Inverse(YawOnly(_mimic.Rotation)) * (realReleasePoint - _mimic.Position);
            Vector3 origin = transform.position + YawOnly(transform.rotation) * localRelease;
            ShadowIllusionBall.Spawn(origin, yawDelta * realVelocity, gravityScale, illusionLifetime);
        }

        /// <summary>Mirrors a catch attempt (the arm reach itself is mirrored from the real pose every frame).</summary>
        public void MirrorCatch()
        {
            if (!IsAlive || _animator == null || !_hasCatchTrigger) return;
            _animator.SetTrigger(AnimatorParams.CatchHash);
        }

        /// <summary>A ball burst the illusion: smoke, pop sound, tiny shake, gone.</summary>
        public void Pop(Vector3 point)
        {
            if (!IsAlive) return;
            VfxManager.Spawn(VfxId.CloneDissolve, ChestPosition, transform.rotation, 1f, dissolveTint);
            AudioManager.PlayAt(SfxId.Clone, point, popVolume, UnityEngine.Random.Range(1.1f, 1.25f));
            var juice = JuiceManager.Instance;
            if (juice != null && popShake > 0f) juice.Shake(popShake, 18f, 0.12f, point);
            Kill(true);
        }

        /// <summary>The owning ability ended: fade into smoke (optionally silent, e.g. round reset).</summary>
        public void Dissolve(bool playEffects = true)
        {
            if (!IsAlive) return;
            if (playEffects)
            {
                VfxManager.Spawn(VfxId.CloneDissolve, ChestPosition, transform.rotation, 0.8f, dissolveTint);
                AudioManager.PlayAt(SfxId.Clone, transform.position, popVolume * 0.4f, 0.9f);
            }
            Kill(false);
        }

        // ------------------------------------------------------------------ IBallHittable

        public BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit)
        {
            if (!IsHittable || ball == null || ball.IsPass) return BallHitResponse.PassThrough;

            // Friendly (and thrower-less) balls ignore the illusion so it never steals a teammate's throw or pass.
            TeamId thrower = ball.ThrowerTeam;
            if (!thrower.IsValid() || thrower == _team) return BallHitResponse.PassThrough;

            Vector3 point = hit.distance > 0f || hit.point != Vector3.zero ? hit.point : ChestPosition;
            Pop(point);

            // Piercing balls (Rayne's beam) burst the illusion and keep flying; everything else is swallowed and drops.
            return ball.Pierce ? BallHitResponse.PassThrough : BallHitResponse.Absorb;
        }

        // ------------------------------------------------------------------ AI helpers (allocation-free)

        /// <summary>Number of live, hittable illusions that look like <paramref name="player"/>.</summary>
        public static int CountClonesOf(DodgeballPlayer player)
        {
            if (player == null) return 0;
            int n = 0;
            for (int i = 0; i < s_active.Count; i++)
            {
                var c = s_active[i];
                if (c != null && c.IsHittable && c._mimic == player) n++;
            }
            return n;
        }

        /// <summary>Fills <paramref name="results"/> with the live illusions of <paramref name="player"/>. Returns the count.</summary>
        public static int GetClonesOf(DodgeballPlayer player, List<ShadowClone> results)
        {
            if (results == null) return 0;
            results.Clear();
            if (player == null) return 0;
            for (int i = 0; i < s_active.Count; i++)
            {
                var c = s_active[i];
                if (c != null && c.IsHittable && c._mimic == player) results.Add(c);
            }
            return results.Count;
        }

        /// <summary>
        /// What an observer who cannot tell the bodies apart would aim at: the real chest or one of the illusions', chosen
        /// uniformly by <paramref name="random01"/> (0..1). Returns true when an illusion was picked.
        /// </summary>
        public static bool PickPerceivedAimPoint(DodgeballPlayer target, float random01, out Vector3 aimPoint)
        {
            aimPoint = target != null ? target.ChestPosition : Vector3.zero;
            int clones = CountClonesOf(target);
            if (clones == 0) return false;

            int pick = Mathf.Min(clones, Mathf.FloorToInt(Mathf.Clamp01(random01) * (clones + 1)));
            if (pick == clones) return false; // the real one

            for (int i = 0, n = 0; i < s_active.Count; i++)
            {
                var c = s_active[i];
                if (c == null || !c.IsHittable || c._mimic != target) continue;
                if (n++ == pick)
                {
                    aimPoint = c.ChestPosition;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Nearest live illusion belonging to <paramref name="team"/> within <paramref name="maxDistance"/> (or null).</summary>
        public static ShadowClone FindNearest(Vector3 position, TeamId team, float maxDistance = float.PositiveInfinity)
        {
            ShadowClone best = null;
            float bestSqr = maxDistance * maxDistance;
            for (int i = 0; i < s_active.Count; i++)
            {
                var c = s_active[i];
                if (c == null || !c.IsHittable || c._team != team) continue;
                float d = (c.transform.position - position).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = c;
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ Unity

        private void OnEnable()
        {
            if (IsAlive && !s_active.Contains(this)) s_active.Add(this);
        }

        private void OnDisable() => s_active.Remove(this);

        private void OnDestroy()
        {
            s_active.Remove(this);
            IsAlive = false;
        }

        private void Update()
        {
            if (!IsAlive) return;

            float dt = Time.deltaTime; // scaled: illusions freeze during hitstop like everything else in play
            _age += dt;

            if (_lifetime > 0f && _age >= _lifetime)
            {
                // Afterimages fade out on their own; animated clones dissolve into smoke.
                if (_kind == ShadowCloneKind.Animated) Dissolve();
                else Kill(false);
                return;
            }

            // Orphaned (mimic or caster destroyed, or the owning ability never dissolved it): fade out.
            if (_mimic == null || (_hadCaster && _caster == null) || (_lifetime <= 0f && _age >= maxAbilityControlledLifetime))
            {
                Dissolve();
                return;
            }

            if (_kind == ShadowCloneKind.Afterimage)
            {
                if (!_collider.enabled)
                {
                    Vector3 d = _mimic.Position - transform.position;
                    d.y = 0f;
                    if (d.sqrMagnitude >= _armDistance * _armDistance) SetArmed(true);
                }
                return;
            }

            TickFollow(dt);
            TickAnimator();
        }

        private void LateUpdate()
        {
            // Upper-body pose mirroring after both animators (and the real player's IK) evaluated this frame.
            if (!IsAlive || _boneCount == 0) return;
            for (int i = 0; i < _boneCount; i++)
            {
                var src = _sourceBones[i];
                var dst = _cloneBones[i];
                if (src != null && dst != null) dst.localRotation = src.localRotation;
            }
        }

        // ------------------------------------------------------------------ internals

        private void Setup(DodgeballPlayer mimic, DodgeballPlayer caster, ShadowCloneKind kind, float lifetime)
        {
            _mimic = mimic;
            _caster = caster;
            _hadCaster = caster != null;
            _team = mimic.Team;
            _kind = kind;
            _lifetime = lifetime;
            _desiredPosition = transform.position;
            _desiredRotation = transform.rotation;

            // Same body volume as the real player: the illusion is exactly as easy to hit.
            float height = mimic.Capsule != null ? mimic.Capsule.height : 1.8f;
            float radius = mimic.Capsule != null ? mimic.Capsule.radius : 0.32f;
            _chestHeight = height * 0.72f;

            _collider = gameObject.AddComponent<CapsuleCollider>();
            _collider.direction = 1;
            _collider.height = height;
            _collider.radius = radius;
            _collider.center = new Vector3(0f, height * 0.5f, 0f);

            // Moving collider: kinematic body keeps the physics scene cheap to update.
            var body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            IsAlive = true;
            if (isActiveAndEnabled && !s_active.Contains(this)) s_active.Add(this);
        }

        private void BindModel(GameObject model)
        {
            DisableColliders(model);

            _animator = model.GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false; // the ability drives the position
                if (_animator.runtimeAnimatorController != null)
                {
                    // One-time parameter scan (allocates once at spawn, never per frame).
                    var parameters = _animator.parameters;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        int hash = parameters[i].nameHash;
                        if (hash == AnimatorParams.SpeedHash) _hasSpeedParam = true;
                        else if (hash == AnimatorParams.ThrowHash) _hasThrowTrigger = true;
                        else if (hash == AnimatorParams.CatchHash) _hasCatchTrigger = true;
                    }
                }
            }

            // Pair up the bones used for pose mirroring (both are the same Humanoid avatar).
            var visual = _mimic.Visual;
            if (_animator == null || !_animator.isHuman || visual == null || !visual.HasHumanoidModel) return;

            _sourceBones = new Transform[s_mirroredBones.Length];
            _cloneBones = new Transform[s_mirroredBones.Length];
            for (int i = 0; i < s_mirroredBones.Length; i++)
            {
                var src = visual.GetBone(s_mirroredBones[i]);
                var dst = _animator.GetBoneTransform(s_mirroredBones[i]);
                if (src == null || dst == null) continue;
                _sourceBones[_boneCount] = src;
                _cloneBones[_boneCount] = dst;
                _boneCount++;
            }
        }

        private void SetArmed(bool armed)
        {
            if (_collider != null) _collider.enabled = armed;
        }

        private void TickFollow(float dt)
        {
            if (dt <= 0f) return;

            float sharpness = _sharpnessOverride > 0f ? _sharpnessOverride : followSharpness;
            Vector3 previous = transform.position;
            Vector3 next = Vector3.Lerp(previous, _desiredPosition, 1f - Mathf.Exp(-sharpness * dt));

            Vector3 planarStep = next - previous;
            planarStep.y = 0f;
            float speedNow = planarStep.magnitude / dt;
            _planarSpeed = Mathf.Lerp(_planarSpeed, speedNow, 1f - Mathf.Exp(-12f * dt));
            if (planarStep.sqrMagnitude > 1e-8f) _travelDirection = planarStep.normalized;

            // Run toward where it is going when clearly outpacing the real player (formation shuffles).
            Quaternion targetRotation = _desiredRotation;
            float mimicSpeed = _mimic.Motor != null ? _mimic.Motor.PlanarSpeed : 0f;
            if (_planarSpeed > mimicSpeed + faceTravelSpeedMargin && _travelDirection.sqrMagnitude > 0.5f)
            {
                targetRotation = Quaternion.LookRotation(_travelDirection, Vector3.up);
            }
            Quaternion rotation = Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-turnSharpness * dt));

            transform.SetPositionAndRotation(next, rotation);
        }

        private void TickAnimator()
        {
            if (_animator == null || _mimic.Visual == null) return;
            _mimic.Visual.CopyAnimatorStateTo(_animator);
            // Locomotion from the clone's own motion so the feet match the ground it actually covers.
            if (_hasSpeedParam) _animator.SetFloat(AnimatorParams.SpeedHash, _planarSpeed);
        }

        private void Kill(bool popped)
        {
            if (!IsAlive) return;
            IsAlive = false;
            if (_collider != null) _collider.enabled = false;
            s_active.Remove(this);

            var handler = Vanished;
            Vanished = null;
            handler?.Invoke(this, popped);

            Destroy(gameObject);
        }

        private static void DisableColliders(GameObject go)
        {
            // Visual copies must never be mistaken for a real body by ball sweeps or the character motor.
            var colliders = go.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) colliders[i].enabled = false;
        }

        private static Quaternion YawOnly(Quaternion rotation)
        {
            Vector3 f = rotation * Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
        }
    }
}
