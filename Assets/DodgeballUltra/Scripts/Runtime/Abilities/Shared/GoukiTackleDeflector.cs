using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// The "battering ram" volume in front of Gouki during [Tackle Intercept]: a kinematic sphere on
    /// <see cref="GameLayers.Hittable"/> that rides ahead of his chest (reaching <c>reach</c> metres in front of him).
    /// Enemy live balls flying into his path meet it before his body capsule and are knocked back toward the enemy half as
    /// <b>Free</b> balls (they can no longer hit anybody). Unblockable balls and balls of Gouki's team pass through.
    /// <para>
    /// It is a separate root object that follows Gouki (a child collider would join the player's compound rigidbody), and
    /// runs before the balls (<c>DefaultExecutionOrder(-80)</c>) so it is in place when they sweep.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]
    [AddComponentMenu("")]
    public sealed class GoukiTackleDeflector : MonoBehaviour, IBallHittable
    {
        private DodgeballPlayer _owner;
        private Rigidbody _body;
        private SphereCollider _sphere;
        private Vector3 _direction = Vector3.forward;
        private float _forwardOffset;
        private float _deflectSpeed;
        private float _deflectLift;
        private bool _active;

        /// <summary>Balls knocked away during this charge.</summary>
        public int Deflected { get; private set; }

        /// <summary>(ball, contact point) after a deflection.</summary>
        public event Action<DodgeBall, Vector3> BallDeflected;

        /// <inheritdoc />
        public TeamId OwnerTeam => _owner != null ? _owner.Team : TeamId.None;

        /// <summary>
        /// Creates the guard. The sphere of <paramref name="radius"/> is centred so its front reaches
        /// <paramref name="reach"/> metres ahead of the chest along <paramref name="direction"/>.
        /// </summary>
        public static GoukiTackleDeflector Create(DodgeballPlayer owner, Vector3 direction, float reach, float radius,
            float deflectSpeed, float deflectLift)
        {
            if (owner == null) return null;
            var go = new GameObject($"GoukiTackleGuard_{owner.PlayerId}");
            go.layer = GameLayers.Hittable;
            var guard = go.AddComponent<GoukiTackleDeflector>();
            guard._owner = owner;
            guard._deflectSpeed = Mathf.Max(0.5f, deflectSpeed);
            guard._deflectLift = Mathf.Clamp01(deflectLift);

            guard._body = go.AddComponent<Rigidbody>();
            guard._body.isKinematic = true;
            guard._body.useGravity = false;
            guard._body.interpolation = RigidbodyInterpolation.None;

            guard._sphere = go.AddComponent<SphereCollider>();
            guard._sphere.isTrigger = false;
            guard._sphere.radius = Mathf.Max(0.2f, radius);
            guard._forwardOffset = Mathf.Max(0f, reach - guard._sphere.radius);
            guard.SetDirection(direction);
            guard._active = true;

            var p = guard.ComputeCentre();
            go.transform.position = p;
            guard._body.position = p;
            return guard;
        }

        /// <summary>Charge direction (planar).</summary>
        public void SetDirection(Vector3 direction) => _direction = ScrewsGadgetKit.Planar(direction, _direction);

        /// <summary>Stops deflecting and destroys the guard. Safe to call more than once.</summary>
        public void Shutdown()
        {
            _active = false;
            if (this == null) return;
            if (_sphere != null) _sphere.enabled = false;
            Destroy(gameObject);
        }

        private void FixedUpdate()
        {
            if (!_active) return;
            if (_owner == null)
            {
                Shutdown();
                return;
            }
            _body.position = ComputeCentre();
        }

        private Vector3 ComputeCentre() => _owner != null
            ? _owner.ChestPosition + _direction * _forwardOffset + Vector3.down * 0.15f
            : transform.position;

        /// <inheritdoc />
        public BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit)
        {
            if (!_active || _owner == null || ball == null || !ball.IsLive) return BallHitResponse.PassThrough;
            if (ball.Unblockable || !ScrewsGadgetKit.IsEnemyBall(OwnerTeam, ball)) return BallHitResponse.PassThrough;

            Vector3 point = hit.point;
            if (hit.distance <= 0f && point == Vector3.zero) point = ball.transform.position;

            // Reflect off Gouki's shoulder line, then bias the rebound toward the enemy half with a little loft.
            Vector3 incoming = ball.Velocity.sqrMagnitude > 1e-4f ? ball.Velocity.normalized : -_direction;
            Vector3 rebound = Vector3.Reflect(incoming, _direction);
            Vector3 enemySide = _direction;
            var court = Court.Instance;
            if (court != null && OwnerTeam.IsValid()) enemySide = ScrewsGadgetKit.Planar(court.AttackDirection(OwnerTeam), _direction);
            rebound = ScrewsGadgetKit.Planar(rebound + enemySide * 0.8f, enemySide);
            rebound = (rebound * (1f - _deflectLift) + Vector3.up * _deflectLift).normalized;

            float speed = _deflectSpeed + (_owner.Motor != null ? _owner.Motor.PlanarSpeed * 0.4f : 0f);
            ball.MakeFree(rebound * speed, true);

            Deflected++;
            Vector3 normal = hit.normal.sqrMagnitude > 1e-4f ? hit.normal : -incoming;
            GameEvents.Publish(new BallBlockedEvent { Ball = ball, Blocker = this, Point = point, Normal = normal });
            VfxManager.Spawn(VfxId.TackleDust, point, Quaternion.LookRotation(rebound, Vector3.up), 0.7f);
            VfxManager.Spawn(VfxId.HitImpact, point, Quaternion.LookRotation(normal, Vector3.up), 0.6f);
            AudioManager.PlayAt(SfxId.BallBounceWall, point, 0.9f, 0.8f);
            BallDeflected?.Invoke(ball, point);
            return BallHitResponse.Handled;
        }

        private void OnDestroy() => _active = false;
    }
}
