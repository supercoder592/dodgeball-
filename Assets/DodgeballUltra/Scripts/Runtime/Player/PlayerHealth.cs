using System;
using System.Collections.Generic;
using DodgeballUltra.Events;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - HP, hit filtering and elimination pipeline.
    /// <code>
    /// ReceiveHit(ref hit):
    ///   if victim not targetable            -> Ignored
    ///   run IIncomingHitFilters (priority)  -> Negated if hit.Cancelled
    ///   Frozen + hit                        -> ForceEliminate (Elsa)
    ///   HP -= Damage, publish PlayerDamagedEvent
    ///   if HP &lt;= 0 || ForceEliminate:
    ///       run IEliminationInterceptors    -> EliminationDelayed / EliminationPrevented
    ///       else Eliminate()                -> PlayerEliminatedEvent (MatchManager moves the player to the outfield)
    /// </code>
    /// <para>Owner module: Player.</para>
    /// <para>Details:</para>
    /// <list type="bullet">
    /// <item>The Invulnerable status (Specter's dodge, revival grace) negates every hit except unblockable ones (Rayne's beam).</item>
    /// <item>Frozen rule: a ball hit on an already-frozen player eliminates (cause <see cref="EliminationCause.Frozen"/>).
    ///       Freezing payloads apply Frozen in <c>OnAfterHitPlayer</c>, so the freezing hit itself never counts.</item>
    /// <item>A non-lethal hit publishes <see cref="PlayerDamagedEvent"/>, applies the hit's knockback and a short flinch stun.</item>
    /// <item>A lethal hit goes through the interceptors; <see cref="HitOutcome.EliminationDelayed"/> keeps the player in at
    ///       HP 0 with <see cref="IsEliminationPending"/> set until <see cref="CommitPendingElimination"/> or a revival.</item>
    /// <item>Elimination: HP 0, Incapacitated(Eliminated), statuses cleared, impulse-driven ragdoll at the hit point
    ///       (impulse = ball momentum x readability factor), <see cref="PlayerEliminatedEvent"/>.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHealth : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Hit reaction")]
        [Tooltip("Flinch stun (s) after a non-lethal hit. Interrupts charging/catching (a hit tank cannot shrug off a fastball mid wind-up).")]
        [Min(0f)] [SerializeField] private float nonLethalHitStun = 0.2f;

        [Header("Elimination ragdoll impulse")]
        [Tooltip("Mass of the ball used for the momentum transfer (kg). Regulation foam ball ~0.35 kg.")]
        [Min(0.01f)] [SerializeField] private float ballMass = 0.35f;

        [Tooltip("Readability factor on the real momentum transfer (a 0.35 kg ball barely moves a 75 kg body; the ragdoll should visibly react).")]
        [Min(0f)] [SerializeField] private float ragdollImpulseFactor = 6f;

        [Tooltip("Extra upward share of the impulse (fraction of its magnitude) so the body lifts slightly off its feet.")]
        [Range(0f, 1f)] [SerializeField] private float ragdollUpwardBias = 0.2f;

        [Tooltip("Upper bound of the ragdoll impulse (N*s).")]
        [Min(0f)] [SerializeField] private float maxRagdollImpulse = 140f;

        [Tooltip("Impulse (N*s) used when an elimination has no ball (ability damage, delayed impact): a backward stagger.")]
        [Min(0f)] [SerializeField] private float defaultEliminationImpulse = 25f;

        [Header("Revival")]
        [Tooltip("Invulnerability (s) granted when revived, so a returning player is not hit on the spot.")]
        [Min(0f)] [SerializeField] private float revivalGraceSeconds = 1f;

        [Tooltip("Blend time (s) from ragdoll back to animation when revived before reaching the outfield.")]
        [Min(0f)] [SerializeField] private float ragdollRecoverBlend = 0.35f;

        [Header("Safety")]
        [Tooltip("A delayed elimination that nobody commits or cancels within this time (s) is committed automatically.")]
        [Min(0.5f)] [SerializeField] private float maxPendingEliminationSeconds = 6f;

        // ------------------------------------------------------------------ contract state

        public float MaxHp { get; private set; } = Core.GameConstants.DefaultMaxHp;
        public float CurrentHp { get; private set; } = Core.GameConstants.DefaultMaxHp;
        public float Normalized => MaxHp <= 0f ? 0f : CurrentHp / MaxHp;

        /// <summary>HP above zero and not eliminated this round.</summary>
        public bool IsAlive => CurrentHp > 0f && !IsEliminated;

        /// <summary>Eliminated (in the outfield or transitioning there).</summary>
        public bool IsEliminated { get; private set; }

        /// <summary>A delayed elimination (Chrono) is pending.</summary>
        public bool IsEliminationPending { get; private set; }

        public event Action<PlayerHealth, HitContext, HitOutcome> HitReceived;
        public event Action<PlayerHealth, EliminationContext> EliminatedEvent;
        public event Action<PlayerHealth> Revived;

        // ------------------------------------------------------------------ additional state

        public DodgeballPlayer Owner => _owner;

        /// <summary>Context of the last elimination (or of the pending one while <see cref="IsEliminationPending"/>).</summary>
        public EliminationContext LastEliminationContext { get; private set; }

        /// <summary>Seconds the current delayed elimination has been pending.</summary>
        public float PendingEliminationTime => IsEliminationPending ? _pendingTime : 0f;

        // ------------------------------------------------------------------ internals

        private readonly List<IIncomingHitFilter> _filters = new List<IIncomingHitFilter>(4);
        private readonly List<IEliminationInterceptor> _interceptors = new List<IEliminationInterceptor>(2);
        private readonly List<IIncomingHitFilter> _filterScratch = new List<IIncomingHitFilter>(4);
        private readonly List<IEliminationInterceptor> _interceptorScratch = new List<IEliminationInterceptor>(2);

        private DodgeballPlayer _owner;
        private float _hpBeforePending;
        private float _pendingTime;
        private bool _eliminating;

        // ------------------------------------------------------------------ setup

        public void Initialize(DodgeballPlayer owner, float maxHp)
        {
            _owner = owner;
            MaxHp = Mathf.Max(1f, maxHp);
            CurrentHp = MaxHp;
            IsEliminated = false;
            IsEliminationPending = false;
            _pendingTime = 0f;
            _hpBeforePending = MaxHp;
        }

        public void SetMaxHp(float maxHp, bool refill)
        {
            MaxHp = Mathf.Max(1f, maxHp);
            if (refill && !IsEliminated) CurrentHp = MaxHp;
            else CurrentHp = Mathf.Min(CurrentHp, MaxHp);
        }

        public void AddHitFilter(IIncomingHitFilter filter)
        {
            if (filter == null || _filters.Contains(filter)) return;
            // Stable insertion by priority (lower first, equal priorities keep registration order).
            int i = _filters.Count;
            while (i > 0 && _filters[i - 1].Priority > filter.Priority) i--;
            _filters.Insert(i, filter);
        }

        public void RemoveHitFilter(IIncomingHitFilter filter)
        {
            if (filter != null) _filters.Remove(filter);
        }

        public void AddEliminationInterceptor(IEliminationInterceptor interceptor)
        {
            if (interceptor == null || _interceptors.Contains(interceptor)) return;
            int i = _interceptors.Count;
            while (i > 0 && _interceptors[i - 1].Priority > interceptor.Priority) i--;
            _interceptors.Insert(i, interceptor);
        }

        public void RemoveEliminationInterceptor(IEliminationInterceptor interceptor)
        {
            if (interceptor != null) _interceptors.Remove(interceptor);
        }

        // ------------------------------------------------------------------ hits

        /// <summary>Main entry point for ball (and ability projectile) hits. See class docs for the pipeline.</summary>
        public HitOutcome ReceiveHit(ref HitContext hit)
        {
            if (hit.Victim == null) hit.Victim = _owner;
            if (hit.Point == Vector3.zero && _owner != null) hit.Point = _owner.ChestPosition;

            if (_owner == null || !_owner.IsTargetable) return HitOutcome.Ignored;

            // 1. Filters: invulnerability/evasion, shields, damage scaling.
            if (RunFilters(ref hit))
            {
                HitReceived?.Invoke(this, hit, HitOutcome.Negated);
                return HitOutcome.Negated;
            }

            // 2. Elsa's rule: a second hit on a frozen player eliminates.
            var status = _owner.Status;
            bool frozenKill = status != null && status.Has(StatusEffectType.Frozen);
            if (frozenKill) hit.ForceEliminate = true;

            // 3. Damage.
            float damage = Mathf.Max(0f, hit.Damage);
            float hpBefore = CurrentHp;
            CurrentHp = Mathf.Max(0f, CurrentHp - damage);

            HitOutcome outcome;
            if (CurrentHp > 0f && !hit.ForceEliminate)
            {
                outcome = HitOutcome.Damaged;
                PublishDamaged(hit.Attacker, damage, hit.Point);
                ApplyHitReaction(in hit);
            }
            else
            {
                // 4. Lethal: interceptors get a say, then elimination.
                var cause = frozenKill ? EliminationCause.Frozen : hit.IsAbilityHit ? EliminationCause.Ability : EliminationCause.BallHit;
                var context = new EliminationContext
                {
                    Victim = _owner,
                    Attacker = hit.Attacker,
                    Cause = cause,
                    Impulse = ComputeRagdollImpulse(in hit),
                    Point = hit.Point,
                    HasHit = true,
                    Hit = hit,
                };
                outcome = RunEliminationPipeline(ref context, false, hpBefore);
            }

            HitReceived?.Invoke(this, hit, outcome);
            return outcome;
        }

        /// <summary>Non-ball damage (e.g. turret shots). Runs the elimination pipeline if HP reaches 0.</summary>
        public HitOutcome ApplyDamage(float amount, DodgeballPlayer source, EliminationCause cause)
        {
            if (_owner == null || !IsAlive || IsEliminationPending) return HitOutcome.Ignored;
            if (amount <= 0f || float.IsNaN(amount)) return HitOutcome.Ignored;

            var hit = new HitContext
            {
                Attacker = source,
                Victim = _owner,
                Point = _owner.ChestPosition,
                Normal = source != null ? (source.Position - _owner.Position).normalized : -_owner.Forward,
                Damage = amount,
                IsAbilityHit = true,
            };

            if (RunFilters(ref hit))
            {
                HitReceived?.Invoke(this, hit, HitOutcome.Negated);
                return HitOutcome.Negated;
            }

            // A ball-type cause on a frozen player follows Elsa's second-hit rule too.
            var status = _owner.Status;
            bool frozenKill = cause == EliminationCause.BallHit && status != null && status.Has(StatusEffectType.Frozen);
            if (frozenKill) hit.ForceEliminate = true;

            float damage = Mathf.Max(0f, hit.Damage);
            float hpBefore = CurrentHp;
            CurrentHp = Mathf.Max(0f, CurrentHp - damage);

            HitOutcome outcome;
            if (CurrentHp > 0f && !hit.ForceEliminate)
            {
                outcome = HitOutcome.Damaged;
                PublishDamaged(source, damage, hit.Point);
                ApplyHitReaction(in hit);
            }
            else
            {
                var context = new EliminationContext
                {
                    Victim = _owner,
                    Attacker = source,
                    Cause = frozenKill ? EliminationCause.Frozen : cause,
                    Impulse = DefaultImpulse(source),
                    Point = hit.Point,
                    HasHit = true,
                    Hit = hit,
                };
                outcome = RunEliminationPipeline(ref context, false, hpBefore);
            }

            HitReceived?.Invoke(this, hit, outcome);
            return outcome;
        }

        // ------------------------------------------------------------------ elimination

        /// <summary>
        /// Eliminates immediately. When <paramref name="bypassInterceptors"/> is false the interceptors still get a say.
        /// Publishes PlayerEliminatedEvent.
        /// </summary>
        public HitOutcome Eliminate(EliminationCause cause, DodgeballPlayer attacker, Vector3 impulse, Vector3 point,
            bool bypassInterceptors = false)
        {
            if (_owner == null || IsEliminated) return HitOutcome.Ignored;

            var context = new EliminationContext
            {
                Victim = _owner,
                Attacker = attacker,
                Cause = cause,
                Impulse = impulse,
                Point = point == Vector3.zero ? _owner.ChestPosition : point,
                HasHit = false,
            };

            float hpBefore = CurrentHp > 0f ? CurrentHp : (IsEliminationPending ? _hpBeforePending : MaxHp);
            if (bypassInterceptors)
            {
                FinalizeElimination(in context);
                return HitOutcome.Eliminated;
            }
            return RunEliminationPipeline(ref context, false, hpBefore);
        }

        /// <summary>Resolves a pending (delayed) elimination now - used by Chrono's Delayed Impact when its timer expires.</summary>
        public void CommitPendingElimination(in EliminationContext context)
        {
            if (_owner == null || IsEliminated || !IsEliminationPending) return;

            var c = context;
            c.Victim = _owner;
            c.Cause = EliminationCause.DelayedImpact;
            if (c.Point == Vector3.zero) c.Point = _owner.ChestPosition;
            if (c.Impulse == Vector3.zero) c.Impulse = LastEliminationContext.Impulse != Vector3.zero ? LastEliminationContext.Impulse : DefaultImpulse(c.Attacker);
            // The delay already was the interceptor's say: resolve for real.
            FinalizeElimination(in c);
        }

        /// <summary>Marks a pending elimination as pending/not pending (called by interceptors that delay).</summary>
        public void SetEliminationPending(bool pending)
        {
            if (pending)
            {
                if (IsEliminated) return;
                if (!IsEliminationPending)
                {
                    if (CurrentHp > 0f) _hpBeforePending = CurrentHp;
                    _pendingTime = 0f;
                }
                IsEliminationPending = true;
                CurrentHp = 0f; // displayed as "pending" by the HUD
                return;
            }

            if (!IsEliminationPending) return;
            IsEliminationPending = false;
            _pendingTime = 0f;
            // Cancelled without an explicit revive: the fatal hit is undone.
            if (!IsEliminated && CurrentHp <= 0f) CurrentHp = Mathf.Clamp(_hpBeforePending, 1f, MaxHp);
        }

        /// <summary>Restores HP (fraction of max) and clears the eliminated flag. Raises Revived.</summary>
        public void Revive(float hpFraction = 1f)
        {
            if (_owner == null) return;
            bool wasEliminated = IsEliminated;

            CurrentHp = Mathf.Clamp(MaxHp * Mathf.Clamp01(hpFraction), 1f, MaxHp);
            IsEliminated = false;
            IsEliminationPending = false;
            _pendingTime = 0f;

            // Revived while still ragdolling in place (before the outfield transfer): stand back up.
            var fsm = _owner.StateMachine;
            if (fsm != null && fsm.IsIn(PlayerStateId.Incapacitated) && fsm.IncapacitationReason == IncapacitationReason.Eliminated)
            {
                fsm.ReleaseIncapacitation(IncapacitationReason.Eliminated);
                var ragdoll = _owner.Visual != null ? _owner.Visual.Ragdoll : null;
                if (ragdoll != null && ragdoll.IsRagdolled) ragdoll.Recover(ragdollRecoverBlend);
            }

            if (wasEliminated && revivalGraceSeconds > 0f && _owner.Status != null)
                _owner.Status.Apply(StatusEffectType.Invulnerable, revivalGraceSeconds, 1f, this);

            Revived?.Invoke(this);
        }

        /// <summary>Directly sets HP without events (time rewinds).</summary>
        public void SetHpSilently(float hp)
        {
            if (float.IsNaN(hp)) return;
            CurrentHp = Mathf.Clamp(hp, 0f, MaxHp);
        }

        public void ResetForRound()
        {
            CurrentHp = MaxHp;
            IsEliminated = false;
            IsEliminationPending = false;
            _pendingTime = 0f;
            _hpBeforePending = MaxHp;
            LastEliminationContext = default;
        }

        public void Tick(float deltaTime)
        {
            if (!IsEliminationPending || IsEliminated) return;

            // Safety net: an interceptor that delayed an elimination and then vanished (unequipped, destroyed) must not
            // leave the player in limbo at 0 HP forever.
            _pendingTime += deltaTime;
            if (_pendingTime >= maxPendingEliminationSeconds)
            {
                CommitPendingElimination(LastEliminationContext);
            }
        }

        // ------------------------------------------------------------------ internals

        /// <summary>Runs the invulnerability rule and the hit filters. Returns true if the hit was negated.</summary>
        private bool RunFilters(ref HitContext hit)
        {
            var status = _owner.Status;
            if (!hit.Unblockable && status != null && status.Has(StatusEffectType.Invulnerable))
            {
                hit.Cancelled = true;
                if (string.IsNullOrEmpty(hit.CancelReason)) hit.CancelReason = "Invulnerable";
                return true;
            }

            if (_filters.Count == 0) return hit.Cancelled;

            // Filters may remove themselves (one-shot shields): iterate over a snapshot.
            _filterScratch.Clear();
            _filterScratch.AddRange(_filters);
            for (int i = 0; i < _filterScratch.Count; i++)
            {
                var filter = _filterScratch[i];
                if (filter == null) continue;
                try
                {
                    filter.FilterHit(ref hit);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
                if (hit.Cancelled) break;
            }
            _filterScratch.Clear();
            return hit.Cancelled;
        }

        /// <summary>
        /// Asks every interceptor (priority order) whether it takes over the elimination, then eliminates if none did.
        /// <paramref name="hpBefore"/> is restored when an interceptor prevents the elimination without restoring HP itself.
        /// </summary>
        private HitOutcome RunEliminationPipeline(ref EliminationContext context, bool bypass, float hpBefore)
        {
            LastEliminationContext = context;

            if (!bypass && _interceptors.Count > 0)
            {
                _interceptorScratch.Clear();
                _interceptorScratch.AddRange(_interceptors);
                for (int i = 0; i < _interceptorScratch.Count; i++)
                {
                    var interceptor = _interceptorScratch[i];
                    if (interceptor == null) continue;

                    HitOutcome decision;
                    try
                    {
                        decision = interceptor.Intercept(this, ref context);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e, this);
                        continue;
                    }

                    if (decision == HitOutcome.EliminationDelayed)
                    {
                        _interceptorScratch.Clear();
                        // HP to give back if the delay ends in a cancellation (Chrono: a teammate caught a ball in time).
                        float restoreHp = hpBefore > 0f ? hpBefore : (IsEliminationPending ? _hpBeforePending : MaxHp);
                        SetEliminationPending(true); // no-op if the interceptor already marked it
                        _hpBeforePending = restoreHp;
                        CurrentHp = 0f;
                        LastEliminationContext = context;
                        return HitOutcome.EliminationDelayed;
                    }

                    if (decision == HitOutcome.EliminationPrevented)
                    {
                        _interceptorScratch.Clear();
                        // The interceptor (e.g. Specter's Time Reversal) normally restores HP itself via SetHpSilently.
                        if (!IsEliminated && CurrentHp <= 0f) CurrentHp = Mathf.Clamp(hpBefore, 1f, MaxHp);
                        return HitOutcome.EliminationPrevented;
                    }
                }
                _interceptorScratch.Clear();
            }

            FinalizeElimination(in context);
            return HitOutcome.Eliminated;
        }

        /// <summary>Commits an elimination: state, ragdoll, events. Interceptors already had their say.</summary>
        private void FinalizeElimination(in EliminationContext context)
        {
            if (IsEliminated || _eliminating || _owner == null) return;
            _eliminating = true;
            try
            {
                IsEliminated = true;
                IsEliminationPending = false;
                _pendingTime = 0f;
                CurrentHp = 0f;
                LastEliminationContext = context;

                // Frozen ice shatters, timed effects end (also releases a Frozen incapacitation first); permanent passive
                // markers stay. Ability-owned effects are cleaned up when Incapacitate interrupts the abilities below.
                if (_owner.Status != null) _owner.Status.ClearForReset();

                // Locks input, freezes the root motor, drops the held ball, interrupts abilities.
                if (_owner.StateMachine != null) _owner.StateMachine.Incapacitate(IncapacitationReason.Eliminated);

                // Impulse-driven ragdoll transition at the hit point (falls back to the defeat animation).
                var visual = _owner.Visual;
                if (visual != null)
                {
                    var ragdoll = visual.Ragdoll;
                    if (ragdoll != null && ragdoll.IsBuilt)
                    {
                        ragdoll.EnableRagdoll(context.Impulse, context.Point);
                    }
                    else if (visual.AnimatorDriver != null)
                    {
                        visual.AnimatorDriver.TriggerDefeat();
                    }
                }

                EliminatedEvent?.Invoke(this, context);
                GameEvents.Publish(new PlayerEliminatedEvent
                {
                    Player = _owner,
                    Attacker = context.Attacker,
                    Cause = context.Cause,
                    Impulse = context.Impulse,
                    Point = context.Point,
                });
            }
            finally
            {
                _eliminating = false;
            }
        }

        private void ApplyHitReaction(in HitContext hit)
        {
            // Knockback along the ball's travel (planar), scaled by the combat-provided impulse (m/s).
            if (hit.KnockbackImpulse > 0f && _owner.Motor != null)
            {
                Vector3 dir = hit.BallVelocity;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = -hit.Normal;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = -_owner.Forward;
                _owner.Motor.AddImpulse(dir.normalized * hit.KnockbackImpulse);
            }

            var visual = _owner.Visual;
            if (visual != null && visual.AnimatorDriver != null) visual.AnimatorDriver.TriggerHit();

            if (nonLethalHitStun > 0f && _owner.StateMachine != null) _owner.StateMachine.Stun(nonLethalHitStun);
        }

        private void PublishDamaged(DodgeballPlayer attacker, float damage, Vector3 point)
        {
            GameEvents.Publish(new PlayerDamagedEvent
            {
                Player = _owner,
                Attacker = attacker,
                Damage = damage,
                RemainingHp = CurrentHp,
                Point = point,
            });
        }

        /// <summary>Ball momentum (m * v) times the readability factor, lifted slightly and clamped.</summary>
        private Vector3 ComputeRagdollImpulse(in HitContext hit)
        {
            Vector3 v = hit.BallVelocity;
            if (v.sqrMagnitude < 1e-4f) return DefaultImpulse(hit.Attacker);

            Vector3 impulse = v * (ballMass * ragdollImpulseFactor);
            impulse += Vector3.up * (impulse.magnitude * ragdollUpwardBias);
            return Vector3.ClampMagnitude(impulse, maxRagdollImpulse);
        }

        private Vector3 DefaultImpulse(DodgeballPlayer source)
        {
            if (_owner == null) return Vector3.zero;
            Vector3 dir = source != null ? _owner.Position - source.Position : -_owner.Forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = -_owner.Forward;
            dir.Normalize();
            return (dir + Vector3.up * ragdollUpwardBias) * defaultEliminationImpulse;
        }
    }
}
