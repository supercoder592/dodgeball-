using DodgeballUltra.Combat;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Rides on a ball thrown by Elsa (added by <see cref="ElsaFrostTrail"/> through <see cref="Attach"/>) and, while the
    /// ball is <see cref="BallState.Live"/>, lays <see cref="ElsaIceTrailSegment"/> ice patches every
    /// <see cref="ElsaIceTrailSettings.Spacing"/> metres along the ball's ground track (the trajectory projected straight
    /// down onto the court floor). Also keeps the attached <see cref="VfxId.IceTrail"/> effect (frost vapour and ice
    /// glitter streaming off the ball) running.
    /// <para>
    /// The component stays on the (pooled or match) ball after use but disables itself the moment the ball stops being
    /// live - caught, deflected, touching the floor, despawned or recycled - so a later throw by anybody else never lays
    /// ice unless Elsa's passive re-attaches it.
    /// </para>
    /// <para>
    /// A 220 km/h ball covers ~1 m per frame, so the emitter steps along the ground track in fixed spacing increments
    /// (up to <see cref="MaxSegmentsPerFrame"/> per frame) instead of spawning once per frame: the trail stays evenly
    /// spaced at any speed or frame rate.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElsaFrostTrailEmitter : MonoBehaviour
    {
        /// <summary>Safety cap on patches laid in one frame (hitch recovery).</summary>
        public const int MaxSegmentsPerFrame = 8;

        /// <summary>Maximum height (m) above the floor at which the ball still casts a trail on it.</summary>
        private const float MaxProjectionDistance = 25f;

        /// <summary>A projected point must be this close (m) to the court floor height to count (ignores bleachers).</summary>
        private const float FloorTolerance = 0.3f;

        private ElsaIceTrailSettings _settings;
        private Vector3 _lastDrop;
        private bool _hasLastDrop;
        private int _laid;
        private VfxHandle _trailVfx;

        /// <summary>The ball this emitter lives on.</summary>
        public DodgeBall Ball { get; private set; }

        /// <summary>Elsa (the thrower whose allies benefit).</summary>
        public DodgeballPlayer Owner { get; private set; }

        /// <summary>True while laying ice.</summary>
        public bool IsEmitting => enabled && Owner != null;

        /// <summary>Patches laid by the current throw.</summary>
        public int SegmentsLaid => _laid;

        /// <summary>
        /// Adds (or reuses) the emitter on <paramref name="ball"/> and starts a trail for <paramref name="owner"/>'s allies.
        /// Returns null when the ball is not live.
        /// </summary>
        public static ElsaFrostTrailEmitter Attach(DodgeBall ball, DodgeballPlayer owner, in ElsaIceTrailSettings settings,
            Color vfxTint, float vfxScale)
        {
            if (ball == null || owner == null || !ball.IsLive) return null;

            var emitter = ball.GetComponent<ElsaFrostTrailEmitter>();
            if (emitter == null) emitter = ball.gameObject.AddComponent<ElsaFrostTrailEmitter>();
            emitter.Begin(ball, owner, settings, vfxTint, vfxScale);
            return emitter;
        }

        /// <summary>Stops laying ice and stops the attached VFX. The patches already laid live out their lifetime.</summary>
        public void Detach()
        {
            VfxManager.StopEffect(_trailVfx);
            _trailVfx = default;
            Owner = null;
            _hasLastDrop = false;
            if (enabled) enabled = false;
        }

        private void Begin(DodgeBall ball, DodgeballPlayer owner, in ElsaIceTrailSettings settings, Color vfxTint, float vfxScale)
        {
            // A re-throw of the same ball restarts cleanly.
            VfxManager.StopEffect(_trailVfx);

            Ball = ball;
            Owner = owner;
            _settings = settings;
            _settings.Spacing = Mathf.Max(0.1f, _settings.Spacing);
            _laid = 0;
            _hasLastDrop = false;
            _trailVfx = VfxManager.SpawnAttached(VfxId.IceTrail, ball.transform, Vector3.zero, vfxScale, vfxTint);
            enabled = true;

            // Lay the first patch right at the release point so the trail starts at Elsa's feet.
            TryEmit();
        }

        private void Awake()
        {
            // Stay dormant until Attach() is called.
            if (Owner == null) enabled = false;
        }

        private void Update()
        {
            TryEmit();
        }

        private void OnDisable()
        {
            VfxManager.StopEffect(_trailVfx);
            _trailVfx = default;
        }

        private void TryEmit()
        {
            if (Ball == null || Owner == null || Ball.State != BallState.Live || Ball.LastThrower != Owner)
            {
                Detach();
                return;
            }

            if (!TryProjectToFloor(Ball.transform.position, out Vector3 ground))
            {
                // Over the stands / outside the arena: break the trail, resume when back over the court.
                _hasLastDrop = false;
                return;
            }

            if (!_hasLastDrop)
            {
                Lay(ground, PlanarDirection(Ball.Velocity));
                _lastDrop = ground;
                _hasLastDrop = true;
                return;
            }

            Vector3 delta = ground - _lastDrop;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance < _settings.Spacing) return;

            Vector3 dir = delta / distance;
            int steps = Mathf.Min(Mathf.FloorToInt(distance / _settings.Spacing), MaxSegmentsPerFrame);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = _lastDrop + dir * (_settings.Spacing * i);
                p.y = ground.y;
                Lay(p, dir);
                if (!enabled) return; // hit the per-throw cap
            }

            _lastDrop += dir * (_settings.Spacing * steps);
            _lastDrop.y = ground.y;
            // After a long hitch, snap forward instead of trying to catch up over many frames.
            if (distance > _settings.Spacing * (MaxSegmentsPerFrame + 1)) _lastDrop = ground;
        }

        private void Lay(Vector3 groundPoint, Vector3 direction)
        {
            ElsaIceTrailSegment.Spawn(groundPoint, direction, Owner, _settings);
            _laid++;
            if (_settings.MaxSegmentsPerThrow > 0 && _laid >= _settings.MaxSegmentsPerThrow) Detach();
        }

        private static Vector3 PlanarDirection(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        /// <summary>
        /// Projects <paramref name="position"/> straight down onto the court floor. Rejects bleachers, walls and anything
        /// outside the arena so ice only forms on the playing surface.
        /// </summary>
        private static bool TryProjectToFloor(Vector3 position, out Vector3 ground)
        {
            var court = Court.Instance;
            if (Physics.Raycast(position + Vector3.up * 0.05f, Vector3.down, out RaycastHit hit, MaxProjectionDistance,
                    GameLayers.GroundMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.8f)
            {
                ground = hit.point;
                if (court == null) return true;
                // Test slightly above the surface so float noise at floor level never reads as "below the floor".
                return Mathf.Abs(hit.point.y - court.FloorY) <= FloorTolerance && !court.IsOutOfArena(hit.point + Vector3.up * 0.1f);
            }

            // No floor collider (tests / minimal scenes): fall back to the analytic court plane.
            if (court != null && position.y >= court.FloorY)
            {
                ground = new Vector3(position.x, court.FloorY, position.z);
                return !court.IsOutOfArena(ground + Vector3.up * 0.1f);
            }

            ground = default;
            return false;
        }
    }
}
