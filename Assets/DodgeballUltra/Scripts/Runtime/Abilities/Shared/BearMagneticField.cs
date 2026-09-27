using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning of one <see cref="BearMagneticField"/> (copied from <see cref="BearMagneticPull"/>).</summary>
    [Serializable]
    public struct BearMagneticFieldSettings
    {
        /// <summary>Field radius around Bear's chest (m). Spec: 5 m.</summary>
        public float Radius;
        /// <summary>Homing rate (1/s) at the field edge; it triples toward the hands.</summary>
        public float HomingRate;
        /// <summary>Speed (m/s) a steered ball is eased down to as it arrives in the hands (a catchable "thunk").</summary>
        public float ArrivalSpeed;
        /// <summary>Minimum speed (m/s) a steered ball keeps, so slow lobs still fly into the hands.</summary>
        public float MinPullSpeed;
        /// <summary>Radius (m) of the magnetic catch zone around the hands/torso (Hittable sphere).</summary>
        public float CatchZoneRadius;
        /// <summary>How far (0..1) the catch zone centre sits from the chest toward the right hand.</summary>
        public float CatchZoneHandBias;
        /// <summary>Pull loose (Free) balls into empty hands.</summary>
        public bool PullFreeBalls;
        /// <summary>Acceleration (m/s^2) applied to a loose ball being pulled.</summary>
        public float FreeBallAcceleration;
        /// <summary>Top speed (m/s) of a loose ball being pulled.</summary>
        public float FreeBallMaxSpeed;
        /// <summary>Distance (m) from the hand at which a pulled loose ball is picked up.</summary>
        public float FreeBallCaptureDistance;
        /// <summary>Downward speed (m/s) of balls deflected because the hands are already full.</summary>
        public float DeflectDownSpeed;
        /// <summary>Fraction of the planar speed a deflected ball keeps.</summary>
        public float DeflectPlanarRetain;
        /// <summary>Tint of the deflection spark.</summary>
        public Color Tint;

        /// <summary>Spec defaults (5 m field).</summary>
        public static BearMagneticFieldSettings Default => new BearMagneticFieldSettings
        {
            Radius = 5f,
            HomingRate = 14f,
            ArrivalSpeed = 11f,
            MinPullSpeed = 7f,
            CatchZoneRadius = 0.75f,
            CatchZoneHandBias = 0.5f,
            PullFreeBalls = true,
            FreeBallAcceleration = 40f,
            FreeBallMaxSpeed = 7f,
            FreeBallCaptureDistance = 0.45f,
            DeflectDownSpeed = 4f,
            DeflectPlanarRetain = 0.12f,
            Tint = new Color(0.55f, 0.75f, 1f, 1f),
        };
    }

    /// <summary>
    /// Bear's [Magnetic Pull] field. Lives for the duration of the skill and does two jobs:
    /// <list type="number">
    /// <item><b>Steering</b> (<see cref="IBallFieldEffect"/>, registered with <see cref="BallManager"/>): every Live ball
    /// thrown by an enemy within <see cref="BearMagneticFieldSettings.Radius"/> has its velocity blended toward Bear's
    /// right hand with an exponential homing rate that rises as it closes in, while gravity is cancelled so the ball
    /// "floats" into the hands. Unblockable balls (Rayne's beam) ignore the field. Loose balls are levitated into empty
    /// hands from this component's own FixedUpdate.</item>
    /// <item><b>Magnetic catch</b> (<see cref="IBallHittable"/>): a kinematic sphere on <see cref="GameLayers.Hittable"/>
    /// around the hands/torso, so the ball's own sweep reaches it before Bear's body capsule. The first enemy match ball
    /// that arrives while Bear's hands are empty is caught - through <see cref="PlayerCombatController.TryResolveCatch"/>
    /// when Bear's catch stance is armed (so a well-timed input can still be Perfect), otherwise via
    /// <see cref="PlayerCombatController.GiveBall"/> plus a Normal-quality <see cref="BallCaughtEvent"/>. Every other enemy
    /// ball (hands full, ability projectiles) is slapped down to the floor and a <see cref="BallBlockedEvent"/> is raised.</item>
    /// </list>
    /// The object is not parented to the player (a child collider would join the player's compound rigidbody); it follows
    /// the hand with a kinematic rigidbody instead. Runs before the balls (<c>DefaultExecutionOrder(-80)</c>) so the catch
    /// zone is where the hand is when the balls sweep.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]
    [AddComponentMenu("")]
    public sealed class BearMagneticField : MonoBehaviour, IBallFieldEffect, IBallHittable
    {
        private DodgeballPlayer _owner;
        private BearMagneticFieldSettings _settings;
        private Rigidbody _body;
        private SphereCollider _zone;
        private bool _active;
        private bool _registered;
        private DodgeBall _freeTarget;
        private float _freeRetargetTimer;

        /// <summary>The Bear that owns the field.</summary>
        public DodgeballPlayer Owner => _owner;

        /// <summary>True until <see cref="Shutdown"/>.</summary>
        public bool IsActive => _active;

        /// <summary>Balls magnetically caught during this activation.</summary>
        public int BallsCaught { get; private set; }

        /// <summary>Balls slapped down because the hands were full.</summary>
        public int BallsDeflected { get; private set; }

        /// <summary>The ball the field is currently most interested in (for head LookAt), or null.</summary>
        public DodgeBall FocusBall { get; private set; }

        /// <summary>(ball, caught) - raised when a ball reaches the catch zone.</summary>
        public event Action<DodgeBall, bool> BallIntercepted;

        /// <inheritdoc />
        public TeamId OwnerTeam => _owner != null ? _owner.Team : TeamId.None;

        // ------------------------------------------------------------------ creation / teardown

        /// <summary>Creates and activates a field for <paramref name="owner"/>.</summary>
        public static BearMagneticField Create(DodgeballPlayer owner, in BearMagneticFieldSettings settings)
        {
            if (owner == null) return null;
            var go = new GameObject($"BearMagneticField_{owner.PlayerId}");
            go.layer = GameLayers.Hittable;
            var field = go.AddComponent<BearMagneticField>();
            field.Setup(owner, settings);
            return field;
        }

        private void Setup(DodgeballPlayer owner, in BearMagneticFieldSettings settings)
        {
            _owner = owner;
            _settings = settings;

            _body = gameObject.AddComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.useGravity = false;
            _body.interpolation = RigidbodyInterpolation.None;
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            _zone = gameObject.AddComponent<SphereCollider>();
            _zone.isTrigger = false; // ball sweeps usually ignore triggers
            _zone.radius = Mathf.Max(0.2f, settings.CatchZoneRadius);

            var centre = ComputeZoneCentre();
            transform.position = centre;
            _body.position = centre;

            _active = true;
            if (BallManager.Instance != null)
            {
                BallManager.Instance.RegisterFieldEffect(this);
                _registered = true;
            }
        }

        /// <summary>Stops the field, unregisters it and destroys the object. Safe to call more than once.</summary>
        public void Shutdown()
        {
            _active = false;
            FocusBall = null;
            _freeTarget = null;
            Unregister();
            if (this == null) return; // already destroyed (scene unload)
            if (_zone != null) _zone.enabled = false;
            Destroy(gameObject);
        }

        private void Unregister()
        {
            if (!_registered) return;
            _registered = false;
            if (BallManager.Instance != null) BallManager.Instance.UnregisterFieldEffect(this);
        }

        private void OnDestroy()
        {
            _active = false;
            Unregister();
        }

        // ------------------------------------------------------------------ per step

        private void FixedUpdate()
        {
            if (!_active) return;
            if (_owner == null)
            {
                Shutdown();
                return;
            }

            // Follow the hands (pose update is immediate for scene queries this step).
            _body.position = ComputeZoneCentre();

            PullLooseBall(Time.fixedDeltaTime);
        }

        /// <summary>Steers enemy live balls toward Bear's hand (called by every Live ball before it integrates).</summary>
        public void ApplyToBall(DodgeBall ball, float fixedDeltaTime)
        {
            if (!_active || _owner == null || ball == null || !ball.IsLive) return;
            if (ball.Unblockable || !ScrewsGadgetKit.IsEnemyBall(OwnerTeam, ball)) return;

            Vector3 hand = HandPosition();
            Vector3 pos = ball.transform.position;
            Vector3 toHand = hand - pos;
            float dist = toHand.magnitude;
            float radius = Mathf.Max(0.5f, _settings.Radius);
            if (dist > radius || dist < 1e-3f) return;

            FocusBall = ball;
            float closeness = 1f - dist / radius; // 0 at the edge, 1 in the hands

            Vector3 v = ball.Velocity;
            float speed = v.magnitude;
            // Keep the ball's momentum far out, ease it to a catchable speed near the hands.
            float targetSpeed = Mathf.Lerp(speed, Mathf.Min(speed, _settings.ArrivalSpeed), closeness);
            targetSpeed = Mathf.Max(targetSpeed, _settings.MinPullSpeed);
            Vector3 desired = toHand / dist * targetSpeed;

            // Exponential homing: frame-rate independent, stronger the closer the ball is (strong magnetic gradient).
            float rate = Mathf.Max(0f, _settings.HomingRate) * (0.35f + 2.65f * closeness);
            float blend = 1f - Mathf.Exp(-rate * fixedDeltaTime);
            Vector3 steered = Vector3.Lerp(v, desired, blend);

            // Cancel the gravity the ball is about to integrate this step so it floats into the hands.
            steered += Vector3.up * (GameConstants.Gravity * Mathf.Max(0f, ball.GravityScale) * fixedDeltaTime * closeness);
            ball.SetVelocity(steered);
        }

        /// <summary>Magnetic catch zone: catches the first enemy match ball, slaps the others down.</summary>
        public BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit)
        {
            if (!_active || _owner == null || ball == null || !ball.IsLive) return BallHitResponse.PassThrough;
            if (ball.Unblockable || !ScrewsGadgetKit.IsEnemyBall(OwnerTeam, ball)) return BallHitResponse.PassThrough;

            Vector3 point = hit.point;
            if (hit.distance <= 0f && point == Vector3.zero) point = ball.transform.position; // overlapping sweep start
            Vector3 normal = hit.normal.sqrMagnitude > 1e-4f ? hit.normal : -ball.Velocity.normalized;

            if (TryMagneticCatch(ball, point))
            {
                BallsCaught++;
                BallIntercepted?.Invoke(ball, true);
                return BallHitResponse.Handled;
            }

            Deflect(ball, point, normal);
            BallsDeflected++;
            BallIntercepted?.Invoke(ball, false);
            return BallHitResponse.Handled;
        }

        // ------------------------------------------------------------------ internals

        private bool TryMagneticCatch(DodgeBall ball, Vector3 point)
        {
            var combat = _owner.Combat;
            if (combat == null || combat.HasBall || combat.CatchingBlocked || ball.IsAbilityBall) return false;
            if (!_owner.CanAct || !_owner.IsInfield || _owner.Health == null || !_owner.Health.IsAlive) return false;

            // Snapshot before the ball changes state.
            var thrower = ball.LastThrower;
            float speedKmh = ball.SpeedKmh;
            int rally = ball.RallyCount;

            // A real, armed catch stance keeps the timing rule (a well-timed press can still be a Perfect Catch);
            // the combat pipeline then publishes the event and grants the rewards itself.
            if (combat.IsCatchArmed)
            {
                var quality = combat.TryResolveCatch(ball, point, Time.time);
                if (quality != CatchQuality.Miss) return true;
                // Miss (e.g. the ball came from behind the catch cone): the magnet still grabs it below,
                // unless the pipeline already changed the ball's state.
                if (!ball.IsLive || combat.HasBall) return combat.HeldBall == ball;
            }

            combat.GiveBall(ball);
            if (combat.HeldBall != ball) return false;

            // Magnetic catches have no input timing: report them as a normal-window catch.
            float reported = Mathf.Max(combat.PerfectCatchWindow + 0.01f, combat.Profile.catchWindow);
            GameEvents.Publish(new BallCaughtEvent
            {
                Ball = ball,
                Catcher = _owner,
                Thrower = thrower,
                Quality = CatchQuality.Normal,
                SecondsBeforeImpact = reported,
                Point = point,
                SpeedKmh = speedKmh,
                RallyCount = rally,
                IsLocalPlayerInvolved = _owner.IsLocalPlayer || (thrower != null && thrower.IsLocalPlayer),
            });
            AudioManager.PlayAt(SfxId.Magnet, point, 0.55f, 1.2f);
            return true;
        }

        private void Deflect(DodgeBall ball, Vector3 point, Vector3 normal)
        {
            Vector3 v = ball.Velocity;
            Vector3 planar = new Vector3(v.x, 0f, v.z) * Mathf.Clamp01(_settings.DeflectPlanarRetain);
            ball.MakeFree(planar + Vector3.down * Mathf.Max(0f, _settings.DeflectDownSpeed), true);

            GameEvents.Publish(new BallBlockedEvent { Ball = ball, Blocker = this, Point = point, Normal = normal });
            VfxManager.Spawn(VfxId.ShieldImpact, point, Quaternion.LookRotation(normal, Vector3.up), 0.55f, _settings.Tint);
            AudioManager.PlayAt(SfxId.Magnet, point, 0.7f, 0.85f);
        }

        /// <summary>Levitates the nearest loose ball into Bear's empty hands.</summary>
        private void PullLooseBall(float dt)
        {
            var combat = _owner.Combat;
            var manager = BallManager.Instance;
            if (!_settings.PullFreeBalls || manager == null || combat == null || combat.HasBall || !_owner.CanAct)
            {
                _freeTarget = null;
                return;
            }

            Vector3 hand = HandPosition();
            float radius = Mathf.Max(0.5f, _settings.Radius);

            _freeRetargetTimer -= dt;
            if (_freeTarget == null || !_freeTarget.IsFree || _freeRetargetTimer <= 0f)
            {
                _freeRetargetTimer = 0.1f;
                var candidate = manager.FindNearestBall(hand, null, radius);
                if (candidate != null && candidate.IsFree && !candidate.IsAbilityBall) _freeTarget = candidate;
                else if (_freeTarget != null && !_freeTarget.IsFree) _freeTarget = null;
            }

            var ball = _freeTarget;
            if (ball == null) return;
            var body = ball.Body;
            if (body == null || body.isKinematic) return;

            Vector3 toHand = hand - ball.transform.position;
            float dist = toHand.magnitude;
            if (dist > radius * 1.2f)
            {
                _freeTarget = null;
                return;
            }
            FocusBall = ball;

            if (dist <= Mathf.Max(0.1f, _settings.FreeBallCaptureDistance))
            {
                // Regular pick-up (events, rules); fall back to a direct hand-over if the pick-up rules refuse.
                if (!combat.TryPickup(ball) && ball.IsFree && !combat.HasBall) combat.GiveBall(ball);
                _freeTarget = null;
                return;
            }

            Vector3 desired = toHand / Mathf.Max(1e-3f, dist) * Mathf.Min(_settings.FreeBallMaxSpeed, dist * 3.5f + 1f);
            Vector3 velocity = Vector3.MoveTowards(body.GetVelocity(), desired, _settings.FreeBallAcceleration * dt);
            if (body.useGravity) velocity -= Physics.gravity * dt; // levitate: cancel this step's gravity
            body.SetVelocity(velocity);
        }

        /// <summary>Bear's right-hand socket (the combat release point / chest as fallbacks).</summary>
        private Vector3 HandPosition()
        {
            if (_owner == null) return transform.position;
            var visual = _owner.Visual;
            if (visual != null && visual.RightHandSocket != null) return visual.RightHandSocket.position;
            if (_owner.Combat != null) return _owner.Combat.GetThrowOrigin();
            return _owner.ChestPosition;
        }

        private Vector3 ComputeZoneCentre()
        {
            if (_owner == null) return transform.position;
            return Vector3.Lerp(_owner.ChestPosition, HandPosition(), Mathf.Clamp01(_settings.CatchZoneHandBias));
        }
    }
}
