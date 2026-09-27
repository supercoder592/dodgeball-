using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Houdini skill [Swap Places] (0.5 s channel, CD 16 s): target an enemy and, after the channel, trade places with them.
    /// <para><b>Targeting.</b> The soft-locked <see cref="PlayerCombatController.CurrentTarget"/>, else the bot's
    /// <see cref="PlayerIntent.DesiredTarget"/>, else the best enemy inside the aim cone
    /// (<see cref="TargetingSystem.FindBestTarget"/>). No valid target =&gt; <see cref="AbilityFailReason.NoTarget"/>
    /// (no cooldown spent). Cloaked enemies cannot be targeted unless revealed.</para>
    /// <para><b>Channel.</b> Houdini is rooted (<see cref="StatusEffectType.Rooted"/>) while a SwapFlash shimmers on both
    /// players - a readable tell. The channel is interrupted if Houdini is stunned or frozen (flash cleared, full cooldown),
    /// and cancelled with a partial cooldown refund if the target stops being valid (eliminated, cloaked, out of range).</para>
    /// <para><b>Design choice - the mirrored, court-relative swap.</b> A literal position swap would put Houdini inside
    /// the enemy half and an enemy inside Houdini's half, breaking the fundamental dodgeball rule that each team stays on its
    /// own side (and the motor confinement would immediately shove both back, producing a jarring pop). Instead each player
    /// takes the other's spot <i>relative to the court</i>:</para>
    /// <code>
    ///   Houdini -> (enemy.x, centre.z + side(Houdini) * |enemy.z   - centre.z|)   // enemy's spot mirrored into Houdini's half
    ///   Enemy   -> (Houdini.x, centre.z + side(enemy) * |Houdini.z - centre.z|)   // Houdini's spot mirrored into the enemy half
    /// </code>
    /// <para>Lateral position (lane) and depth (distance from the centre line) are exchanged, so the trick still reads as a
    /// swap - an aggressive enemy at the centre line gets flung back to their baseline while Houdini takes the front, lanes
    /// cross over - but both stay legal. Both positions are clamped to each player's zone
    /// (<see cref="AbilityUtil.ClampToPlayerZone"/>) and nudged apart from other players; facing is preserved. The enemy
    /// suffers a brief 0.3 s disorientation stun (cancels their charge / catch).</para>
    /// </summary>
    [Serializable]
    public sealed class HoudiniSwapPlaces : AbilityBase
    {
        [Header("Targeting")]
        [Tooltip("Half-angle (deg) of the aim cone searched when nothing is soft-locked.")]
        [Range(5f, 60f)] public float aimConeAngle = 25f;

        [Tooltip("Maximum distance (m) to the swap target.")]
        [Range(3f, 60f)] public float maxRange = 30f;

        [Tooltip("Cloaked (stealthed) enemies can be targeted.")]
        public bool allowCloakedTargets;

        [Header("Swap")]
        [Tooltip("Disorientation stun on the swapped enemy (s). Spec: 0.3 s.")]
        [Range(0f, 2f)] public float disorientStun = 0.3f;

        [Tooltip("Minimum planar distance (m) kept from other players at the arrival points.")]
        [Range(0.3f, 2f)] public float minSeparation = 0.8f;

        [Tooltip("Fraction of the cooldown refunded when the target becomes invalid during the channel.")]
        [Range(0f, 1f)] public float lostTargetRefund = 0.75f;

        [Header("Presentation")]
        [Tooltip("Tint of the SwapFlash / TeleportPoof effects.")]
        public Color flashTint = new Color(0.74f, 0.64f, 0.95f, 1f);

        [Tooltip("Scale of the SwapFlash effects.")]
        [Range(0.2f, 3f)] public float flashScale = 1f;

        [Tooltip("Camera trauma at the moment of the swap (0..1).")]
        [Range(0f, 1f)] public float swapTrauma = 0.3f;

        [Tooltip("Screen warp for the local player when they are swapped (0 = none).")]
        [Range(0f, 1f)] public float localWarpIntensity = 0.45f;

        [Header("AI")]
        [Tooltip("Bots use the swap to dodge a ball arriving no sooner than the channel plus this margin (s).")]
        [Range(0f, 1f)] public float aiDodgeMargin = 0.15f;

        [Tooltip("...and no later than this (s) after now.")]
        [Range(0.5f, 3f)] public float aiDodgeWindow = 1.4f;

        [NonSerialized] private DodgeballPlayer _target;
        [NonSerialized] private VfxHandle _ownerFlash;
        [NonSerialized] private VfxHandle _targetFlash;
        [NonSerialized] private bool _rooted;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public HoudiniSwapPlaces()
        {
        }

        /// <summary>Current (or pending) swap target.</summary>
        public DodgeballPlayer Target => _target;

        // ------------------------------------------------------------------ activation

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            _target = ResolveTarget();
            if (_target == null)
            {
                reason = AbilityFailReason.NoTarget;
                return false;
            }
            reason = AbilityFailReason.None;
            return true;
        }

        protected override void OnCastStarted()
        {
            if (!IsValidTarget(_target, 1f)) _target = ResolveTarget();

            // Rooted for the channel (can still act otherwise). Slightly longer than the channel so there is no gap.
            if (Owner.Status != null)
            {
                Owner.Status.Apply(StatusEffectType.Rooted, CastTime + 0.1f, 1f, this);
                _rooted = true;
            }
            if (Owner.Motor != null) Owner.Motor.SetPlanarVelocity(Vector3.zero);

            float channel = CastTime + 0.1f;
            _ownerFlash = VfxManager.SpawnAttached(VfxId.SwapFlash, Owner.transform, Vector3.up * 1f, flashScale * 0.8f, flashTint, channel);
            if (_target != null)
            {
                _targetFlash = VfxManager.SpawnAttached(VfxId.SwapFlash, _target.transform, Vector3.up * 1f, flashScale * 0.8f, flashTint, channel);
            }
            AudioManager.PlayAt(SfxId.AbilityCast, Owner.Position, 0.8f);
        }

        protected override void OnCastTick(float deltaTime, float progress)
        {
            // Fallback in case the kernel interrupt path did not run (the controller normally calls InterruptAll).
            if (Owner.Status != null && Owner.Status.Has(StatusEffectType.Frozen))
            {
                Interrupt(InterruptReason.Frozen);
                return;
            }
            if (Owner.StateMachine != null && Owner.StateMachine.IsIn(PlayerStateId.Stunned))
            {
                Interrupt(InterruptReason.Stunned);
                return;
            }
            if (!Owner.IsInfield || (Owner.Health != null && !Owner.Health.IsAlive))
            {
                Interrupt(InterruptReason.Eliminated);
                return;
            }

            // The target slipped away (eliminated, cloaked, out of range): cancel with a partial refund.
            if (!IsValidTarget(_target, 1.25f))
            {
                Interrupt(InterruptReason.Cancelled);
                if (lostTargetRefund > 0f) ReduceCooldown(CooldownDuration * lostTargetRefund);
            }
        }

        protected override void OnCast()
        {
            ClearChannel();

            if (!IsValidTarget(_target, 1.25f)) _target = ResolveTarget();
            if (_target == null)
            {
                // Instant-cast configuration (castTime 0) with the target gone: end now, refund most of the cooldown.
                EndAbility();
                if (lostTargetRefund > 0f) ReduceCooldown(CooldownDuration * lostTargetRefund);
                return;
            }

            PerformSwap(Owner, _target);
        }

        protected override void OnInterrupt(InterruptReason reason) => ClearChannel();

        protected override void OnEnd(bool interrupted)
        {
            ClearChannel();
            _target = null;
        }

        protected override void OnRoundReset()
        {
            ClearChannel();
            _target = null;
        }

        protected override void OnUnequip() => ClearChannel();

        /// <summary>
        /// Bots swap (a) to dodge a ball that arrives after the channel completes, (b) to break an enemy's charged throw
        /// (the stun cancels it), (c) occasionally to reshuffle lanes when an enemy is close.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            var enemy = ctx.NearestEnemy;
            if (Data == null || enemy == null || ctx.Self == null || ctx.NearestEnemyDistance > maxRange) return 0f;

            float u = 0f;
            float minTime = CastTime + aiDodgeMargin;
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact >= minTime && ctx.IncomingTimeToImpact <= aiDodgeWindow)
            {
                // Only worth it if the swap really moves us out of the lane.
                if (Mathf.Abs(enemy.Position.x - ctx.Self.Position.x) > 1.2f) u = Mathf.Max(u, 0.75f);
            }
            if (enemy.Combat != null && enemy.Combat.IsCharging) u = Mathf.Max(u, 0.6f);
            if (u <= 0f && ctx.NearestEnemyDistance < 9f) u = 0.15f;

            return Mathf.Clamp01(u * (0.5f + Data.aiWeight));
        }

        // ------------------------------------------------------------------ swap

        /// <summary>Executes the mirrored, court-relative swap (see class docs).</summary>
        private void PerformSwap(DodgeballPlayer houdini, DodgeballPlayer enemy)
        {
            Vector3 houdiniFrom = houdini.Position;
            Vector3 enemyFrom = enemy.Position;
            Quaternion houdiniFacing = houdini.Rotation;
            Quaternion enemyFacing = enemy.Rotation;

            ComputeDestinations(houdini, enemy, out Vector3 houdiniTo, out Vector3 enemyTo);

            // Departure puffs.
            VfxManager.Spawn(VfxId.TeleportPoof, houdiniFrom + Vector3.up * 1f, Quaternion.identity, flashScale, flashTint);
            VfxManager.Spawn(VfxId.TeleportPoof, enemyFrom + Vector3.up * 1f, Quaternion.identity, flashScale, flashTint);

            houdini.Teleport(houdiniTo, houdiniFacing);
            enemy.Teleport(enemyTo, enemyFacing);

            // Arrival flashes + sound at both ends.
            VfxManager.Spawn(VfxId.SwapFlash, houdiniTo + Vector3.up * 1f, Quaternion.identity, flashScale, flashTint);
            VfxManager.Spawn(VfxId.SwapFlash, enemyTo + Vector3.up * 1f, Quaternion.identity, flashScale, flashTint);
            AudioManager.PlayAt(SfxId.Teleport, houdiniTo, 1f);
            AudioManager.PlayAt(SfxId.Teleport, enemyTo, 1f, 0.94f);

            // Disorientation: cancels the enemy's charge / catch stance and interrupts their channels.
            if (disorientStun > 0f && enemy.StateMachine != null) enemy.StateMachine.Stun(disorientStun);

            var juice = JuiceManager.Instance;
            if (juice != null && swapTrauma > 0f) juice.AddTrauma(swapTrauma, (houdiniTo + enemyTo) * 0.5f);
            if (localWarpIntensity > 0f && (houdini.IsLocalPlayer || enemy.IsLocalPlayer))
                ScreenFx.Pulse(ScreenPulse.TimeRewind, localWarpIntensity, 0.3f);
        }

        /// <summary>Mirrored destinations, clamped to each player's zone and separated from bystanders.</summary>
        private void ComputeDestinations(DodgeballPlayer houdini, DodgeballPlayer enemy, out Vector3 houdiniTo, out Vector3 enemyTo)
        {
            Vector3 h = houdini.Position;
            Vector3 e = enemy.Position;
            var court = Court.Instance;

            if (court != null && houdini.Team.IsValid() && enemy.Team.IsValid())
            {
                float cz = court.Center.z;
                float houdiniSide = court.SideSign(houdini.Team);
                float enemySide = court.SideSign(enemy.Team);
                houdiniTo = new Vector3(e.x, h.y, cz + houdiniSide * Mathf.Abs(e.z - cz));
                enemyTo = new Vector3(h.x, e.y, cz + enemySide * Mathf.Abs(h.z - cz));
            }
            else
            {
                // No court (sandbox / tests): plain swap, each keeping its own height.
                houdiniTo = new Vector3(e.x, h.y, e.z);
                enemyTo = new Vector3(h.x, e.y, h.z);
            }

            houdiniTo = AbilityUtil.ClampToPlayerZone(houdini, houdiniTo);
            enemyTo = AbilityUtil.ClampToPlayerZone(enemy, enemyTo);

            // Keep clear of bystanders (the swap partner is leaving its spot, so it is ignored).
            houdiniTo = AbilityUtil.ClampToPlayerZone(houdini, Separate(houdiniTo, houdini, enemy, enemyTo));
            enemyTo = AbilityUtil.ClampToPlayerZone(enemy, Separate(enemyTo, enemy, houdini, houdiniTo));
        }

        /// <summary>Pushes <paramref name="destination"/> out of other players' personal space (one relaxation pass).</summary>
        private Vector3 Separate(Vector3 destination, DodgeballPlayer mover, DodgeballPlayer partner, Vector3 partnerDestination)
        {
            float min = minSeparation;
            float minSqr = min * min;

            // The partner will stand at its own destination.
            destination = PushAway(destination, partnerDestination, min, minSqr, mover.Forward);

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p == mover || p == partner || !p.IsInitialized || !p.gameObject.activeInHierarchy) continue;
                destination = PushAway(destination, p.Position, min, minSqr, mover.Forward);
            }
            return destination;
        }

        private static Vector3 PushAway(Vector3 destination, Vector3 other, float min, float minSqr, Vector3 fallbackAxis)
        {
            Vector3 d = destination - other;
            d.y = 0f;
            float sqr = d.sqrMagnitude;
            if (sqr >= minSqr) return destination;

            Vector3 dir = sqr > 1e-6f ? d / Mathf.Sqrt(sqr) : Vector3.Cross(Vector3.up, fallbackAxis).normalized;
            Vector3 pushed = other + dir * min;
            return new Vector3(pushed.x, destination.y, pushed.z);
        }

        // ------------------------------------------------------------------ targeting

        private DodgeballPlayer ResolveTarget()
        {
            var combat = Owner.Combat;
            if (combat != null && IsValidTarget(combat.CurrentTarget, 1f)) return combat.CurrentTarget;

            var intent = Owner.Intent;
            if (IsValidTarget(intent.DesiredTarget, 1f)) return intent.DesiredTarget;

            Vector3 aim = intent.AimDirection;
            if (aim.sqrMagnitude < 1e-4f) aim = Owner.Forward;
            var best = TargetingSystem.FindBestTarget(Owner, Owner.ChestPosition, aim.normalized, aimConeAngle, maxRange);
            return IsValidTarget(best, 1f) ? best : null;
        }

        /// <summary>A targetable infield enemy, visible (unless allowed), within range * <paramref name="rangeSlack"/>.</summary>
        private bool IsValidTarget(DodgeballPlayer p, float rangeSlack)
        {
            if (p == null || !p.IsInitialized || !p.gameObject.activeInHierarchy) return false;
            if (!IsEnemy(p) || !p.IsTargetable) return false;
            if (!allowCloakedTargets && p.Status != null && p.Status.Has(StatusEffectType.Cloaked) &&
                !p.Status.Has(StatusEffectType.Revealed)) return false;

            float range = maxRange * rangeSlack;
            return (p.Position - Owner.Position).sqrMagnitude <= range * range;
        }

        private void ClearChannel()
        {
            VfxManager.StopEffect(_ownerFlash);
            VfxManager.StopEffect(_targetFlash);
            _ownerFlash = default;
            _targetFlash = default;

            if (_rooted)
            {
                _rooted = false;
                if (Owner != null && Owner.Status != null) Owner.Status.Remove(StatusEffectType.Rooted, this);
            }
        }
    }
}
