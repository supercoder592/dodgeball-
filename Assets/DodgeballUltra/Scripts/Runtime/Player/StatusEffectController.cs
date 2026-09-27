using System;
using System.Collections.Generic;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - timed status effects keyed by (type, source). Duration &lt;= 0 means permanent until removed.
    /// Applies side-effects: Frozen -> Incapacitate(Frozen) + motor frozen + CharacterVisual.SetFrozen;
    /// Slow/Haste -> motor speed modifier; Slippery -> motor traction; Cloaked -> CharacterVisual.SetCloaked;
    /// Stunned mirrors the state machine. Publishes StatusEffectChangedEvent.
    /// <para>Owner module: Player.</para>
    /// <para>
    /// Stacking: one entry per (type, source). The effective value of a type is its strongest entry (Slow and Haste use the
    /// strongest of each; <see cref="MoveSpeedMultiplier"/> = (1 - slow) x (1 + haste)). Rules:
    /// <list type="bullet">
    /// <item>Frozen (Elsa): cannot move or catch; incapacitates the state machine; icy look (CharacterVisual.SetFrozen, blue tint, mist).
    ///       Allies cannot remove it (only its duration, <see cref="Remove"/> by its source, or a reset ends it).</item>
    /// <item>Revealed removes and blocks Cloaked (Gale throwing from stealth, detection abilities).</item>
    /// <item>Stunned applied from outside is routed to <see cref="PlayerStateMachine.Stun"/>, which mirrors it back here.</item>
    /// <item>Rooted / DodgeDisabled / Silenced / Invulnerable are read by the motor, abilities and <see cref="PlayerHealth"/>.</item>
    /// </list>
    /// Allocation-free while ticking (entries live in a pre-sized list, changes are tracked with a bit mask).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatusEffectController : MonoBehaviour
    {
        /// <summary>One active effect instance.</summary>
        public struct ActiveEffect
        {
            public StatusEffectType Type;
            public object Source;
            public float Duration;   // requested duration (<= 0 permanent)
            public float Remaining;  // +inf when permanent
            public float Magnitude;
            public bool Permanent;
        }

        [Header("Frozen look")]
        [Tooltip("Sustained tint applied through HitFlash while frozen (glacier ice blue).")]
        [SerializeField] private Color frozenTint = new Color(0.62f, 0.83f, 1f, 1f);

        [Tooltip("Strength of the frozen tint (0..1).")]
        [Range(0f, 1f)] [SerializeField] private float frozenTintAmount = 0.45f;

        [Tooltip("Height (m) of the looping frozen-mist effect above the feet.")]
        [Min(0f)] [SerializeField] private float frozenMistHeight = 0.9f;

        [Tooltip("Scale of the ice shatter burst when the freeze ends.")]
        [Min(0f)] [SerializeField] private float thawBurstScale = 0.6f;

        [Tooltip("Cold screen tint (0..1) shown to the local player while frozen.")]
        [Range(0f, 1f)] [SerializeField] private float localFrozenScreenTint = 0.6f;

        [Header("Limits")]
        [Tooltip("Strongest slow accepted (a 100% slow would be a root; use Rooted for that).")]
        [Range(0f, 1f)] [SerializeField] private float maxSlow = 0.9f;

        [Tooltip("Strongest haste accepted (+fraction).")]
        [Min(0f)] [SerializeField] private float maxHaste = 1f;

        /// <summary>(type, active) raised whenever an effect is applied/refreshed (true) or removed/expired (false when fully gone).</summary>
        public event Action<StatusEffectType, bool> Changed;

        private const int TypeCount = 32; // StatusEffectType values must stay below 32 (bit mask bookkeeping)

        private readonly List<ActiveEffect> _effects = new List<ActiveEffect>(16);
        private readonly int[] _counts = new int[TypeCount];
        private readonly bool[] _sideEffectActive = new bool[TypeCount];

        private DodgeballPlayer _owner;
        private float _moveSpeedMultiplier = 1f;
        private bool _blockedCatching;
        private VfxHandle _frozenMist;

        /// <summary>The player these effects belong to.</summary>
        public DodgeballPlayer Owner => _owner;

        /// <summary>Currently active effect instances (read-only view, no allocation).</summary>
        public IReadOnlyList<ActiveEffect> Active => _effects;

        // ------------------------------------------------------------------ contract

        public void Initialize(DodgeballPlayer owner)
        {
            if (_owner != null && _owner != owner) RemoveAll();
            _owner = owner;
            RecomputeMoveSpeed();
        }

        /// <summary>
        /// Applies (or refreshes) an effect. <paramref name="source"/> distinguishes stacks from different causes;
        /// the same (type, source) pair refreshes duration and keeps the larger magnitude.
        /// </summary>
        public void Apply(StatusEffectType type, float duration, float magnitude = 1f, object source = null)
        {
            int t = (int)type;
            if (t < 0 || t >= TypeCount || float.IsNaN(duration) || float.IsNaN(magnitude)) return;

            // Eliminated players (ragdolling / outfield transfer) take no new effects.
            if (_owner != null && _owner.Health != null && _owner.Health.IsEliminated) return;

            // Revealed blocks stealth.
            if (type == StatusEffectType.Cloaked && Has(StatusEffectType.Revealed)) return;

            // Stun requested by gameplay code: the state machine owns stuns and mirrors them back with itself as source.
            var fsm = _owner != null ? _owner.StateMachine : null;
            if (type == StatusEffectType.Stunned && fsm != null && !fsm.IsMirroringStun && !ReferenceEquals(source, fsm))
            {
                if (duration > 0f) fsm.Stun(duration);
                return;
            }

            bool permanent = duration <= 0f;
            float remaining = permanent ? float.PositiveInfinity : duration;

            int index = IndexOf(type, source);
            if (index >= 0)
            {
                var e = _effects[index];
                e.Permanent |= permanent;
                e.Remaining = e.Permanent ? float.PositiveInfinity : Mathf.Max(e.Remaining, remaining);
                e.Duration = e.Permanent ? 0f : Mathf.Max(e.Duration, duration);
                e.Magnitude = Mathf.Max(e.Magnitude, magnitude);
                _effects[index] = e;
            }
            else
            {
                _effects.Add(new ActiveEffect
                {
                    Type = type,
                    Source = source,
                    Duration = permanent ? 0f : duration,
                    Remaining = remaining,
                    Magnitude = magnitude,
                    Permanent = permanent,
                });
                _counts[t]++;
            }

            // Revealed strips any active cloak.
            if (type == StatusEffectType.Revealed && Has(StatusEffectType.Cloaked))
                Remove(StatusEffectType.Cloaked);

            ProcessTypeChange(type);
        }

        /// <summary>Removes the effect from <paramref name="source"/> (or every source when null).</summary>
        public void Remove(StatusEffectType type, object source = null)
        {
            int t = (int)type;
            if (t < 0 || t >= TypeCount || _counts[t] == 0) return;

            bool removed = false;
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var e = _effects[i];
                if (e.Type != type) continue;
                if (source != null && !ReferenceEquals(e.Source, source)) continue;
                _effects.RemoveAt(i);
                _counts[t]--;
                removed = true;
            }

            if (removed) ProcessTypeChange(type);
        }

        public void RemoveAll()
        {
            if (_effects.Count == 0)
            {
                // Still make sure no side effect is left over (e.g. after a re-initialisation).
                for (int t = 0; t < TypeCount; t++)
                    if (_sideEffectActive[t]) ProcessTypeChange((StatusEffectType)t);
                return;
            }

            int mask = 0;
            for (int i = 0; i < _effects.Count; i++) mask |= 1 << (int)_effects[i].Type;
            _effects.Clear();
            Array.Clear(_counts, 0, _counts.Length);
            ProcessMask(mask);
        }

        /// <summary>
        /// Elimination / round-reset clear: removes every timed effect and every hard-control effect (Frozen, Stunned,
        /// Rooted) whatever its duration, but keeps permanent markers applied by passives (e.g. Gale's SilentFootsteps,
        /// applied with duration &lt;= 0). Ability-owned permanent effects are removed by the abilities themselves when
        /// they are interrupted.
        /// </summary>
        public void ClearForReset()
        {
            if (_effects.Count == 0)
            {
                RemoveAll();
                return;
            }

            int mask = 0;
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var e = _effects[i];
                bool hardControl = e.Type == StatusEffectType.Frozen || e.Type == StatusEffectType.Stunned ||
                                   e.Type == StatusEffectType.Rooted;
                if (e.Permanent && !hardControl) continue;
                _effects.RemoveAt(i);
                _counts[(int)e.Type]--;
                mask |= 1 << (int)e.Type;
            }
            if (mask != 0) ProcessMask(mask);
        }

        public bool Has(StatusEffectType type)
        {
            int t = (int)type;
            return t >= 0 && t < TypeCount && _counts[t] > 0;
        }

        /// <summary>Strongest active magnitude for <paramref name="type"/> (0 when absent).</summary>
        public float GetMagnitude(StatusEffectType type)
        {
            if (!Has(type)) return 0f;
            float best = 0f;
            for (int i = 0; i < _effects.Count; i++)
                if (_effects[i].Type == type && _effects[i].Magnitude > best) best = _effects[i].Magnitude;
            return best;
        }

        /// <summary>Longest remaining duration for <paramref name="type"/> (0 when absent, +inf when permanent).</summary>
        public float GetRemaining(StatusEffectType type)
        {
            if (!Has(type)) return 0f;
            float best = 0f;
            for (int i = 0; i < _effects.Count; i++)
                if (_effects[i].Type == type && _effects[i].Remaining > best) best = _effects[i].Remaining;
            return best;
        }

        /// <summary>Combined Slow/Haste multiplier for the motor.</summary>
        public float MoveSpeedMultiplier => _moveSpeedMultiplier;

        public void Tick(float deltaTime)
        {
            if (_effects.Count == 0 || deltaTime <= 0f) return;

            int expiredMask = 0;
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var e = _effects[i];
                if (e.Permanent) continue;
                e.Remaining -= deltaTime;
                if (e.Remaining <= 0f)
                {
                    _effects.RemoveAt(i);
                    _counts[(int)e.Type]--;
                    expiredMask |= 1 << (int)e.Type;
                }
                else
                {
                    _effects[i] = e;
                }
            }

            if (expiredMask != 0) ProcessMask(expiredMask);
        }

        // ------------------------------------------------------------------ additional queries

        /// <summary>True if <paramref name="source"/> has an active <paramref name="type"/> effect on this player.</summary>
        public bool HasFrom(StatusEffectType type, object source) => IndexOf(type, source) >= 0;

        // ------------------------------------------------------------------ side effects

        private void ProcessMask(int mask)
        {
            for (int t = 0; t < TypeCount && mask != 0; t++)
            {
                int bit = 1 << t;
                if ((mask & bit) == 0) continue;
                mask &= ~bit;
                ProcessTypeChange((StatusEffectType)t);
            }
        }

        /// <summary>Re-evaluates the side effects of <paramref name="type"/> and publishes the change.</summary>
        private void ProcessTypeChange(StatusEffectType type)
        {
            int t = (int)type;
            bool active = _counts[t] > 0;
            float magnitude = active ? GetMagnitude(type) : 0f;
            bool wasActive = _sideEffectActive[t];
            _sideEffectActive[t] = active;

            switch (type)
            {
                case StatusEffectType.Slow:
                case StatusEffectType.Haste:
                    RecomputeMoveSpeed();
                    break;

                case StatusEffectType.Slippery:
                    if (_owner != null && _owner.Motor != null)
                    {
                        if (active) _owner.Motor.SetTractionModifier(this, 1f - Mathf.Clamp01(magnitude));
                        else _owner.Motor.RemoveTractionModifier(this);
                    }
                    break;

                case StatusEffectType.Frozen:
                    if (active != wasActive) ApplyFrozen(active);
                    break;

                case StatusEffectType.Cloaked:
                    if (active != wasActive && _owner != null && _owner.Visual != null) _owner.Visual.SetCloaked(active);
                    break;
            }

            if (active == wasActive && !active) return; // nothing was there and nothing is there

            float remaining = active ? GetRemaining(type) : 0f;
            GameEvents.Publish(new StatusEffectChangedEvent
            {
                Player = _owner,
                Type = type,
                Applied = active,
                Duration = float.IsPositiveInfinity(remaining) ? 0f : remaining,
                Magnitude = magnitude,
            });
            Changed?.Invoke(type, active);
        }

        private void ApplyFrozen(bool frozen)
        {
            if (_owner == null) return;

            var fsm = _owner.StateMachine;
            if (fsm != null)
            {
                if (frozen) fsm.Incapacitate(IncapacitationReason.Frozen);
                else fsm.ReleaseIncapacitation(IncapacitationReason.Frozen);
            }

            var combat = _owner.Combat;
            if (combat != null)
            {
                if (frozen)
                {
                    if (!combat.CatchingBlocked)
                    {
                        combat.CatchingBlocked = true;
                        _blockedCatching = true;
                    }
                }
                else if (_blockedCatching)
                {
                    combat.CatchingBlocked = false;
                    _blockedCatching = false;
                }
            }

            var visual = _owner.Visual;
            if (visual != null)
            {
                visual.SetFrozen(frozen);
                if (visual.HitFlash != null) visual.HitFlash.SetTint(frozenTint, frozen ? frozenTintAmount : 0f);
            }

            if (frozen)
            {
                if (!_frozenMist.IsValid)
                    _frozenMist = VfxManager.SpawnAttached(VfxId.FrozenMist, _owner.transform, Vector3.up * frozenMistHeight);
            }
            else
            {
                if (_frozenMist.IsValid) VfxManager.StopEffect(_frozenMist);
                _frozenMist = default;
                // Thaw: the ice cracks off.
                VfxManager.Spawn(VfxId.IceBurst, _owner.ChestPosition, Quaternion.identity, thawBurstScale);
            }

            if (_owner.IsLocalPlayer) ScreenFx.SetSustained(ScreenPulse.Freeze, frozen ? localFrozenScreenTint : 0f);
        }

        private void RecomputeMoveSpeed()
        {
            float slow = Mathf.Clamp(GetMagnitude(StatusEffectType.Slow), 0f, maxSlow);
            float haste = Mathf.Clamp(GetMagnitude(StatusEffectType.Haste), 0f, maxHaste);
            _moveSpeedMultiplier = (1f - slow) * (1f + haste);
        }

        private int IndexOf(StatusEffectType type, object source)
        {
            for (int i = 0; i < _effects.Count; i++)
            {
                var e = _effects[i];
                if (e.Type == type && ReferenceEquals(e.Source, source)) return i;
            }
            return -1;
        }

        private void OnDestroy()
        {
            if (_frozenMist.IsValid) VfxManager.StopEffect(_frozenMist, true);
            if (_owner != null && _owner.IsLocalPlayer && _sideEffectActive[(int)StatusEffectType.Frozen])
                ScreenFx.SetSustained(ScreenPulse.Freeze, 0f);
        }
    }
}
