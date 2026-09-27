using DodgeballUltra.Combat;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>What happened to one entity during a time rewind (used for VFX and diagnostics).</summary>
    public enum ChronoRewindResult
    {
        /// <summary>Nothing changed (no history, wrong state, excluded).</summary>
        Skipped = 0,
        /// <summary>Moved back to its recorded position (and velocity).</summary>
        Rewound,
        /// <summary>A live ball whose current throw had not happened yet: returned to its old position as a free ball.</summary>
        ThrowUndone,
        /// <summary>An ability projectile that did not exist yet: removed from play.</summary>
        Erased,
    }

    /// <summary>
    /// Shared time-manipulation helpers for Chrono's kit (Stasis Field targeting, Temporal Reset rewinds, AI threat checks).
    /// Everything here is allocation-free and relies only on kernel contracts (<see cref="TimeRewindRecorder"/>,
    /// <see cref="DodgeBall"/>, <see cref="DodgeballPlayer"/>, <see cref="BallManager"/>, <see cref="Court"/>).
    /// </summary>
    public static class ChronoTimeUtil
    {
        /// <summary>Sampling step (s) used for the gravity-aware threat look-ahead.</summary>
        private const float ThreatSampleStep = 0.05f;

        /// <summary>Tolerance (s) when comparing snapshot times with a ball's launch time.</summary>
        private const float LaunchTimeEpsilon = 0.02f;

        // ------------------------------------------------------------------ ball classification

        /// <summary>True when <paramref name="ball"/> was last thrown by an enemy of <paramref name="owner"/>.</summary>
        public static bool IsEnemyBall(DodgeBall ball, DodgeballPlayer owner)
        {
            if (ball == null || owner == null) return false;
            TeamId team = ball.ThrowerTeam;
            return team.IsValid() && owner.Team.IsValid() && team != owner.Team;
        }

        /// <summary>
        /// True when the live <paramref name="ball"/> endangers an infield, targetable member of <paramref name="team"/>:
        /// it is soft-locked on one of them, or its gravity-aware flight over the next <paramref name="lookAhead"/> seconds
        /// passes within <paramref name="radius"/> metres of one of their bodies.
        /// </summary>
        /// <param name="timeToThreat">Seconds until the closest approach to the endangered player (+inf when false).</param>
        public static bool IsThreateningTeam(DodgeBall ball, TeamId team, float lookAhead, float radius, out float timeToThreat)
        {
            timeToThreat = float.PositiveInfinity;
            if (ball == null || !ball.IsLive || !team.IsValid()) return false;

            Vector3 position = ball.transform.position;
            Vector3 velocity = ball.Velocity;
            float gravity = Mathf.Abs(Physics.gravity.y) * Mathf.Max(0f, ball.GravityScale);
            float r2 = radius * radius;
            bool threat = false;

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.Team != team || !p.IsTargetable) continue;

                // Body as a vertical segment from knees to head.
                Vector3 feet = p.Position;
                float bottom = feet.y + 0.35f;
                float top = p.HeadPosition.y;

                bool reaches = false;
                for (float t = 0f; t <= lookAhead; t += ThreatSampleStep)
                {
                    Vector3 s = TrajectoryPredictor.PositionAt(position, velocity, gravity, t);
                    float dy = s.y < bottom ? bottom - s.y : (s.y > top ? s.y - top : 0f);
                    float dx = s.x - feet.x;
                    float dz = s.z - feet.z;
                    if (dx * dx + dy * dy + dz * dz <= r2)
                    {
                        reaches = true;
                        if (t < timeToThreat) timeToThreat = t;
                        break;
                    }
                }

                if (!reaches && ball.LockedTarget == p)
                {
                    // A soft-locked throw counts even if the look-ahead window is too short to reach the victim.
                    reaches = true;
                    float d = Vector3.Distance(position, p.ChestPosition);
                    float speed = Mathf.Max(1f, velocity.magnitude);
                    timeToThreat = Mathf.Min(timeToThreat, d / speed);
                }

                threat |= reaches;
            }

            return threat;
        }

        /// <summary>
        /// The live enemy ball (relative to <paramref name="owner"/>) within <paramref name="range"/> of <paramref name="origin"/>
        /// that will reach one of <paramref name="owner"/>'s teammates (or the owner) the soonest. Balls that arrive in less
        /// than <paramref name="minTimeToThreat"/> seconds are ignored (too late to react to). Null when none.
        /// </summary>
        public static DodgeBall FindMostThreateningEnemyBall(DodgeballPlayer owner, Vector3 origin, float range, float lookAhead,
            float radius, float minTimeToThreat, out float timeToThreat)
        {
            timeToThreat = float.PositiveInfinity;
            var manager = BallManager.Instance;
            if (owner == null || manager == null) return null;

            var balls = manager.ActiveBalls;
            if (balls == null) return null;

            DodgeBall best = null;
            float range2 = range * range;
            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || !ball.IsLive || !IsEnemyBall(ball, owner)) continue;
                if ((ball.transform.position - origin).sqrMagnitude > range2) continue;
                if (!IsThreateningTeam(ball, owner.Team, lookAhead, radius, out float t)) continue;
                if (t < minTimeToThreat) continue;
                if (t < timeToThreat)
                {
                    timeToThreat = t;
                    best = ball;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------ zone geometry

        /// <summary>
        /// World-space bounds of the whole playing area: both infield halves and both outfield strips. Falls back to a
        /// generous box around the origin when there is no <see cref="Court"/>.
        /// </summary>
        public static Bounds GetPlayAreaBounds()
        {
            var court = Court.Instance;
            if (court == null) return new Bounds(Vector3.zero, new Vector3(9f, 10f, 24f));

            Bounds b = court.GetInfieldBounds(TeamId.Home);
            b.Encapsulate(court.GetInfieldBounds(TeamId.Away));
            b.Encapsulate(court.GetOutfieldBounds(TeamId.Home));
            b.Encapsulate(court.GetOutfieldBounds(TeamId.Away));
            return b;
        }

        /// <summary>Yaw rotation of the court (zones are aligned with the court lines).</summary>
        public static Quaternion CourtYaw()
        {
            var court = Court.Instance;
            return court != null ? Quaternion.Euler(0f, court.transform.eulerAngles.y, 0f) : Quaternion.identity;
        }

        /// <summary>
        /// True when <paramref name="position"/> lies inside a square zone of side <paramref name="size"/> centred on
        /// <paramref name="center"/> (aligned with <paramref name="zoneRotation"/>), from 1 m below the centre up to
        /// <paramref name="height"/> above it.
        /// </summary>
        public static bool IsInsideZone(Vector3 position, Vector3 center, Quaternion zoneRotation, float size, float height)
        {
            Vector3 local = Quaternion.Inverse(zoneRotation) * (position - center);
            float half = size * 0.5f;
            return Mathf.Abs(local.x) <= half && Mathf.Abs(local.z) <= half && local.y >= -1f && local.y <= height;
        }

        // ------------------------------------------------------------------ rewinds

        /// <summary>
        /// Moves <paramref name="player"/> back to where its <see cref="TimeRewindRecorder"/> saw it
        /// <paramref name="secondsAgo"/> seconds ago, clamped to the player's CURRENT zone confinement (so rewinds never
        /// move anyone across the centre line or out of the outfield). Players without history, ragdolling eliminated
        /// players and players held by another ability (grabbed / round transition) are skipped.
        /// </summary>
        public static ChronoRewindResult TryRewindPlayer(DodgeballPlayer player, float secondsAgo, bool restoreVelocity,
            out Vector3 from, out Vector3 to)
        {
            from = to = player != null ? player.Position : Vector3.zero;
            if (player == null || !player.IsInitialized || player.Rewind == null) return ChronoRewindResult.Skipped;

            var fsm = player.StateMachine;
            if (fsm != null && fsm.IsIn(PlayerStateId.Incapacitated))
            {
                var reason = fsm.IncapacitationReason;
                if (reason == IncapacitationReason.Eliminated || reason == IncapacitationReason.Grabbed ||
                    reason == IncapacitationReason.RoundTransition)
                    return ChronoRewindResult.Skipped;
            }

            if (!player.Rewind.TryGetSnapshot(secondsAgo, out RewindSnapshot snap)) return ChronoRewindResult.Skipped;

            to = AbilityUtil.ClampToPlayerZone(player, snap.Position);
            float floor = AbilityUtil.GroundPoint(to).y;
            if (to.y < floor) to.y = floor;

            player.Teleport(to, YawOnly(snap.Rotation, player.Rotation));

            if (restoreVelocity && player.Motor != null)
            {
                Vector3 planar = snap.Velocity;
                planar.y = 0f;
                player.Motor.SetPlanarVelocity(planar);
            }

            return ChronoRewindResult.Rewound;
        }

        /// <summary>
        /// Restores <paramref name="ball"/> to its state <paramref name="secondsAgo"/> seconds ago:
        /// <list type="bullet">
        /// <item>Live ball whose snapshot belongs to the current flight: recorded position and velocity, still live.</item>
        /// <item>Live match ball whose current throw had not happened yet: back to its recorded position with its recorded
        /// velocity, as a free ball (the throw is undone, rally reset).</item>
        /// <item>Live ability projectile that did not exist yet: recycled (erased from the timeline).</item>
        /// <item>Free / stasis balls: teleported to the recorded position with the recorded velocity.</item>
        /// <item>Held / despawned balls, and snapshots taken while despawned: skipped.</item>
        /// </list>
        /// </summary>
        public static ChronoRewindResult TryRewindBall(DodgeBall ball, float secondsAgo, out Vector3 from, out Vector3 to)
        {
            from = to = ball != null ? ball.transform.position : Vector3.zero;
            if (ball == null || ball.Rewind == null) return ChronoRewindResult.Skipped;

            BallState state = ball.State;
            if (state == BallState.Held || state == BallState.Despawned) return ChronoRewindResult.Skipped;
            if (!ball.Rewind.TryGetSnapshot(secondsAgo, out RewindSnapshot snap)) return ChronoRewindResult.Skipped;

            var recordedState = (BallState)snap.State;
            if (recordedState == BallState.Despawned) return ChronoRewindResult.Skipped;

            to = snap.Position;

            if (state == BallState.Live)
            {
                bool sameFlight = recordedState == BallState.Live && snap.Time >= ball.LaunchTime - LaunchTimeEpsilon;
                if (sameFlight)
                {
                    ball.TeleportTo(snap.Position, snap.Velocity);
                    return ChronoRewindResult.Rewound;
                }

                if (ball.IsAbilityBall)
                {
                    // The projectile was conjured after the rewind point: it never existed back then.
                    if (BallManager.Instance != null) BallManager.Instance.Recycle(ball);
                    else ball.MakeFree(Vector3.zero, true);
                    to = from;
                    return ChronoRewindResult.Erased;
                }

                // The throw had not happened yet: the ball goes back where it was, as a plain free ball.
                ball.TeleportTo(snap.Position, snap.Velocity);
                ball.MakeFree(snap.Velocity, true);
                return ChronoRewindResult.ThrowUndone;
            }

            // Free or Stasis: plain teleport, keeping the current state.
            ball.TeleportTo(snap.Position, state == BallState.Stasis ? Vector3.zero : snap.Velocity);
            return ChronoRewindResult.Rewound;
        }

        /// <summary>Yaw-only version of <paramref name="rotation"/> (falls back to <paramref name="fallback"/> when degenerate).</summary>
        public static Quaternion YawOnly(Quaternion rotation, Quaternion fallback)
        {
            Vector3 f = rotation * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f)
            {
                f = fallback * Vector3.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f) return Quaternion.identity;
            }
            return Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        /// <summary>Theme colour of <paramref name="player"/>'s hero (fallback: Chrono's clockwork gold).</summary>
        public static Color ThemeColor(DodgeballPlayer player)
        {
            if (player != null && player.Character != null) return player.Character.themeColor;
            return new Color(0.95f, 0.78f, 0.38f);
        }
    }
}
