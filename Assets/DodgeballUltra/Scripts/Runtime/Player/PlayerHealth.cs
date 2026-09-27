using System;
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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHealth : MonoBehaviour
    {
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

        // ------------------------------------------------------------------ IMPLEMENT: Player module
        public void Initialize(DodgeballPlayer owner, float maxHp) => throw new NotImplementedException();

        public void SetMaxHp(float maxHp, bool refill) => throw new NotImplementedException();

        public void AddHitFilter(IIncomingHitFilter filter) => throw new NotImplementedException();
        public void RemoveHitFilter(IIncomingHitFilter filter) => throw new NotImplementedException();
        public void AddEliminationInterceptor(IEliminationInterceptor interceptor) => throw new NotImplementedException();
        public void RemoveEliminationInterceptor(IEliminationInterceptor interceptor) => throw new NotImplementedException();

        /// <summary>Main entry point for ball (and ability projectile) hits. See class docs for the pipeline.</summary>
        public HitOutcome ReceiveHit(ref HitContext hit) => throw new NotImplementedException();

        /// <summary>Non-ball damage (e.g. turret shots). Runs the elimination pipeline if HP reaches 0.</summary>
        public HitOutcome ApplyDamage(float amount, DodgeballPlayer source, EliminationCause cause) => throw new NotImplementedException();

        /// <summary>
        /// Eliminates immediately. When <paramref name="bypassInterceptors"/> is false the interceptors still get a say.
        /// Publishes PlayerEliminatedEvent.
        /// </summary>
        public HitOutcome Eliminate(EliminationCause cause, DodgeballPlayer attacker, Vector3 impulse, Vector3 point,
            bool bypassInterceptors = false) => throw new NotImplementedException();

        /// <summary>Resolves a pending (delayed) elimination now - used by Chrono's Delayed Impact when its timer expires.</summary>
        public void CommitPendingElimination(in EliminationContext context) => throw new NotImplementedException();

        /// <summary>Marks a pending elimination as pending/not pending (called by interceptors that delay).</summary>
        public void SetEliminationPending(bool pending) => throw new NotImplementedException();

        /// <summary>Restores HP (fraction of max) and clears the eliminated flag. Raises Revived.</summary>
        public void Revive(float hpFraction = 1f) => throw new NotImplementedException();

        /// <summary>Directly sets HP without events (time rewinds).</summary>
        public void SetHpSilently(float hp) => throw new NotImplementedException();

        public void ResetForRound() => throw new NotImplementedException();

        public void Tick(float deltaTime) => throw new NotImplementedException();
    }
}
