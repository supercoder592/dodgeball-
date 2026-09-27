using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Houdini passive [Hat Trick]: pressing Pass makes the held ball vanish from Houdini's hand and reappear directly in
    /// the nearest teammate's hands - no lob, nothing for the enemy to intercept.
    /// <para>
    /// Implemented as the <see cref="IPassHandler"/> of Houdini's <see cref="PlayerCombatController"/> (assigned on equip,
    /// cleared on unequip). Receiver choice: the nearest teammate who can take the ball right now (hands free, not
    /// stunned / incapacitated / frozen), strictly preferring teammates in Houdini's own zone (infield to infield,
    /// outfield to outfield) and falling back to the other zone. When nobody can receive, the handler declines and the
    /// default lob pass runs.
    /// </para>
    /// <para>
    /// Presentation: a TeleportPoof at both ends and the Teleport SFX; <see cref="BallPassedEvent"/> is published with
    /// <c>Teleported = true</c> so HUD/AI/audio can tell it from a normal pass.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class HoudiniHatTrick : AbilityBase, IPassHandler
    {
        /// <summary>Score penalty that makes any same-zone receiver win over any cross-zone one.</summary>
        private const float CrossZonePenalty = 10000f;

        [Header("Receivers")]
        [Tooltip("Maximum distance (m) to a receiving teammate. 0 = anywhere in the arena.")]
        [Min(0f)] public float maxRange = 0f;

        [Tooltip("Prefer teammates in Houdini's own zone (infield/outfield) over closer ones in the other zone.")]
        public bool preferSameZone = true;

        [Tooltip("Outfield teammates may receive the teleported ball (classic infield/outfield passing play).")]
        public bool allowOutfieldReceivers = true;

        [Header("Presentation")]
        [Tooltip("Scale of the TeleportPoof smoke puff at each end.")]
        [Range(0.1f, 3f)] public float poofScale = 0.55f;

        [Tooltip("Tint of the smoke puffs (stage-magic smoke: neutral grey with a faint violet cast).")]
        public Color poofTint = new Color(0.62f, 0.58f, 0.68f, 1f);

        [Tooltip("Volume of the Teleport SFX at each end.")]
        [Range(0f, 1f)] public float sfxVolume = 0.85f;

        [NonSerialized] private PlayerCombatController _registeredOn;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public HoudiniHatTrick()
        {
        }

        /// <summary>Number of passes teleported since equip (stats / tests).</summary>
        public int TeleportedPasses { get; private set; }

        // ------------------------------------------------------------------ ability hooks

        protected override void OnEquip() => Register();

        protected override void OnUnequip() => Unregister();

        protected override void OnTick(float deltaTime)
        {
            // The combat controller may be created after equip, or its handler cleared by a reset: keep ours installed.
            var combat = Owner.Combat;
            if (combat != null && (combat != _registeredOn || combat.PassHandler == null)) Register();
        }

        protected override void OnCast()
        {
            // Passive: never cast.
        }

        /// <summary>Passives are never activated by the AI.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ IPassHandler

        /// <summary>Teleports <paramref name="ball"/> into the best receiver's hands. False = use the default lob.</summary>
        public bool TryHandlePass(DodgeBall ball, DodgeballPlayer from, DodgeballPlayer to)
        {
            if (ball == null || from == null || from.Combat == null) return false;
            if (ball.State != BallState.Held || (ball.Holder != null && ball.Holder != from)) return false;

            var receiver = SelectReceiver(from, to);
            if (receiver == null) return false;

            var fromCombat = from.Combat;
            Vector3 departure = ball.transform.position;

            // Release from Houdini's hand first so the combat controllers never both reference the ball.
            if (fromCombat.IsCharging) fromCombat.CancelCharge();
            if (fromCombat.HeldBall == ball) fromCombat.DropBall(Vector3.zero);
            receiver.Combat.GiveBall(ball);

            Vector3 arrival = receiver.Combat.GetThrowOrigin();
            VfxManager.Spawn(VfxId.TeleportPoof, departure, Quaternion.identity, poofScale, poofTint);
            VfxManager.Spawn(VfxId.TeleportPoof, arrival, Quaternion.identity, poofScale, poofTint);
            AudioManager.PlayAt(SfxId.Teleport, departure, sfxVolume);
            AudioManager.PlayAt(SfxId.Teleport, arrival, sfxVolume, 1.08f);

            TeleportedPasses++;
            GameEvents.Publish(new BallPassedEvent { Ball = ball, From = from, To = receiver, Teleported = true });
            return true;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Nearest teammate able to receive, same zone first. <paramref name="suggested"/> (the combat controller's choice)
        /// wins ties.
        /// </summary>
        private DodgeballPlayer SelectReceiver(DodgeballPlayer from, DodgeballPlayer suggested)
        {
            DodgeballPlayer best = null;
            float bestScore = float.PositiveInfinity;
            float maxSqr = maxRange > 0f ? maxRange * maxRange : float.PositiveInfinity;

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!CanReceive(from, p)) continue;

                Vector3 d = p.Position - from.Position;
                d.y = 0f;
                float sqr = d.sqrMagnitude;
                if (sqr > maxSqr) continue;

                float score = Mathf.Sqrt(sqr);
                if (preferSameZone && p.Zone != from.Zone) score += CrossZonePenalty;
                if (p == suggested) score -= 0.01f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>A teammate with free hands who is able to act (not stunned, incapacitated, frozen or mid-ragdoll).</summary>
        private bool CanReceive(DodgeballPlayer from, DodgeballPlayer p)
        {
            if (p == null || p == from || !p.IsInitialized || !p.gameObject.activeInHierarchy) return false;
            if (!PlayerRegistry.AreTeammates(from, p)) return false;
            if (p.Combat == null || p.Combat.HasBall) return false;
            if (!allowOutfieldReceivers && p.Zone == CourtZone.Outfield) return false;
            if (p.StateMachine != null && !p.StateMachine.CanAct) return false;
            if (p.Status != null && p.Status.Has(StatusEffectType.Frozen)) return false;
            return true;
        }

        private void Register()
        {
            var combat = Owner != null ? Owner.Combat : null;
            if (combat == null) return;
            if (_registeredOn != null && _registeredOn != combat) Unregister();

            // Never steal a handler somebody else installed on purpose.
            if (combat.PassHandler == null || ReferenceEquals(combat.PassHandler, this))
            {
                combat.PassHandler = this;
                _registeredOn = combat;
            }
        }

        private void Unregister()
        {
            if (_registeredOn != null && ReferenceEquals(_registeredOn.PassHandler, this)) _registeredOn.PassHandler = null;
            _registeredOn = null;
        }
    }
}
