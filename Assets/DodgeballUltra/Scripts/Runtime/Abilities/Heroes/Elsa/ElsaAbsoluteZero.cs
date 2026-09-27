using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Elsa ultimate [Absolute Zero] (duration 6 s): flash-freezes the entire enemy court.
    /// <list type="bullet">
    /// <item>Every enemy INFIELD player gets <see cref="StatusEffectType.Slippery"/> 0.85 (the motor loses 85% traction:
    /// heavy inertia, long skids, hard to change direction) and <see cref="StatusEffectType.DodgeDisabled"/> (no jumps,
    /// no slides) for the rest of the window.</item>
    /// <item>The court stays frozen for the whole window: enemies who enter the infield later (revived by a perfect catch
    /// or an outfield hit) are affected on arrival; enemies who leave it (eliminated) are released so the outfield and the
    /// ragdoll are never slippery.</item>
    /// <item>An <see cref="ElsaAbsoluteZeroField"/> glazes the enemy half with creeping frost and cold mist; if the local
    /// player is one of the victims their screen gets the sustained Freeze tint while affected.</item>
    /// </list>
    /// <para>
    /// Once cast, the frozen court is committed: if Elsa herself is stunned, frozen or eliminated mid-ultimate, the
    /// ability phase ends (kernel rules) but the effect keeps running to its end, ticked by the field
    /// (<see cref="ElsaAbsoluteZeroField.ExternalTick"/>). Only a round end, an unequip or a replacement stops it early.
    /// </para>
    /// Statuses use this ability instance as their source, so they never collide with other Slippery/DodgeDisabled
    /// sources and are removed precisely at the end.
    /// </summary>
    [Serializable]
    public sealed class ElsaAbsoluteZero : AbilityBase
    {
        [Header("Absolute Zero")]
        [Tooltip("Traction lost by enemies on the frozen court (0..1). Spec: heavy inertia/sliding -> 0.85.")]
        [Range(0f, 1f)] public float tractionLoss = 0.85f;

        [Tooltip("Enemies on the frozen court cannot jump or slide. Spec: dodges disabled.")]
        public bool disableDodges = true;

        [Tooltip("Used only when the AbilityData has no duration: seconds the court stays frozen. Spec: 6 s.")]
        [Range(1f, 15f)] public float fallbackDuration = 6f;

        [Tooltip("Keep the court frozen when Elsa is stunned, frozen or eliminated after casting.")]
        public bool persistIfCasterInterrupted = true;

        [Header("Presentation")]
        [Tooltip("Colour and peak opacity of the frost sheen on the enemy floor.")]
        public Color sheenColor = new Color(0.86f, 0.93f, 1f, 0.62f);

        [Tooltip("Tint of the cold mist and ice bursts.")]
        public Color mistTint = new Color(0.8f, 0.9f, 1f, 1f);

        [Tooltip("Scale of each cold-mist emitter over the frozen half.")]
        [Range(0.2f, 4f)] public float mistScale = 1.6f;

        [Tooltip("Seconds the frost takes to creep from the centre line to the enemy baseline.")]
        [Range(0.05f, 2f)] public float spreadTime = 0.45f;

        [Tooltip("Seconds the frost takes to thaw at the end.")]
        [Range(0.05f, 3f)] public float thawTime = 0.7f;

        [Tooltip("Sustained Freeze screen tint for the local player while affected (0 = none).")]
        [Range(0f, 1f)] public float localScreenFrost = 0.55f;

        [Tooltip("Camera trauma added on cast (0..1).")]
        [Range(0f, 1f)] public float castTrauma = 0.55f;

        [Header("AI")]
        [Tooltip("Bots cast when at least this many enemies are in the infield.")]
        [Range(1, 6)] public int aiMinEnemiesInfield = 2;

        [NonSerialized] private List<DodgeballPlayer> _affected;
        [NonSerialized] private Action<float> _orphanTick;
        [NonSerialized] private ElsaAbsoluteZeroField _field;
        [NonSerialized] private TeamId _frozenTeam = TeamId.None;
        [NonSerialized] private float _effectRemaining;
        [NonSerialized] private bool _effectRunning;
        [NonSerialized] private bool _orphaned;
        [NonSerialized] private bool _removeImmediately;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public ElsaAbsoluteZero()
        {
        }

        /// <summary>Enemies currently affected (read-only view for UI / tests).</summary>
        public IReadOnlyList<DodgeballPlayer> AffectedPlayers => _affected;

        /// <summary>True while the enemy court is frozen (also after an interrupt of Elsa, see class docs).</summary>
        public bool IsCourtFrozen => _effectRunning;

        /// <summary>Seconds of freeze left (0 when not frozen).</summary>
        public float FreezeTimeRemaining => _effectRunning ? Mathf.Max(0f, _effectRemaining) : 0f;

        /// <summary>Team whose court is frozen (None when inactive).</summary>
        public TeamId FrozenTeam => _frozenTeam;

        // ------------------------------------------------------------------ hooks

        protected override void OnInitialize()
        {
            // Runtime containers are created per clone (MemberwiseClone would otherwise share the template's instances).
            _affected = new List<DodgeballPlayer>(6);
            _orphanTick = TickEffect;
        }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            reason = AbilityFailReason.None;
            if (!Owner.Team.IsValid())
            {
                reason = AbilityFailReason.Custom;
                return false;
            }
            return true;
        }

        protected override void OnCast()
        {
            // A leftover orphaned effect (only possible with debug ultimate refills) is finished first.
            StopEffect(immediate: true);

            bool manualTimer = Duration <= 0f;
            float duration = manualTimer ? fallbackDuration : Duration;
            if (manualTimer) HoldActive();

            _frozenTeam = Owner.Team.Opponent();
            _effectRemaining = duration;
            _effectRunning = true;
            _orphaned = false;

            SpawnField(duration);
            Reconcile();
            PlayCastJuice();
        }

        protected override void OnTick(float deltaTime) => TickEffect(deltaTime);

        protected override void OnInterrupt(InterruptReason reason)
        {
            // Round end / hero replacement: the court is reset right away, no visible thaw into the next round.
            _removeImmediately = reason == InterruptReason.RoundEnded || reason == InterruptReason.Replaced;

            // Caster disabled after committing the ultimate: hand the effect over to the field so the court stays frozen.
            bool casterDisabled = reason == InterruptReason.Stunned || reason == InterruptReason.Frozen ||
                                  reason == InterruptReason.Eliminated;
            if (persistIfCasterInterrupted && casterDisabled && _effectRunning && _field != null)
            {
                _orphaned = true;
                _field.ExternalTick = _orphanTick;
            }
        }

        protected override void OnEnd(bool interrupted)
        {
            bool immediate = interrupted && _removeImmediately;
            _removeImmediately = false;
            if (_orphaned) return; // the field keeps ticking the effect until it runs out
            StopEffect(immediate);
        }

        protected override void OnRoundReset() => StopEffect(immediate: true);

        protected override void OnUnequip() => StopEffect(immediate: true);

        /// <summary>
        /// Worth more the more enemies are in the infield; bonus when losing and when the nearest enemy carries a ball
        /// (a skidding thrower cannot dodge the counter).
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.EnemiesInfield < aiMinEnemiesInfield) return 0f;
            float u = 0.45f + 0.15f * (ctx.EnemiesInfield - aiMinEnemiesInfield);
            if (ctx.AlliesInfield < ctx.EnemiesInfield) u += 0.15f;
            if (ctx.NearestEnemy != null && ctx.NearestEnemy.Combat != null && ctx.NearestEnemy.Combat.HasBall) u += 0.1f;
            return Mathf.Clamp01(u * (0.5f + Data.aiWeight));
        }

        // ------------------------------------------------------------------ effect

        /// <summary>Advances the frozen-court effect (driven by OnTick, or by the field after an interrupt).</summary>
        private void TickEffect(float deltaTime)
        {
            if (!_effectRunning) return;

            _effectRemaining -= deltaTime;
            if (_effectRemaining <= 0f)
            {
                StopEffect(immediate: false);
                if (IsActive) EndAbility();
                return;
            }

            Reconcile();
        }

        /// <summary>Releases every victim and thaws (or removes) the field. Idempotent.</summary>
        private void StopEffect(bool immediate)
        {
            ReleaseAll();
            if (_field != null)
            {
                _field.ExternalTick = null;
                // Immediate removal clears the screen tint now; a thaw fades it out together with the ice.
                if (immediate) _field.SetLocalFrost(0f);
                _field.Stop(immediate);
                _field = null;
            }
            _effectRunning = false;
            _orphaned = false;
            _effectRemaining = 0f;
            _frozenTeam = TeamId.None;
        }

        /// <summary>
        /// Makes the affected set match "enemy infield players still in play": new arrivals are frozen for the remaining
        /// window, leavers are released. Allocation-free; at most six players.
        /// </summary>
        private void Reconcile()
        {
            if (_affected == null || !_effectRunning || !_frozenTeam.IsValid()) return;

            float remaining = FreezeTimeRemaining;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.Team != _frozenTeam) continue;

                bool onFrozenCourt = p.IsInitialized && p.IsInfield && p.Status != null &&
                                     (p.Health == null || p.Health.IsAlive);
                bool tracked = _affected.Contains(p);

                if (onFrozenCourt && !tracked && remaining > 0f)
                {
                    Affect(p, remaining);
                    _affected.Add(p);
                }
                else if (!onFrozenCourt && tracked)
                {
                    Release(p);
                    _affected.Remove(p);
                }
            }

            // Players destroyed mid-effect.
            for (int i = _affected.Count - 1; i >= 0; i--)
            {
                if (_affected[i] == null) _affected.RemoveAt(i);
            }

            UpdateLocalScreenFrost();
        }

        private void Affect(DodgeballPlayer p, float duration)
        {
            p.Status.Apply(StatusEffectType.Slippery, duration, tractionLoss, this);
            if (disableDodges) p.Status.Apply(StatusEffectType.DodgeDisabled, duration, 1f, this);

            // A jolt of frost at their feet as the ice grabs them.
            VfxManager.Spawn(VfxId.IceBurst, p.Position + Vector3.up * 0.1f, Quaternion.identity, 0.6f, mistTint);
        }

        private void Release(DodgeballPlayer p)
        {
            if (p == null || p.Status == null) return;
            p.Status.Remove(StatusEffectType.Slippery, this);
            p.Status.Remove(StatusEffectType.DodgeDisabled, this);
        }

        private void ReleaseAll()
        {
            if (_affected == null) return;
            for (int i = 0; i < _affected.Count; i++) Release(_affected[i]);
            _affected.Clear();
        }

        private void UpdateLocalScreenFrost()
        {
            if (_field == null) return;
            bool localAffected = false;
            for (int i = 0; i < _affected.Count; i++)
            {
                if (_affected[i] != null && _affected[i].IsLocalPlayer)
                {
                    localAffected = true;
                    break;
                }
            }
            _field.SetLocalFrost(localAffected ? localScreenFrost : 0f);
        }

        private void SpawnField(float duration)
        {
            var court = Court.Instance;
            if (court == null) return;

            var style = ElsaAbsoluteZeroFieldStyle.Default;
            style.SheenColor = sheenColor;
            style.MistTint = mistTint;
            style.MistScale = mistScale;
            style.SpreadTime = spreadTime;
            style.FadeTime = thawTime;

            Bounds half = court.GetInfieldBounds(_frozenTeam);
            // The field's own timer is a safety net (+0.25 s): the ability normally stops it first, then it thaws.
            _field = ElsaAbsoluteZeroField.Create(half, court.FloorY, court.Center.z, _frozenTeam, duration + 0.25f, style);
        }

        private void PlayCastJuice()
        {
            var court = Court.Instance;
            Vector3 centre = court != null ? court.GetInfieldBounds(_frozenTeam).center : Owner.Position + Owner.Forward * 6f;
            if (court != null) centre.y = court.FloorY;

            VfxManager.Spawn(VfxId.IceBurst, centre + Vector3.up * 0.2f, Quaternion.identity, 3f, mistTint);
            VfxManager.Spawn(VfxId.IceBurst, Owner.Combat != null ? Owner.Combat.GetThrowOrigin() : Owner.ChestPosition,
                Quaternion.LookRotation(Owner.Forward), 0.8f, mistTint);
            AudioManager.PlayAt(SfxId.UltimateCast, Owner.Position, 1f);
            AudioManager.PlayAt(SfxId.Freeze, centre, 1f, 0.75f);

            var juice = JuiceManager.Instance;
            if (juice != null) juice.AddTrauma(castTrauma, centre);

            // Everyone sees the cold flash; the victims feel it harder (the sustained tint follows via the field).
            var local = PlayerRegistry.LocalPlayer;
            bool localIsVictim = local != null && local.Team == _frozenTeam && local.IsInfield;
            ScreenFx.Pulse(ScreenPulse.UltimateCast, Owner.IsLocalPlayer ? 1f : 0.5f, 0.45f);
            ScreenFx.Pulse(ScreenPulse.Freeze, localIsVictim ? 1f : 0.4f, 0.6f);
        }
    }
}
