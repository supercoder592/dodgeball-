using System;
using DodgeballUltra.Abilities;
using DodgeballUltra.Characters;
using DodgeballUltra.Combat;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - hub component of a player. Owns identity (team, hero, zone) and drives the deterministic
    /// per-frame update order of its sub-systems:
    /// <code>
    /// Update:      intent = IntentSource.Sample()  -> Status.Tick -> StateMachine.Tick -> Combat.Tick -> Abilities.Tick -> Health.Tick
    /// FixedUpdate: StateMachine.FixedTick -> Motor.FixedTick
    /// </code>
    /// Sub-components (PlayerMotor, PlayerStateMachine, PlayerCombatController, PlayerHealth, StatusEffectController,
    /// AbilityController) MUST NOT implement Update/FixedUpdate themselves; they expose Tick/FixedTick that this class calls.
    /// Visual components (CharacterVisual and children) run on their own LateUpdate/OnAnimatorIK.
    /// <para>Owner module: Player. Public members below are the contract; keep them.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class DodgeballPlayer : MonoBehaviour
    {
        // ------------------------------------------------------------------ identity
        public int PlayerId { get; private set; }
        public string DisplayName { get; private set; }
        public TeamId Team { get; private set; } = TeamId.None;
        public CharacterData Character { get; private set; }
        public HeroId Hero => Character != null ? Character.heroId : HeroId.Rayne;
        public bool IsHumanControlled { get; private set; }

        /// <summary>The player followed by the camera and HUD.</summary>
        public bool IsLocalPlayer { get; private set; }

        public CourtZone Zone { get; private set; } = CourtZone.Infield;
        public bool IsInfield => Zone == CourtZone.Infield;

        /// <summary>True once <see cref="Initialize"/> ran.</summary>
        public bool IsInitialized { get; private set; }

        // ------------------------------------------------------------------ sub-systems (assigned in Initialize / Awake)
        public Rigidbody Body { get; private set; }
        public CapsuleCollider Capsule { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public PlayerStateMachine StateMachine { get; private set; }
        public PlayerCombatController Combat { get; private set; }
        public PlayerHealth Health { get; private set; }
        public StatusEffectController Status { get; private set; }
        public AbilityController Abilities { get; private set; }
        public CharacterVisual Visual { get; private set; }
        public TimeRewindRecorder Rewind { get; private set; }

        /// <summary>Who drives this player. Can be swapped at runtime (e.g. bot takes over).</summary>
        public IIntentSource IntentSource { get; set; }

        /// <summary>Intent sampled this frame, already neutralised when <see cref="InputLocked"/> or the player cannot act.</summary>
        public PlayerIntent Intent { get; private set; }

        /// <summary>When true the intent source is ignored (countdowns, round transitions).</summary>
        public bool InputLocked { get; set; }

        /// <summary>Raised after the zone changes (Infield/Outfield).</summary>
        public event Action<DodgeballPlayer, CourtZone> ZoneChanged;

        // ------------------------------------------------------------------ spatial helpers
        public Vector3 Position => transform.position;
        public Quaternion Rotation => transform.rotation;

        /// <summary>Planar facing direction.</summary>
        public Vector3 Forward
        {
            get
            {
                var f = transform.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        public Vector3 Velocity => Motor != null ? Motor.Velocity : Vector3.zero;

        /// <summary>World point at chest height: the aim point for throws and the centre of the catch zone.</summary>
        public Vector3 ChestPosition => transform.position + Vector3.up * (Capsule != null ? Capsule.height * 0.72f : 1.3f);

        public Vector3 HeadPosition => transform.position + Vector3.up * (Capsule != null ? Capsule.height * 0.93f : 1.65f);

        /// <summary>Can currently be hit by enemy balls (infield, not mid-elimination).</summary>
        public bool IsTargetable =>
            IsInitialized && Zone == CourtZone.Infield && Health != null && Health.IsAlive &&
            (StateMachine == null || StateMachine.IncapacitationReason != IncapacitationReason.Eliminated);

        /// <summary>Can use abilities / throw / catch this frame.</summary>
        public bool CanAct => StateMachine != null && StateMachine.CanAct && !InputLocked;

        // ------------------------------------------------------------------ lifecycle (IMPLEMENT: Player module)

        /// <summary>
        /// Wires every sub-system for <paramref name="info"/>: sets identity, adds/gets components, builds the realistic
        /// character visual (CharacterVisual.Build), sets up abilities from CharacterData, registers in PlayerRegistry,
        /// sets layer <see cref="GameLayers.Player"/> and publishes PlayerSpawnedEvent.
        /// </summary>
        public void Initialize(in PlayerSpawnInfo info)
        {
            throw new NotImplementedException("Player module: DodgeballPlayer.Initialize");
        }

        /// <summary>Moves the player between Infield and Outfield (MatchManager decides when). Publishes PlayerZoneChangedEvent.</summary>
        public void SetZone(CourtZone zone)
        {
            throw new NotImplementedException("Player module: DodgeballPlayer.SetZone");
        }

        /// <summary>Instantly relocates the player (resets interpolation and planar velocity).</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            throw new NotImplementedException("Player module: DodgeballPlayer.Teleport");
        }

        /// <summary>Full reset between rounds: HP, statuses, state machine, abilities' round state, held ball dropped.</summary>
        public void ResetForRound(Vector3 position, Quaternion rotation)
        {
            throw new NotImplementedException("Player module: DodgeballPlayer.ResetForRound");
        }

        /// <summary>Raises <see cref="ZoneChanged"/>. For use by the implementation.</summary>
        private void RaiseZoneChanged(CourtZone zone) => ZoneChanged?.Invoke(this, zone);
    }
}
