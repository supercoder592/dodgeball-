using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Shadow's skill <b>[Night Parade]</b>: "spawns 2 active clones performing identical throw/catch animations; clones vanish
    /// on ball impact (CD 12 s)".
    /// <para>
    /// Two animated <see cref="ShadowClone"/>s flank Shadow at ±<see cref="flankSpacing"/> m along his right vector. The
    /// formation follows with a slight lag (exponential easing), always clamped to Shadow's own court zone. Each clone
    /// copies Shadow's animator every frame and mirrors his upper-body pose after IK, so throws and catch reaches look
    /// identical; when Shadow throws, every clone throws a harmless <see cref="ShadowIllusionBall"/> alongside the real one
    /// (vanishes after 0.6 s) - catchers have to guess which ball is real. Enemy balls burst a clone (Absorb + CloneDissolve).
    /// </para>
    /// <para>Duration (6 s) and cooldown (12 s) come from <see cref="AbilityData"/>. The ability ends early if every clone popped.</para>
    /// </summary>
    [Serializable]
    public sealed class ShadowNightParade : AbilityBase
    {
        /// <summary>Used only if AbilityData.duration is left at 0 (misconfigured asset): the spec duration.</summary>
        private const float FallbackDuration = 6f;

        [Header("Formation")]
        [Tooltip("Number of clones. Spec: 2 (one on each flank).")]
        [Range(1, 4)] public int cloneCount = 2;

        [Tooltip("Lateral distance (m) between Shadow and each flanking clone. Spec: 1.4 m.")]
        [Range(0.6f, 4f)] public float flankSpacing = 1.4f;

        [Tooltip("How tightly the clones follow their slots (1/s). Lower = more lag.")]
        [Range(1f, 30f)] public float followSharpness = 9f;

        [Header("Mimicry")]
        [Tooltip("Life (s) of the illusion balls clones throw when Shadow throws. Spec: 0.6 s.")]
        [Range(0.1f, 2f)] public float illusionBallLifetime = 0.6f;

        [Tooltip("Clones also mirror Shadow's catch attempts.")]
        public bool mirrorCatches = true;

        [Header("AI")]
        [Tooltip("Bots consider the parade when an enemy is within this distance (m).")]
        [Min(2f)] public float aiEngageRange = 16f;

        [NonSerialized] private List<ShadowClone> _clones;
        [NonSerialized] private List<int> _slots;      // formation slot of each clone (parallel to _clones)
        [NonSerialized] private float[] _offsets;      // lateral offset (m) of each slot

        /// <summary>Live clones of the current parade (read-only view for UI / AI).</summary>
        public IReadOnlyList<ShadowClone> Clones => _clones;

        public ShadowNightParade() { }

        protected override void OnInitialize()
        {
            _clones = new List<ShadowClone>(4);
            _slots = new List<int>(4);
        }

        protected override void OnEquip()
        {
            Listen<BallThrownEvent>(OnBallThrown);
            Listen<CatchAttemptEvent>(OnCatchAttempt);
        }

        protected override void OnUnequip() => DissolveAll(false);

        protected override void OnCast()
        {
            DissolveAll(false);
            if (Duration <= 0f) ExtendActive(FallbackDuration);

            int count = Mathf.Clamp(cloneCount, 1, 4);
            if (_offsets == null || _offsets.Length != count) _offsets = new float[count];

            Vector3 right = PlanarRight(Owner);
            for (int i = 0; i < count; i++)
            {
                // Alternate sides: +1, -1, +2, -2 ... flank spacings.
                float side = (i % 2 == 0) ? 1f : -1f;
                _offsets[i] = side * (i / 2 + 1) * flankSpacing;

                // Clones step out of Shadow's body and ease into their slot.
                Vector3 slot = SlotPosition(right, _offsets[i]);
                var clone = ShadowClone.SpawnAnimated(Owner, Owner, Vector3.Lerp(Owner.Position, slot, 0.35f), Owner.Rotation, 0f);
                if (clone == null) continue;
                clone.FollowSharpness = followSharpness;
                clone.SetDesiredPose(slot, Owner.Rotation);
                _clones.Add(clone);
                _slots.Add(i);
            }

            if (_clones.Count == 0)
            {
                EndAbility(); // no visual to copy (headless test / missing model)
                return;
            }

            AudioManager.PlayAt(SfxId.AbilityCast, Owner.ChestPosition, 0.8f, 0.9f);
        }

        protected override void OnTick(float deltaTime)
        {
            PruneDead();
            if (_clones.Count == 0)
            {
                EndAbility(); // every clone was burst by a ball
                return;
            }

            Vector3 right = PlanarRight(Owner);
            Quaternion facing = Owner.Rotation;
            for (int i = 0; i < _clones.Count; i++)
            {
                // Each clone keeps its own flank even after a sibling popped.
                _clones[i].SetDesiredPose(SlotPosition(right, _offsets[_slots[i]]), facing, followSharpness);
            }
        }

        protected override void OnInterrupt(InterruptReason reason) => DissolveAll(true);

        protected override void OnEnd(bool interrupted) => DissolveAll(true);

        protected override void OnRoundReset() => DissolveAll(false);

        /// <summary>
        /// Most valuable when Shadow is about to throw at a nearby enemy (two decoy balls make the catch a guess) or when
        /// enemies hold balls and Shadow needs bodies to soak throws.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || ctx.NearestEnemy == null) return 0f;
            float w = Data.aiWeight;
            float utility = 0f;

            if (ctx.HoldingBall && ctx.NearestEnemyDistance <= aiEngageRange) utility = w * 0.8f;

            int armedEnemies = CountEnemiesHoldingBalls();
            if (armedEnemies > 0) utility = Mathf.Max(utility, w * (0.4f + 0.15f * armedEnemies));
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact > 0.3f && ctx.IncomingTimeToImpact < 1.2f)
                utility = Mathf.Max(utility, w * 0.5f);

            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ events

        private void OnBallThrown(BallThrownEvent e)
        {
            if (!IsActive || e.Thrower != Owner || e.IsAbilityThrow || e.Ball == null) return;
            for (int i = 0; i < _clones.Count; i++)
            {
                var clone = _clones[i];
                if (clone != null && clone.IsAlive) clone.MirrorThrow(e.Origin, e.Velocity, e.Ball.GravityScale, illusionBallLifetime);
            }
        }

        private void OnCatchAttempt(CatchAttemptEvent e)
        {
            if (!mirrorCatches || !IsActive || e.Player != Owner) return;
            for (int i = 0; i < _clones.Count; i++)
            {
                var clone = _clones[i];
                if (clone != null && clone.IsAlive) clone.MirrorCatch();
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Formation slot for a lateral offset, clamped to Shadow's zone, at Shadow's height (mirrors jumps).</summary>
        private Vector3 SlotPosition(Vector3 right, float lateralOffset)
        {
            Vector3 p = AbilityUtil.ClampToPlayerZone(Owner, Owner.Position + right * lateralOffset);
            p.y = Owner.Position.y;
            return p;
        }

        private void PruneDead()
        {
            for (int i = _clones.Count - 1; i >= 0; i--)
            {
                var c = _clones[i];
                if (c != null && c.IsAlive) continue;
                _clones.RemoveAt(i);
                _slots.RemoveAt(i);
            }
        }

        private void DissolveAll(bool effects)
        {
            if (_clones == null) return;
            for (int i = 0; i < _clones.Count; i++)
            {
                var c = _clones[i];
                if (c != null) c.Dissolve(effects);
            }
            _clones.Clear();
            _slots.Clear();
        }

        private int CountEnemiesHoldingBalls()
        {
            int n = 0;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && IsEnemy(p) && p.Combat != null && p.Combat.HasBall) n++;
            }
            return n;
        }

        private static Vector3 PlanarRight(DodgeballPlayer player)
        {
            Vector3 r = player.transform.right;
            r.y = 0f;
            return r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
        }
    }
}
