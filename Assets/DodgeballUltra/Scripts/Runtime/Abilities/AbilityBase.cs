using System;
using System.Collections.Generic;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities
{
    /// <summary>
    /// Base class for every hero ability (passive, skill and ultimate).
    /// <para>
    /// Concrete abilities are plain <c>[Serializable]</c> C# classes whose serialized fields are their tuning values.
    /// They are stored polymorphically inside an <see cref="AbilityData"/> asset (<c>[SerializeReference]</c>) and cloned
    /// per player at runtime, so designers tweak them in the Inspector and every player gets independent state.
    /// </para>
    /// <para>Lifecycle (driven by <see cref="AbilityController"/>):</para>
    /// <code>
    ///            TryActivate()                 castTime elapsed              duration elapsed / EndAbility()
    ///  Ready ─────────────────► Casting ─────────────────────► Active ──────────────────────────────► Cooldown ──► Ready
    ///           OnCastStarted()           OnCast()               OnTick(dt) each frame      OnEnd() + OnCooldown()   OnReady()
    ///                     │  Interrupt(): OnInterrupt() + OnEnd(true) + cooldown  ▲
    ///                     └───────────────────────────────────────────────────────┘
    /// Passives: Equip() -> OnEquip() once, then OnTick(dt) every frame; never cast.
    /// </code>
    /// Hooks required by the spec: <see cref="OnCast"/>, <see cref="OnTick"/>, <see cref="OnInterrupt"/>, <see cref="OnCooldown"/>.
    /// </summary>
    [Serializable]
    public abstract class AbilityBase
    {
        // ------------------------------------------------------------------ runtime state (never serialized)
        [NonSerialized] private AbilityData _data;
        [NonSerialized] private DodgeballPlayer _owner;
        [NonSerialized] private Cooldown _cooldown;
        [NonSerialized] private AbilityPhase _phase;
        [NonSerialized] private float _castRemaining;
        [NonSerialized] private float _activeRemaining;
        [NonSerialized] private bool _holdActive;
        [NonSerialized] private bool _equipped;
        [NonSerialized] private List<Action> _unsubscribers;

        // ------------------------------------------------------------------ public state
        public AbilityData Data => _data;
        public DodgeballPlayer Owner => _owner;
        public AbilitySlot Slot => _data != null ? _data.slot : AbilitySlot.Skill;
        public AbilityPhase Phase => _phase;
        public bool IsInitialized => _owner != null;
        public bool IsPassive => Slot == AbilitySlot.Passive;
        public bool IsReady => _phase == AbilityPhase.Ready;
        public bool IsCasting => _phase == AbilityPhase.Casting;
        public bool IsActive => _phase == AbilityPhase.Active;
        public bool IsBusy => _phase == AbilityPhase.Casting || _phase == AbilityPhase.Active;

        public string DisplayName => _data != null ? _data.displayName : GetType().Name;

        /// <summary>Cooldown length used when the ability ends. Override for dynamic cooldowns.</summary>
        public virtual float CooldownDuration => _data != null ? _data.cooldown : 0f;

        /// <summary>Channel time before OnCast. Override for dynamic cast times.</summary>
        public virtual float CastTime => _data != null ? _data.castTime : 0f;

        /// <summary>Active duration after OnCast. Override for dynamic durations.</summary>
        public virtual float Duration => _data != null ? _data.duration : 0f;

        public float CooldownRemaining => _cooldown != null ? _cooldown.Remaining : 0f;
        public float CooldownNormalized => _cooldown != null ? _cooldown.NormalizedRemaining : 0f;
        public float ActiveTimeRemaining => _phase == AbilityPhase.Active ? Mathf.Max(0f, _activeRemaining) : 0f;

        /// <summary>0..1 progress of the current channel.</summary>
        public float CastProgress => _phase == AbilityPhase.Casting && CastTime > 0f ? 1f - Mathf.Clamp01(_castRemaining / CastTime) : 0f;

        // ------------------------------------------------------------------ construction

        /// <summary>
        /// Clones this template for runtime use. Serialized tuning fields are copied (shallow); runtime state is reset.
        /// Override only if the ability holds mutable reference-type tuning data that must be deep-copied.
        /// </summary>
        public virtual AbilityBase CloneTemplate()
        {
            var clone = (AbilityBase)MemberwiseClone();
            clone._data = null;
            clone._owner = null;
            clone._cooldown = null;
            clone._phase = AbilityPhase.Ready;
            clone._castRemaining = 0f;
            clone._activeRemaining = 0f;
            clone._holdActive = false;
            clone._equipped = false;
            clone._unsubscribers = null;
            return clone;
        }

        /// <summary>Binds this runtime instance to its owner and data. Called once by <see cref="AbilityController"/>.</summary>
        public void Initialize(DodgeballPlayer owner, AbilityData data)
        {
            _owner = owner;
            _data = data;
            _cooldown = new Cooldown(CooldownDuration);
            _phase = IsPassive ? AbilityPhase.Disabled : AbilityPhase.Ready;
            OnInitialize();
            Equip();
        }

        /// <summary>Activates passive hooks / event subscriptions. Idempotent.</summary>
        public void Equip()
        {
            if (_equipped || _owner == null) return;
            _equipped = true;
            OnEquip();
        }

        /// <summary>Removes passive hooks and every subscription made through <see cref="Listen{T}"/>. Idempotent.</summary>
        public void Unequip()
        {
            if (!_equipped) return;
            if (IsBusy) Interrupt(InterruptReason.Replaced);
            OnUnequip();
            if (_unsubscribers != null)
            {
                for (int i = _unsubscribers.Count - 1; i >= 0; i--) _unsubscribers[i]();
                _unsubscribers.Clear();
            }
            _equipped = false;
        }

        // ------------------------------------------------------------------ activation

        /// <summary>Checks every generic rule, then <see cref="CanActivateCustom"/>.</summary>
        public bool CanActivate(out AbilityFailReason reason)
        {
            reason = AbilityFailReason.None;
            if (_owner == null || _data == null) { reason = AbilityFailReason.Custom; return false; }
            if (IsPassive) { reason = AbilityFailReason.IsPassive; return false; }
            if (IsBusy) { reason = AbilityFailReason.AlreadyActive; return false; }
            if (_phase == AbilityPhase.Cooldown) { reason = AbilityFailReason.OnCooldown; return false; }
            if (_phase == AbilityPhase.Disabled) { reason = AbilityFailReason.Custom; return false; }

            var match = MatchManager.Instance;
            if (match != null && !match.IsPlaying) { reason = AbilityFailReason.MatchNotPlaying; return false; }
            if (!_owner.CanAct) { reason = AbilityFailReason.CannotAct; return false; }
            if (_owner.Status != null && _owner.Status.Has(StatusEffectType.Silenced)) { reason = AbilityFailReason.Silenced; return false; }
            if (!_owner.IsInfield && !_data.usableFromOutfield) { reason = AbilityFailReason.NotInfield; return false; }
            if (_data.requiresBall && (_owner.Combat == null || !_owner.Combat.HasBall)) { reason = AbilityFailReason.RequiresBall; return false; }
            if (Slot == AbilitySlot.Ultimate && (_owner.Abilities == null || !_owner.Abilities.IsUltimateReady(_data.ultimateCost)))
            {
                reason = AbilityFailReason.UltimateNotCharged;
                return false;
            }

            return CanActivateCustom(out reason);
        }

        /// <summary>
        /// Starts the ability if allowed: consumes the ultimate meter, then either starts the channel (castTime &gt; 0)
        /// or fires <see cref="OnCast"/> immediately. Publishes AbilityFailedEvent on failure.
        /// </summary>
        public bool TryActivate(out AbilityFailReason reason)
        {
            if (!CanActivate(out reason))
            {
                if (_owner != null && reason != AbilityFailReason.IsPassive)
                {
                    GameEvents.Publish(new AbilityFailedEvent { Player = _owner, Ability = this, Slot = Slot, Reason = reason });
                }
                return false;
            }

            if (Slot == AbilitySlot.Ultimate) _owner.Abilities.ConsumeUltimate(_data.ultimateCost);

            _holdActive = false;
            if (CastTime > 0f)
            {
                _phase = AbilityPhase.Casting;
                _castRemaining = CastTime;
                OnCastStarted();
            }
            else
            {
                FireCast();
            }
            return true;
        }

        /// <summary>Per-frame update (scaled time). Called by AbilityController.</summary>
        public void Tick(float deltaTime)
        {
            if (_owner == null) return;

            switch (_phase)
            {
                case AbilityPhase.Disabled:
                    if (IsPassive && _equipped) OnTick(deltaTime);
                    break;

                case AbilityPhase.Casting:
                    _castRemaining -= deltaTime;
                    OnCastTick(deltaTime, CastProgress);
                    if (_phase == AbilityPhase.Casting && _castRemaining <= 0f) FireCast();
                    break;

                case AbilityPhase.Active:
                    OnTick(deltaTime);
                    if (_phase != AbilityPhase.Active) break; // OnTick ended the ability
                    if (!_holdActive)
                    {
                        _activeRemaining -= deltaTime;
                        if (_activeRemaining <= 0f) EndInternal(false);
                    }
                    break;

                case AbilityPhase.Cooldown:
                    bool finished = _cooldown.Tick(deltaTime);
                    OnCooldownTick(deltaTime);
                    if (finished)
                    {
                        _phase = AbilityPhase.Ready;
                        OnReady();
                    }
                    break;
            }
        }

        /// <summary>
        /// Interrupts a channel or active effect (stun, freeze, elimination, round end). Non-interruptible abilities
        /// ignore Stunned/Frozen but are always stopped by Eliminated/RoundEnded/Replaced/Cancelled.
        /// </summary>
        public void Interrupt(InterruptReason reason)
        {
            if (!IsBusy) return;
            bool soft = reason == InterruptReason.Stunned || reason == InterruptReason.Frozen;
            if (soft && _data != null && !_data.interruptible) return;
            OnInterrupt(reason);
            EndInternal(true);
        }

        /// <summary>Instantly readies the ability (debug, round reset, cooldown refunds).</summary>
        public void ResetCooldown()
        {
            _cooldown?.Reset();
            if (_phase == AbilityPhase.Cooldown)
            {
                _phase = AbilityPhase.Ready;
                OnReady();
            }
        }

        /// <summary>Reduces the remaining cooldown by <paramref name="seconds"/>.</summary>
        public void ReduceCooldown(float seconds) => _cooldown?.Reduce(seconds);

        /// <summary>Called between rounds: stops anything running and readies the ability.</summary>
        public void ResetForRound()
        {
            if (IsBusy) Interrupt(InterruptReason.RoundEnded);
            if (IsBusy) EndInternal(true); // non-interruptible edge case
            _cooldown?.Reset();
            if (!IsPassive) _phase = AbilityPhase.Ready;
            OnRoundReset();
        }

        // ------------------------------------------------------------------ hooks for concrete abilities

        /// <summary>After Initialize, before Equip. Cache components here.</summary>
        protected virtual void OnInitialize() { }

        /// <summary>Register passive modifiers / event listeners here (use <see cref="Listen{T}"/>).</summary>
        protected virtual void OnEquip() { }

        /// <summary>Unregister anything registered in OnEquip (Listen subscriptions are removed automatically).</summary>
        protected virtual void OnUnequip() { }

        /// <summary>Extra activation rules (e.g. needs a target). Default: allowed.</summary>
        protected virtual bool CanActivateCustom(out AbilityFailReason reason)
        {
            reason = AbilityFailReason.None;
            return true;
        }

        /// <summary>A channel started (castTime &gt; 0). Show wind-up VFX here.</summary>
        protected virtual void OnCastStarted() { }

        /// <summary>Every frame while channelling.</summary>
        protected virtual void OnCastTick(float deltaTime, float progress) { }

        /// <summary>SPEC HOOK - the ability's effect fires. Required.</summary>
        protected abstract void OnCast();

        /// <summary>SPEC HOOK - every frame while Active (and every frame for passives).</summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>SPEC HOOK - a channel or active effect was interrupted.</summary>
        protected virtual void OnInterrupt(InterruptReason reason) { }

        /// <summary>SPEC HOOK - the cooldown just started.</summary>
        protected virtual void OnCooldown() { }

        /// <summary>Every frame while cooling down.</summary>
        protected virtual void OnCooldownTick(float deltaTime) { }

        /// <summary>The cooldown finished.</summary>
        protected virtual void OnReady() { }

        /// <summary>The Active phase closed (normally or interrupted). Clean up spawned objects here.</summary>
        protected virtual void OnEnd(bool interrupted) { }

        /// <summary>Round reset (after ResetForRound bookkeeping).</summary>
        protected virtual void OnRoundReset() { }

        /// <summary>
        /// AI utility 0..1 for using this ability now. Default: derived from <see cref="AbilityData.aiHint"/>.
        /// Override for smarter, ability-specific decisions.
        /// </summary>
        public virtual float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (_data == null) return 0f;
            float w = _data.aiWeight;
            switch (_data.aiHint)
            {
                case AbilityAIHint.Never: return 0f;
                case AbilityAIHint.Anytime: return w * 0.5f;
                case AbilityAIHint.WhenHoldingBall: return ctx.HoldingBall && ctx.NearestEnemy != null ? w : 0f;
                case AbilityAIHint.WhenThreatened: return ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.6f ? w : 0f;
                case AbilityAIHint.WhenEnemyInRange: return ctx.NearestEnemy != null && ctx.NearestEnemyDistance < 8f ? w : 0f;
                case AbilityAIHint.WhenTeammateOutfield: return ctx.TeammatesInOutfield > 0 ? w : 0f;
                case AbilityAIHint.WhenBallsLoose: return ctx.FreeBallsNearby > 0 && !ctx.HoldingBall ? w : 0f;
                case AbilityAIHint.WhenLosing: return ctx.AlliesInfield < ctx.EnemiesInfield ? w : 0f;
                default: return 0f;
            }
        }

        // ------------------------------------------------------------------ helpers for concrete abilities

        /// <summary>Ends the Active phase now (normal completion) and starts the cooldown.</summary>
        protected void EndAbility()
        {
            if (IsBusy) EndInternal(false);
        }

        /// <summary>
        /// Keeps the ability Active after OnCast until <see cref="EndAbility"/> is called (e.g. while a projectile flies),
        /// ignoring <see cref="Duration"/>.
        /// </summary>
        protected void HoldActive() => _holdActive = true;

        /// <summary>Adds seconds to the remaining Active time.</summary>
        protected void ExtendActive(float seconds) => _activeRemaining += seconds;

        /// <summary>Subscribes to a game event for as long as this ability is equipped.</summary>
        protected void Listen<T>(Action<T> handler) where T : struct, IGameEvent
        {
            GameEvents.Subscribe(handler);
            (_unsubscribers ??= new List<Action>()).Add(() => GameEvents.Unsubscribe(handler));
        }

        protected bool IsEnemy(DodgeballPlayer other) => PlayerRegistry.AreEnemies(_owner, other);
        protected bool IsAlly(DodgeballPlayer other) => PlayerRegistry.AreTeammates(_owner, other);
        protected bool IsOwner(DodgeballPlayer other) => other != null && other == _owner;

        /// <summary>Time.time shortcut (scaled; respects hitstop).</summary>
        protected static float Now => Time.time;

        // ------------------------------------------------------------------ internals

        private void FireCast()
        {
            _phase = AbilityPhase.Active;
            _activeRemaining = Duration;
            GameEvents.Publish(new AbilityCastEvent { Player = _owner, Ability = this, Slot = Slot });
            OnCast();

            // Instant abilities (no duration, not held) end right after the effect fires.
            if (_phase == AbilityPhase.Active && !_holdActive && _activeRemaining <= 0f) EndInternal(false);
        }

        private void EndInternal(bool interrupted)
        {
            if (!IsBusy) return;
            _holdActive = false;
            _activeRemaining = 0f;
            _castRemaining = 0f;
            // Mark as cooling down before callbacks so re-entrant EndAbility() calls are no-ops.
            _phase = AbilityPhase.Cooldown;
            OnEnd(interrupted);
            GameEvents.Publish(new AbilityEndedEvent { Player = _owner, Ability = this, Slot = Slot, Interrupted = interrupted });

            float cd = CooldownDuration;
            if (cd > 0f)
            {
                _cooldown.Start(cd);
                OnCooldown();
            }
            else
            {
                _cooldown.Reset();
                _phase = AbilityPhase.Ready;
                OnReady();
            }
        }
    }
}
