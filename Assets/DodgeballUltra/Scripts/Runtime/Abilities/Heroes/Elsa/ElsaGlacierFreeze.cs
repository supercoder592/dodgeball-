using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Elsa skill [Glacier Freeze] (CD 13 s): conjures and hurls a supercooled ball (<see cref="BallStyle.Freeze"/>,
    /// 1.15x a full-charge throw) through the normal throw pipeline (<see cref="AbilityUtil.ThrowAbilityBall"/>: soft-lock,
    /// lead targeting, BallThrownEvent, Frost Trail). Its <see cref="ElsaFreezePayload"/> turns a landed hit into a
    /// 2.5 s freeze instead of damage: the victim cannot move or catch, allies cannot thaw them, and any further ball hit
    /// during the freeze eliminates them.
    /// </summary>
    [Serializable]
    public sealed class ElsaGlacierFreeze : AbilityBase
    {
        [Header("Freeze")]
        [Tooltip("Seconds a hit enemy stays frozen. Spec: 2.5 s.")]
        [Range(0.5f, 6f)] public float freezeDuration = 2.5f;

        [Header("Projectile")]
        [Tooltip("Speed multiplier on top of Elsa's full-charge throw. Spec default 1.15.")]
        [Range(0.5f, 2f)] public float speedMultiplier = 1.15f;

        [Tooltip("Gravity multiplier of the freeze ball (lower = flatter).")]
        [Range(0f, 1.5f)] public float gravityScale = 0.5f;

        [Tooltip("Collision radius multiplier of the freeze ball.")]
        [Range(0.8f, 1.6f)] public float radiusMultiplier = 1f;

        [Header("Presentation")]
        [Tooltip("Tint of the ice effects (IceBurst / FrozenMist).")]
        public Color iceTint = new Color(0.72f, 0.88f, 1f, 1f);

        [Tooltip("Scale of the IceBurst when the freeze lands.")]
        [Range(0.2f, 3f)] public float impactBurstScale = 1f;

        [Tooltip("Scale of the frost puff at Elsa's hand on release.")]
        [Range(0f, 2f)] public float castBurstScale = 0.4f;

        [Header("AI")]
        [Tooltip("Bots only throw the freeze ball at enemies closer than this (m).")]
        [Min(1f)] public float aiMaxRange = 20f;

        [NonSerialized] private DodgeBall _lastBall;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public ElsaGlacierFreeze()
        {
        }

        /// <summary>The most recent freeze ball (may be recycled already).</summary>
        public DodgeBall LastBall => _lastBall;

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            // Without a ball manager there is nothing to throw: fail instead of burning the cooldown.
            if (BallManager.Instance == null || Owner.Combat == null)
            {
                reason = AbilityFailReason.Custom;
                return false;
            }
            reason = AbilityFailReason.None;
            return true;
        }

        protected override void OnCast()
        {
            var payload = new ElsaFreezePayload(Owner, freezeDuration, iceTint, impactBurstScale);
            var options = AbilityThrowOptions.Default(BallStyle.Freeze);
            options.SpeedMultiplier = speedMultiplier;
            options.GravityScale = gravityScale;
            options.RadiusMultiplier = radiusMultiplier;
            options.Payload = payload;

            _lastBall = AbilityUtil.ThrowAbilityBall(Owner, options);

            Vector3 hand = Owner.Combat != null ? Owner.Combat.GetThrowOrigin() : Owner.ChestPosition;
            if (castBurstScale > 0f) VfxManager.Spawn(VfxId.IceBurst, hand, Quaternion.LookRotation(Owner.Forward), castBurstScale, iceTint);
            AudioManager.PlayAt(SfxId.AbilityCast, hand, 0.9f);
            AudioManager.PlayAt(SfxId.Freeze, hand, 0.5f, 1.2f);
        }

        protected override void OnEnd(bool interrupted)
        {
            // The projectile is autonomous once thrown (its payload cleans up after itself).
            _lastBall = null;
        }

        protected override void OnRoundReset() => _lastBall = null;

        /// <summary>
        /// Loves frozen targets (the next hit eliminates them - a freeze ball on a frozen enemy is a guaranteed kill), likes
        /// enemies holding or charging a ball (freezing cancels their throw), ignores far or absent enemies.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            var enemy = ctx.NearestEnemy;
            if (Data == null || enemy == null || ctx.NearestEnemyDistance > aiMaxRange) return 0f;

            float u = 0.45f;
            if (enemy.Status != null && enemy.Status.Has(StatusEffectType.Frozen)) u = 1f;
            else if (enemy.Status != null && enemy.Status.Has(StatusEffectType.Invulnerable)) u = 0.05f;
            else if (enemy.Combat != null && enemy.Combat.IsCharging) u = 0.8f;
            else if (enemy.Combat != null && enemy.Combat.HasBall) u = 0.65f;

            // Closer = easier to land (the freeze ball is fast but not a beam).
            u *= Mathf.Lerp(1f, 0.6f, Mathf.InverseLerp(6f, aiMaxRange, ctx.NearestEnemyDistance));
            return Mathf.Clamp01(u * (0.5f + Data.aiWeight));
        }
    }
}
