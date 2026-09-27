using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel) - a physical foam dodgeball.
    /// <para>
    /// Live balls resolve contacts with a manual sphere sweep each FixedUpdate (layers: <see cref="GameLayers.BallSweepMask"/>)
    /// instead of relying on OnCollisionEnter, so 220 km/h balls never tunnel and catch timing is exact:
    /// <code>
    /// FixedUpdate (Live):
    ///   apply BallManager field effects (magnet, stasis) -> payload.OnTick
    ///   custom gravity (GravityScale) ; sweep from position along velocity*dt (radius = Radius)
    ///   first hit:
    ///     IBallHittable            -> OnBallHit response (Block / Absorb / Handled / PassThrough)
    ///     DodgeballPlayer (enemy)  -> victim.Combat.TryResolveCatch(ball, point, impactTime)  => caught?  (BallCaughtEvent)
    ///                                 else HitResolver.ResolveHit(...)                        (BallHitPlayerEvent)
    ///                                 Pierce balls continue; others deflect and become Free
    ///     DodgeballPlayer (mate)   -> passes are received (Combat.ReceivePass); other balls pass through teammates
    ///     court surface            -> reflect with restitution, BallBouncedEvent; floor => RallyCount = 0 and Free
    /// </code>
    /// Free balls are plain physics bodies (Rigidbody, CCD, bouncy material). Held balls are kinematic and follow the hand socket.
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class DodgeBall : MonoBehaviour
    {
        public int BallId { get; private set; }
        public BallState State { get; private set; } = BallState.Free;
        public BallStyle Style { get; private set; } = BallStyle.Standard;

        /// <summary>Player holding the ball (Held state) or null.</summary>
        public DodgeballPlayer Holder { get; private set; }

        /// <summary>Last player that threw this ball (kept after it lands, used for credit and team checks).</summary>
        public DodgeballPlayer LastThrower { get; private set; }

        public TeamId ThrowerTeam => LastThrower != null ? LastThrower.Team : TeamId.None;

        /// <summary>Consecutive catch+rethrow count without touching the floor (Rally Boost).</summary>
        public int RallyCount { get; private set; }

        public bool IsLive => State == BallState.Live;
        public bool IsFree => State == BallState.Free;

        /// <summary>True for temporary ability projectiles (meteor, beam, glue, freeze, turret shots): recycled after use, never picked up.</summary>
        public bool IsAbilityBall { get; private set; }

        public bool IsPass { get; private set; }
        public bool Unblockable { get; private set; }
        public bool Pierce { get; private set; }
        public float GravityScale { get; private set; } = 1f;
        public IBallPayload Payload { get; private set; }
        public DodgeballPlayer LockedTarget { get; private set; }

        /// <summary>Time.time when last launched.</summary>
        public float LaunchTime { get; private set; }
        public Vector3 LaunchOrigin { get; private set; }

        public Vector3 Velocity { get; private set; }
        public float Speed => Velocity.magnitude;
        public float SpeedKmh => Speed * Core.GameConstants.MsToKmh;

        /// <summary>Current collision radius (m). Base radius * RadiusMultiplier of the last throw.</summary>
        public float Radius { get; private set; } = Core.GameConstants.BallRadius;
        public float BaseRadius => Core.GameConstants.BallRadius;

        public Rigidbody Body { get; private set; }
        public SphereCollider SphereCollider { get; private set; }

        /// <summary>Child transform that holds the mesh; squash &amp; stretch scales this, never the physics root.</summary>
        public Transform VisualRoot { get; private set; }

        public TimeRewindRecorder Rewind { get; private set; }

        /// <summary>(ball, previous, current)</summary>
        public event Action<DodgeBall, BallState, BallState> StateChanged;

        // ------------------------------------------------------------------ IMPLEMENT: Combat module

        /// <summary>Called once by BallManager after instantiation: builds visuals (realistic PBR foam ball), physics, recorder.</summary>
        public void Initialize(int ballId, bool isAbilityBall, BallStyle style) => throw new NotImplementedException();

        /// <summary>Puts the ball in <paramref name="holder"/>'s hand (kinematic, follows <paramref name="socket"/>).</summary>
        public void AttachTo(DodgeballPlayer holder, Transform socket) => throw new NotImplementedException();

        /// <summary>Launches the ball as Live with <paramref name="velocity"/>. Sets thrower, rally, payload, flags from <paramref name="p"/>.</summary>
        public void Launch(in ThrowParams p, Vector3 velocity) => throw new NotImplementedException();

        /// <summary>Makes the ball a plain Free physics ball with <paramref name="velocity"/> (drops, deflections). Calls payload.OnEnded if it was live.</summary>
        public void MakeFree(Vector3 velocity, bool resetRally) => throw new NotImplementedException();

        /// <summary>Changes the flight velocity while Live (magnets, curves, Chrono rewinds).</summary>
        public void SetVelocity(Vector3 velocity) => throw new NotImplementedException();

        /// <summary>Teleports the ball keeping its state (Houdini, Chrono rewind).</summary>
        public void TeleportTo(Vector3 position, Vector3 velocity) => throw new NotImplementedException();

        /// <summary>Freezes the ball mid-air for <paramref name="duration"/> seconds (Chrono). Any player may grab it.</summary>
        public void EnterStasis(float duration) => throw new NotImplementedException();

        /// <summary>Removes the ball from play for <paramref name="duration"/> s then respawns at <paramref name="respawnPosition"/> (Houdini).</summary>
        public void Despawn(float duration, Vector3 respawnPosition) => throw new NotImplementedException();

        /// <summary>Resets everything and places the ball Free at <paramref name="position"/> (round start).</summary>
        public void ResetTo(Vector3 position) => throw new NotImplementedException();

        /// <summary>Changes the look/behaviour flavour (trail, emissive tint).</summary>
        public void SetStyle(BallStyle style) => throw new NotImplementedException();

        /// <summary>True when <paramref name="player"/> is allowed to pick this ball up right now.</summary>
        public bool CanBePickedUpBy(DodgeballPlayer player) => throw new NotImplementedException();

        /// <summary>Overrides the rally count (e.g. counter throws increment it).</summary>
        public void SetRallyCount(int rallyCount) => throw new NotImplementedException();
    }
}
