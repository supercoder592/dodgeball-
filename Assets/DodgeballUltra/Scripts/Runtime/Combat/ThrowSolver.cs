using System;
using DodgeballUltra.Abilities;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel) - converts <see cref="ThrowParams"/> into a launch velocity:
    /// iterative lead targeting on the target's current velocity + low-arc ballistic solution (Core.Ballistics) at
    /// <see cref="ThrowParams.FinalSpeed"/> with gravity * GravityScale. Falls back to a straight aim when unsolvable.
    /// <para>
    /// Aim priority (same as the web build's <c>ThrowSolver.solve</c>):
    /// <list type="number">
    /// <item><see cref="ThrowParams.Target"/> set: lead the target's chest (<see cref="PredictInterceptPoint"/>, planar
    /// target velocity only - jumps are not led) and solve the low (fast, flat) ballistic arc through that point.</item>
    /// <item>Otherwise <see cref="ThrowParams.AimPoint"/> (the camera crosshair's world point / the bot's aim point) when it
    /// is a sensible distance away: low arc through it, so the ball arrives where the player aimed (parallax-free).</item>
    /// <item>Otherwise, and whenever the arc is unsolvable at this speed (point out of range), a straight throw along the
    /// aim (gravity then bends it down naturally, like a real weak throw).</item>
    /// </list>
    /// </para>
    /// <para>
    /// The live flight integrates the same gravity (<see cref="GameConstants.Gravity"/> * <see cref="DodgeBall.GravityScale"/>)
    /// and only a very light quadratic drag (<see cref="BallPhysicsTuning.liveQuadraticDrag"/>), so the closed-form solution
    /// arrives within a few centimetres of the aim point at dodgeball distances.
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class ThrowSolver
    {
        // ------------------------------------------------------------------ tuning (static, documented)

        /// <summary>
        /// Longest time (s) a target's motion is extrapolated when leading it. Beyond ~1.25 s a juke makes any prediction
        /// meaningless, and a long lead on a slow lob would send the ball far off the target.
        /// </summary>
        public static float MaxLeadTime = 1.25f;

        /// <summary>Fixed-point iterations used by <see cref="Solve"/> when leading a target (converges in 3-4 for ball speeds &gt;&gt; player speeds).</summary>
        public static int LeadIterations = 5;

        /// <summary>
        /// Minimum distance (m) between the release point and <see cref="ThrowParams.AimPoint"/> for the aim point to be used.
        /// Closer points (aiming at your own feet, degenerate camera hits) fall back to a straight throw along the aim.
        /// </summary>
        public static float MinAimPointDistance = 1f;

        /// <summary>Distance (m) used to estimate the flight time of a straight (unsolved) throw.</summary>
        public static float StraightThrowReferenceDistance = 12f;

        // ------------------------------------------------------------------ contract API

        /// <summary>Launch velocity for <paramref name="p"/>. <paramref name="flightTime"/> is the predicted time to target (or +inf).</summary>
        public static Vector3 Solve(in ThrowParams p, out float flightTime)
        {
            float speed = p.FinalSpeed;
            float gravity = GameConstants.Gravity * Mathf.Max(0f, p.GravityScale);
            Vector3 origin = p.Origin;
            flightTime = float.PositiveInfinity;

            if (speed <= 1e-4f || !IsFinite(origin)) return Vector3.zero;

            Vector3 velocity;
            float time;

            // 1) Target: lead its chest and solve the low arc through the intercept point.
            var target = p.Target;
            if (target != null)
            {
                Vector3 aim = PredictInterceptPoint(origin, target, speed, gravity, LeadIterations);
                if (SolveStatic(origin, aim, speed, gravity, out velocity, out time) && IsFinite(velocity))
                {
                    flightTime = time;
                    return velocity;
                }

                // Out of range at this speed: throw straight at the point (it drops short, like a real weak throw).
                return Straight(aim - origin, in p, speed, out flightTime);
            }

            // 2) Explicit aim point (crosshair world point / AI aim point).
            Vector3 aimPoint = p.AimPoint;
            if (IsFinite(aimPoint) && aimPoint != Vector3.zero &&
                (aimPoint - origin).sqrMagnitude >= MinAimPointDistance * MinAimPointDistance)
            {
                if (SolveStatic(origin, aimPoint, speed, gravity, out velocity, out time) && IsFinite(velocity))
                {
                    flightTime = time;
                    return velocity;
                }
                return Straight(aimPoint - origin, in p, speed, out flightTime);
            }

            // 3) Straight along the aim direction (else the thrower's facing, else world forward).
            return Straight(p.AimDirection, in p, speed, out flightTime);
        }

        /// <summary>Velocity that makes a projectile from <paramref name="origin"/> hit <paramref name="targetPoint"/> at <paramref name="speed"/>.</summary>
        /// <remarks>
        /// Low-arc solution of <see cref="Ballistics.SolveLaunchAngle"/> in the vertical plane through both points, rebuilt
        /// in 3D. <paramref name="gravity"/> is a positive magnitude (already multiplied by the gravity scale). Returns false
        /// when the point is out of range at this speed; <paramref name="velocity"/> then holds the 45-degree maximum-range
        /// throw toward the point and <paramref name="flightTime"/> its time to cover the horizontal distance.
        /// </remarks>
        public static bool SolveStatic(Vector3 origin, Vector3 targetPoint, float speed, float gravity, out Vector3 velocity, out float flightTime)
        {
            velocity = Vector3.zero;
            flightTime = float.PositiveInfinity;
            if (speed <= 1e-4f || !IsFinite(origin) || !IsFinite(targetPoint)) return false;

            Vector3 delta = targetPoint - origin;
            float horizontal = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);

            if (horizontal < 1e-4f)
            {
                // Straight up / straight down.
                velocity = new Vector3(0f, delta.y >= 0f ? speed : -speed, 0f);
                flightTime = Mathf.Abs(delta.y) / speed;
                return true;
            }

            bool solvable = Ballistics.SolveLaunchAngle(speed, horizontal, delta.y, Mathf.Max(0f, gravity), true, out float angle);
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            float inv = 1f / horizontal;
            velocity = new Vector3(delta.x * inv * cos * speed, sin * speed, delta.z * inv * cos * speed);
            flightTime = Ballistics.FlightTime(speed, angle, horizontal);
            return solvable;
        }

        /// <summary>Lead targeting: aim point where <paramref name="target"/> will be when the ball arrives.</summary>
        /// <remarks>
        /// Fixed-point iteration: solve the flight time to the current guess, move the guess to where the target's chest will
        /// be after that time (planar velocity only, capped at <see cref="MaxLeadTime"/>), repeat until it settles.
        /// <paramref name="gravity"/> is a positive magnitude (already scaled). Returns <paramref name="origin"/> when
        /// <paramref name="target"/> is null.
        /// </remarks>
        public static Vector3 PredictInterceptPoint(Vector3 origin, DodgeballPlayer target, float speed, float gravity, int iterations = 3)
        {
            if (target == null) return origin;
            Vector3 chest = target.ChestPosition;
            if (speed <= 1e-4f) return chest;

            Vector3 targetVelocity = target.Velocity;
            targetVelocity.y = 0f; // vertical motion (jumps) is never led: a jump arc is too short to predict usefully
            if (!IsFinite(targetVelocity) || targetVelocity.sqrMagnitude < 1e-6f) return chest;

            float maxLead = Mathf.Max(0f, MaxLeadTime);
            Vector3 aim = chest;
            float time = 0f;
            int count = Mathf.Max(1, iterations);
            for (int i = 0; i < count; i++)
            {
                SolveStatic(origin, aim, speed, gravity, out _, out float t);
                if (float.IsNaN(t) || float.IsInfinity(t)) t = maxLead;
                t = Mathf.Min(t, maxLead);
                aim = chest + targetVelocity * t;
                if (Mathf.Abs(t - time) < 1e-4f) break;
                time = t;
            }
            return aim;
        }

        // ------------------------------------------------------------------ helpers

        private static Vector3 Straight(Vector3 direction, in ThrowParams p, float speed, out float flightTime)
        {
            if (!IsFinite(direction) || direction.sqrMagnitude < 1e-8f) direction = p.AimDirection;
            if (!IsFinite(direction) || direction.sqrMagnitude < 1e-8f)
                direction = p.Thrower != null ? p.Thrower.Forward : Vector3.forward;
            direction.Normalize();
            flightTime = StraightThrowReferenceDistance / Mathf.Max(1e-3f, speed);
            return direction * speed;
        }

        internal static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }

    /// <summary>
    /// CONTRACT (kernel) - predicts ball flight (gravity-aware) for catch IK, AI dodging and Danger Sense.
    /// <para>
    /// The predictor steps the very integrator a live <see cref="DodgeBall"/> uses (semi-implicit Euler with
    /// <c>gravity * GravityScale</c>, the light quadratic air drag of <see cref="BallPhysicsTuning.liveQuadraticDrag"/> and the
    /// 220 km/h cap) at the physics rate, and tests every step as a swept segment - so even a 220 km/h ball (~1.2 m per
    /// 50 Hz step) can never skip over a body. Contacts are refined inside the step (analytic entry for spheres, bisection for
    /// capsules). Things that cannot be predicted (magnetic fields, homing payloads, a player changing direction) are not.
    /// </para>
    /// <para>
    /// A live ball stops being dangerous the moment it touches the floor (it becomes Free and the rally resets), so predictions
    /// end there when a <see cref="Court"/> exists. All queries are allocation-free.
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class TrajectoryPredictor
    {
        // ------------------------------------------------------------------ tuning (static, documented)

        /// <summary>Smallest integration step (s) used by the predictor (the physics step is clamped to [min, max]).</summary>
        public static float MinStep = 1f / 240f;

        /// <summary>Largest integration step (s) used by the predictor.</summary>
        public static float MaxStep = 1f / 30f;

        /// <summary>Bisection iterations used to refine a capsule contact inside one step (12 = sub-millimetre).</summary>
        public static int RefineIterations = 12;

        /// <summary>Capsule used for players that have no CapsuleCollider (m).</summary>
        public static float FallbackBodyRadius = 0.32f;

        /// <summary>Standing height used for players that have no CapsuleCollider (m).</summary>
        public static float FallbackBodyHeight = 1.8f;

        // ------------------------------------------------------------------ contract API

        /// <summary>Position of a projectile after <paramref name="t"/> seconds.</summary>
        public static Vector3 PositionAt(Vector3 position, Vector3 velocity, float gravity, float t)
            => position + velocity * t + 0.5f * t * t * gravity * Vector3.down;

        /// <summary>
        /// Earliest time within <paramref name="maxTime"/> at which the ball comes within <paramref name="radius"/> of
        /// <paramref name="point"/> (sampled + refined). Returns false if it never does.
        /// </summary>
        /// <remarks>
        /// Held and despawned balls never "reach" anything; a ball in stasis only counts when it already is within reach.
        /// Free balls use full gravity and roll along the floor. <paramref name="ballPositionAtTime"/> is the ball centre at
        /// the returned time.
        /// </remarks>
        public static bool TimeToReach(DodgeBall ball, Vector3 point, float radius, float maxTime, out float time, out Vector3 ballPositionAtTime)
        {
            time = 0f;
            ballPositionAtTime = Vector3.zero;
            if (ball == null || radius < 0f || !ThrowSolver.IsFinite(point)) return false;

            Vector3 p = ball.FlightPosition;
            ballPositionAtTime = p;
            float r2 = radius * radius;
            if ((p - point).sqrMagnitude <= r2) return true;

            if (!GetFlightModel(ball, out Vector3 v, out float gravity, out float drag, out bool live)) return false;
            if (maxTime <= 0f) return false;

            float floorY = FloorHeight(out bool hasFloor);
            float ballRadius = ball.Radius;
            float dt = Step();
            float t = 0f;

            while (t < maxTime)
            {
                // Planar motion is a straight line (gravity is vertical, drag only slows it): once it recedes beyond the
                // radius it can never come back.
                if (IsRecedingPlanar(p, v, point, radius)) return false;
                // Below the sphere and falling: gravity will never lift it back up.
                if (v.y <= 0f && gravity >= 0f && p.y < point.y - radius) return false;

                float h = Mathf.Min(dt, maxTime - t);
                Vector3 p1 = Integrate(p, ref v, gravity, drag, h);

                if (hasFloor && p1.y - ballRadius <= floorY)
                {
                    if (live)
                    {
                        // The throw ends on the floor; it may still reach the point on the way down this step.
                        if (SegmentSphereEntry(p, p1, point, radius, out float sFloor))
                        {
                            time = t + sFloor * h;
                            ballPositionAtTime = Vector3.LerpUnclamped(p, p1, sFloor);
                            return true;
                        }
                        return false;
                    }
                    // Free ball: rolls along the floor.
                    p1.y = floorY + ballRadius;
                    if (v.y < 0f) v.y = 0f;
                }

                if (SegmentSphereEntry(p, p1, point, radius, out float s))
                {
                    time = t + s * h;
                    ballPositionAtTime = Vector3.LerpUnclamped(p, p1, s);
                    return true;
                }

                p = p1;
                t += h;
            }
            return false;
        }

        /// <summary>Predicted impact on <paramref name="player"/>'s body (capsule). False if the ball will miss.</summary>
        /// <remarks>
        /// Only Live and Free balls are predicted (a free ball cannot hurt anybody; callers filter on <see cref="DodgeBall.IsLive"/>).
        /// The player is assumed to stay where they are (no extrapolation - the question is "will it hit me if I do not
        /// move"). <paramref name="impactPoint"/> is the ball centre at the moment of contact (where the hands meet it).
        /// </remarks>
        public static bool PredictImpact(DodgeBall ball, DodgeballPlayer player, float maxTime, out float timeToImpact, out Vector3 impactPoint)
        {
            timeToImpact = 0f;
            impactPoint = Vector3.zero;
            if (ball == null || player == null || maxTime <= 0f) return false;
            if (ball.State != BallState.Live && ball.State != BallState.Free) return false;
            if (!GetFlightModel(ball, out Vector3 v, out float gravity, out float drag, out bool live)) return false;

            GetBodyCapsule(player, out Vector3 axisA, out Vector3 axisB, out float bodyRadius);
            float reach = bodyRadius + ball.Radius;
            float reach2 = reach * reach;
            Vector3 axisCentre = (axisA + axisB) * 0.5f;

            Vector3 p = ball.FlightPosition;
            if (SqrDistancePointSegment(p, axisA, axisB) <= reach2)
            {
                impactPoint = p;
                return true;
            }

            float floorY = FloorHeight(out bool hasFloor);
            float ballRadius = ball.Radius;
            float dt = Step();
            float t = 0f;

            while (t < maxTime)
            {
                if (IsRecedingPlanar(p, v, axisCentre, reach)) return false;
                if (v.y <= 0f && gravity >= 0f && p.y < axisA.y - reach) return false; // below the feet and falling

                float h = Mathf.Min(dt, maxTime - t);
                Vector3 p1 = Integrate(p, ref v, gravity, drag, h);

                bool floorContact = hasFloor && p1.y - ballRadius <= floorY;
                if (floorContact && !live)
                {
                    p1.y = floorY + ballRadius; // free ball rolling
                    if (v.y < 0f) v.y = 0f;
                }

                if (SegmentCapsuleEntry(p, p1, axisA, axisB, reach, out float s))
                {
                    timeToImpact = t + s * h;
                    impactPoint = Vector3.LerpUnclamped(p, p1, s);
                    return true;
                }

                if (floorContact && live) return false; // lands first: the throw is over

                p = p1;
                t += h;
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Velocity / gravity / drag of the ball's current motion. False for balls that do not fly (held, despawned).</summary>
        private static bool GetFlightModel(DodgeBall ball, out Vector3 velocity, out float gravity, out float drag, out bool live)
        {
            velocity = ball.Velocity;
            gravity = 0f;
            drag = 0f;
            live = false;
            switch (ball.State)
            {
                case BallState.Live:
                    live = true;
                    gravity = GameConstants.Gravity * Mathf.Max(0f, ball.GravityScale);
                    var manager = BallManager.Instance;
                    drag = (manager != null ? manager.Tuning : BallPhysicsTuning.Default).liveQuadraticDrag;
                    return ThrowSolver.IsFinite(velocity);
                case BallState.Free:
                    gravity = GameConstants.Gravity;
                    return ThrowSolver.IsFinite(velocity);
                case BallState.Stasis:
                    velocity = Vector3.zero; // frozen in place: only an "already there" query can succeed
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>One integration step exactly like DodgeBall.StepLive (semi-implicit Euler, quadratic drag, speed cap).</summary>
        private static Vector3 Integrate(Vector3 p, ref Vector3 v, float gravity, float drag, float h)
        {
            v.y -= gravity * h;
            if (drag > 0f)
            {
                float speed = v.magnitude;
                if (speed > 0.01f) v *= Mathf.Max(0f, 1f - drag * speed * h);
            }
            float max = GameConstants.MaxBallSpeedMs;
            float sqr = v.sqrMagnitude;
            if (sqr > max * max) v *= max / Mathf.Sqrt(sqr);
            return p + v * h;
        }

        private static float Step()
        {
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f || float.IsNaN(dt)) dt = 0.02f;
            return Mathf.Clamp(dt, Mathf.Max(1e-4f, MinStep), Mathf.Max(MinStep, MaxStep));
        }

        /// <summary>Court floor height (only when a Court exists; without one, predictions just run to maxTime).</summary>
        private static float FloorHeight(out bool hasFloor)
        {
            var court = Court.Instance;
            hasFloor = court != null;
            return hasFloor ? court.FloorY : float.NegativeInfinity;
        }

        /// <summary>True when the planar motion moves away from <paramref name="centre"/> while already outside <paramref name="radius"/>.</summary>
        private static bool IsRecedingPlanar(Vector3 p, Vector3 v, Vector3 centre, float radius)
        {
            float dx = p.x - centre.x, dz = p.z - centre.z;
            if (dx * dx + dz * dz <= radius * radius) return false;
            return dx * v.x + dz * v.z >= 0f;
        }

        /// <summary>World axis (bottom/top sphere centres) and radius of the player's body capsule.</summary>
        private static void GetBodyCapsule(DodgeballPlayer player, out Vector3 a, out Vector3 b, out float radius)
        {
            var capsule = player.Capsule;
            if (capsule != null)
            {
                Transform t = capsule.transform;
                Vector3 scale = t.lossyScale;
                float radial = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                radius = capsule.radius * radial;
                float half = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - radius);
                Vector3 centre = t.TransformPoint(capsule.center);
                a = centre + Vector3.down * half;
                b = centre + Vector3.up * half;
                return;
            }

            radius = FallbackBodyRadius;
            Vector3 feet = player.Position;
            a = feet + Vector3.up * radius;
            b = feet + Vector3.up * Mathf.Max(radius, FallbackBodyHeight - radius);
        }

        /// <summary>Earliest fraction of segment p0-&gt;p1 inside the sphere (0 if already inside). False if it never enters.</summary>
        private static bool SegmentSphereEntry(Vector3 p0, Vector3 p1, Vector3 centre, float radius, out float s)
        {
            s = 0f;
            Vector3 d = p1 - p0;
            Vector3 m = p0 - centre;
            float c = m.sqrMagnitude - radius * radius;
            if (c <= 0f) return true;
            float a = d.sqrMagnitude;
            if (a < 1e-12f) return false;
            float b = Vector3.Dot(m, d);
            if (b >= 0f) return false; // moving away
            float disc = b * b - a * c;
            if (disc < 0f) return false;
            float root = (-b - Mathf.Sqrt(disc)) / a;
            if (root < 0f || root > 1f) return false;
            s = root;
            return true;
        }

        /// <summary>
        /// Earliest fraction of the swept segment p0-&gt;p1 at which a sphere of radius <paramref name="reach"/> (ball + body
        /// radius) touches the capsule axis a-b. Closest approach of the two segments decides whether there is a contact;
        /// bisection on the (convex) distance along the ball segment finds the entry.
        /// </summary>
        private static bool SegmentCapsuleEntry(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, float reach, out float s)
        {
            s = 0f;
            float reach2 = reach * reach;
            if (SqrDistancePointSegment(p0, a, b) <= reach2) return true;

            float closest = ClosestSegmentSegment(p0, p1, a, b, out float sClosest);
            if (closest > reach2) return false;

            float lo = 0f, hi = sClosest;
            Vector3 d = p1 - p0;
            int iterations = Mathf.Clamp(RefineIterations, 1, 32);
            for (int i = 0; i < iterations; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (SqrDistancePointSegment(p0 + d * mid, a, b) <= reach2) hi = mid;
                else lo = mid;
            }
            s = hi;
            return true;
        }

        private static float SqrDistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
            return (p - (a + ab * t)).sqrMagnitude;
        }

        /// <summary>
        /// Squared distance between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection 5.1.9);
        /// <paramref name="s"/> is the parameter of the closest point on the first segment.
        /// </summary>
        private static float ClosestSegmentSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = d1.sqrMagnitude, e = d2.sqrMagnitude, f = Vector3.Dot(d2, r);
            float t;
            const float eps = 1e-12f;

            if (a <= eps && e <= eps)
            {
                s = 0f;
                return r.sqrMagnitude;
            }
            if (a <= eps)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float bb = Vector3.Dot(d1, d2);
                    float denom = a * e - bb * bb;
                    s = denom > eps ? Mathf.Clamp01((bb * f - c * e) / denom) : 0f;
                    t = (bb * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((bb - c) / a);
                    }
                }
            }
            Vector3 c1 = p1 + d1 * s;
            Vector3 c2 = p2 + d2 * t;
            return (c1 - c2).sqrMagnitude;
        }
    }

    /// <summary>
    /// CONTRACT (kernel) - aim assist / soft lock-on.
    /// <para>
    /// Candidates are enemy players that can currently be hit (<see cref="DodgeballPlayer.IsTargetable"/>: infield, alive, not
    /// mid-elimination), are not hidden from aim (Gale's cloak unless Revealed; Shadow's Mirage disguise, see
    /// <see cref="IgnoreObscuredTargets"/>) and are in line of sight (a ray against <see cref="GameLayers.CourtMask"/>:
    /// walls, bleachers). The cone is measured in the horizontal plane, so camera pitch never breaks the lock.
    /// </para>
    /// <para>Score (lower is better): <c>angle / maxAngle + DistanceWeight * distance / maxDistance</c>.</para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class TargetingSystem
    {
        // ------------------------------------------------------------------ tuning (static, documented)

        /// <summary>Weight of the distance term in the score (0 = pure angle). Web build: 0.35.</summary>
        public static float DistanceWeight = 0.35f;

        /// <summary>Maximum distance (m) of enemies considered by <see cref="CycleTarget"/>.</summary>
        public static float CycleMaxDistance = 42f;

        /// <summary>
        /// When true, an enemy disguised by Shadow's Mirage Formation (<see cref="StatusEffectType.Obscured"/>) is never
        /// soft-locked - the lock marker would otherwise reveal which body is real. Bots pick targets explicitly
        /// (<see cref="PlayerIntent.DesiredTarget"/>) and are unaffected.
        /// </summary>
        public static bool IgnoreObscuredTargets = true;

        /// <summary>Require an unobstructed line of sight (court geometry) for <see cref="FindBestTarget"/>.</summary>
        public static bool RequireLineOfSight = true;

        /// <summary>Distance (m) kept short of the target's chest by the line-of-sight ray (so the target's own props never count).</summary>
        public static float LineOfSightEndMargin = 0.25f;

        // ------------------------------------------------------------------ contract API

        /// <summary>
        /// Best enemy for <paramref name="thrower"/> inside a cone of <paramref name="maxAngle"/> degrees around
        /// <paramref name="aimDirection"/> within <paramref name="maxDistance"/>; scored by angle and distance and line of sight.
        /// </summary>
        public static DodgeballPlayer FindBestTarget(DodgeballPlayer thrower, Vector3 aimOrigin, Vector3 aimDirection, float maxAngle, float maxDistance)
        {
            if (thrower == null || !ThrowSolver.IsFinite(aimOrigin) || !ThrowSolver.IsFinite(aimDirection)) return null;
            float planarLen = Mathf.Sqrt(aimDirection.x * aimDirection.x + aimDirection.z * aimDirection.z);
            if (planarLen < 1e-5f) aimDirection = thrower.Forward; // looking straight up/down: use the body facing

            float maxA = Mathf.Max(0.1f, maxAngle);
            float maxD = Mathf.Max(0.1f, maxDistance);
            DodgeballPlayer best = null;
            float bestScore = float.PositiveInfinity;

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!IsCandidate(thrower, p)) continue;

                Vector3 chest = p.ChestPosition;
                Vector3 to = chest - aimOrigin;
                float distance = to.magnitude;
                if (distance > maxD) continue;

                float angle = PlanarAngle(aimDirection, to);
                if (angle > maxA) continue;
                if (RequireLineOfSight && !HasLineOfSight(aimOrigin, chest)) continue;

                float score = angle / maxA + DistanceWeight * distance / maxD;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Next target after <paramref name="current"/> in screen/angle order (cycle target input).</summary>
        /// <remarks>
        /// Order = signed planar angle from the aim direction (negative = left, positive = right). The next target is the
        /// first one to the right of <paramref name="current"/>, wrapping around to the leftmost. Without a (valid) current
        /// target, the enemy closest to the aim direction is returned. Allocation-free.
        /// </remarks>
        public static DodgeballPlayer CycleTarget(DodgeballPlayer thrower, DodgeballPlayer current, Vector3 aimOrigin, Vector3 aimDirection)
        {
            if (thrower == null || !ThrowSolver.IsFinite(aimOrigin) || !ThrowSolver.IsFinite(aimDirection)) return null;
            Vector3 forward = new Vector3(aimDirection.x, 0f, aimDirection.z);
            if (forward.sqrMagnitude < 1e-8f) forward = thrower.Forward;

            float maxD2 = CycleMaxDistance * CycleMaxDistance;
            var players = PlayerRegistry.All;

            bool currentValid = current != null && IsCandidate(thrower, current) &&
                                (current.ChestPosition - aimOrigin).sqrMagnitude <= maxD2;
            float currentAngle = currentValid ? SignedPlanarAngle(forward, current.ChestPosition - aimOrigin) : 0f;

            DodgeballPlayer next = null, leftmost = null, closest = null;
            float nextAngle = float.PositiveInfinity, leftmostAngle = float.PositiveInfinity, closestAbs = float.PositiveInfinity;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!IsCandidate(thrower, p)) continue;
                Vector3 to = p.ChestPosition - aimOrigin;
                if (to.sqrMagnitude > maxD2) continue;

                float a = SignedPlanarAngle(forward, to);
                if (Mathf.Abs(a) < closestAbs)
                {
                    closestAbs = Mathf.Abs(a);
                    closest = p;
                }
                if (a < leftmostAngle)
                {
                    leftmostAngle = a;
                    leftmost = p;
                }
                if (currentValid && p != current && a > currentAngle && a < nextAngle)
                {
                    nextAngle = a;
                    next = p;
                }
            }

            if (!currentValid) return closest;
            if (next != null) return next;
            return leftmost != null && leftmost != current ? leftmost : current; // wrap (or the only target left)
        }

        // ------------------------------------------------------------------ additional helpers

        /// <summary>Can <paramref name="thrower"/> soft-lock <paramref name="candidate"/> at all (ignoring cone and distance)?</summary>
        public static bool IsCandidate(DodgeballPlayer thrower, DodgeballPlayer candidate)
        {
            if (candidate == null || thrower == null || candidate == thrower) return false;
            if (!PlayerRegistry.AreEnemies(thrower, candidate) || !candidate.IsTargetable) return false;
            return !IsHiddenFromAim(candidate);
        }

        /// <summary>Cloaked and not revealed (Gale), or disguised among clones (Shadow, when <see cref="IgnoreObscuredTargets"/>).</summary>
        public static bool IsHiddenFromAim(DodgeballPlayer p)
        {
            var status = p != null ? p.Status : null;
            if (status == null) return false;
            if (status.Has(StatusEffectType.Cloaked) && !status.Has(StatusEffectType.Revealed)) return true;
            return IgnoreObscuredTargets && status.Has(StatusEffectType.Obscured);
        }

        /// <summary>True when no court geometry (walls, bleachers) blocks the segment <paramref name="from"/> -&gt; <paramref name="to"/>.</summary>
        public static bool HasLineOfSight(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float distance = d.magnitude;
            float length = distance - Mathf.Max(0f, LineOfSightEndMargin);
            if (length <= 0.05f) return true;
            return !Physics.Raycast(from, d / distance, length, GameLayers.CourtMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Unsigned angle (deg) between two directions projected on the ground plane (180 when either is ~zero).</summary>
        public static float PlanarAngle(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return 180f;
            return Vector3.Angle(a, b);
        }

        /// <summary>Signed planar angle (deg) from <paramref name="from"/> to <paramref name="to"/>; positive = clockwise seen from above (to the right).</summary>
        public static float SignedPlanarAngle(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y = 0f;
            if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }
    }

    /// <summary>
    /// CONTRACT (kernel) - applies a live-ball hit to a player: builds the HitContext, runs payload hooks,
    /// PlayerHealth.ReceiveHit, knockback, and publishes BallHitPlayerEvent (which feeds the Juice pipeline).
    /// <para>
    /// <code>
    /// ResolveHit(ball, victim):
    ///   hit = { Damage 100, BallVelocity, KnockbackImpulse = Tuning.KnockbackForSpeed(|v|), Unblockable, IsAbilityHit }
    ///   payload.OnHitPlayer(ref hit)  -> false / hit.Cancelled  => Negated (the default hit is suppressed)
    ///   else victim.Health.ReceiveHit(ref hit)   (filters, Frozen rule, damage, knockback + flinch on Damaged,
    ///                                             interceptors, elimination + ragdoll)
    ///   EliminationDelayed -> the body still takes the blow here (knockback + hit reaction)
    ///   no MatchManager    -> attacker ultimate: ultGainOnHit (+ ultGainOnEliminate)   (the match grants it otherwise)
    ///   publish BallHitPlayerEvent (JuiceManager: hitstop, shake, squash, flash; HUD; audio; VFX; Match)
    ///   payload.OnAfterHitPlayer(hit, outcome)
    /// </code>
    /// </para>
    /// <para>
    /// The knockback impulse of a non-lethal hit is applied by <see cref="PlayerHealth"/> (along the ball's travel) together
    /// with the flinch and <c>AnimatorDriver.TriggerHit</c>, so it is never applied twice. The resolver applies it itself only
    /// when Health does not react (no Health component, <see cref="HitOutcome.EliminationDelayed"/>).
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class HitResolver
    {
        // ------------------------------------------------------------------ tuning (static, documented)

        /// <summary>Damage of a standard ball hit (spec: 100 = one-hit elimination for 100 HP heroes).</summary>
        public static float StandardDamage = GameConstants.StandardHitDamage;

        // ------------------------------------------------------------------ contract API

        public static HitOutcome ResolveHit(DodgeBall ball, DodgeballPlayer victim, Vector3 point, Vector3 normal)
        {
            if (ball == null || victim == null) return HitOutcome.Ignored;

            var attacker = ball.LastThrower;
            // Your own ball never hurts you or your teammates (the ball's sweep filters this too).
            if (attacker != null && (attacker == victim || PlayerRegistry.AreTeammates(attacker, victim))) return HitOutcome.Ignored;

            Vector3 velocity = ball.Velocity;
            float speed = velocity.magnitude;
            var manager = BallManager.Instance;
            var tuning = manager != null ? manager.Tuning : BallPhysicsTuning.Default;

            var hit = new HitContext
            {
                Ball = ball,
                Attacker = attacker,
                Victim = victim,
                Point = ThrowSolver.IsFinite(point) && point != Vector3.zero ? point : victim.ChestPosition,
                Normal = normal.sqrMagnitude > 1e-8f ? normal.normalized : (speed > 1e-3f ? -velocity / speed : Vector3.up),
                BallVelocity = velocity,
                Damage = StandardDamage,
                IsAbilityHit = ball.IsAbilityBall,
                Unblockable = ball.Unblockable,
                ForceEliminate = false,
                KnockbackImpulse = tuning.KnockbackForSpeed(speed),
                Cancelled = false,
            };

            // 1. Payload pre-hit hook (freeze: no damage; beam: unblockable + heavy shove; ...).
            var payload = ball.Payload;
            bool keep = true;
            if (payload != null)
            {
                try { keep = payload.OnHitPlayer(ball, ref hit); }
                catch (Exception e) { Debug.LogException(e, ball); }
            }

            // 2. Health pipeline.
            HitOutcome outcome;
            if (!keep || hit.Cancelled)
            {
                outcome = HitOutcome.Negated;
            }
            else if (victim.Health != null)
            {
                outcome = victim.Health.ReceiveHit(ref hit);
            }
            else
            {
                // No Health component (partial scene): still read as a hit so the pipeline stays testable.
                outcome = victim.IsTargetable ? HitOutcome.Damaged : HitOutcome.Ignored;
                if (outcome == HitOutcome.Damaged) ApplyBodyReaction(victim, in hit);
            }

            // 3. Chrono's Delayed Impact keeps the victim in at 0 HP: the body still takes the blow.
            if (outcome == HitOutcome.EliminationDelayed) ApplyBodyReaction(victim, in hit);

            // 4. Play rewards when no match manager grants them from the event (sandbox scenes).
            bool landed = outcome == HitOutcome.Damaged || outcome == HitOutcome.Eliminated;
            if (landed && !CombatRuleValues.MatchOwnsPlayRewards && attacker != null && attacker.Abilities != null &&
                PlayerRegistry.AreEnemies(attacker, victim))
            {
                float gain = CombatRuleValues.UltGainOnHit + (outcome == HitOutcome.Eliminated ? CombatRuleValues.UltGainOnEliminate : 0f);
                if (gain > 0f) attacker.Abilities.AddUltimateCharge(gain, UltGainReason.HitLanded);
            }

            // 5. Juice pipeline + everybody else. Every hit reaches the JuiceManager (it filters Ignored/Negated itself).
            GameEvents.Publish(new BallHitPlayerEvent
            {
                Ball = ball,
                Attacker = attacker,
                Victim = victim,
                Point = hit.Point,
                Normal = hit.Normal,
                BallVelocity = hit.BallVelocity,
                SpeedKmh = hit.SpeedKmh,
                Damage = hit.Damage,
                Outcome = outcome,
                IsLocalPlayerInvolved = victim.IsLocalPlayer || (attacker != null && attacker.IsLocalPlayer),
            });

            // 6. Payload post-hit hook (freeze the victim, meteor shockwave, beam VFX...).
            if (payload != null)
            {
                try { payload.OnAfterHitPlayer(ball, in hit, outcome); }
                catch (Exception e) { Debug.LogException(e, ball); }
            }

            return outcome;
        }

        /// <summary>Ability damage without a ball (turret, shockwave). Publishes PlayerDamagedEvent via PlayerHealth.</summary>
        /// <remarks>
        /// <para>
        /// <paramref name="damage"/> &gt; 0 goes through <see cref="PlayerHealth.ApplyDamage"/> (Health's non-ball damage
        /// path, cause <see cref="EliminationCause.Ability"/>): hit filters and invulnerability, <see cref="PlayerDamagedEvent"/>,
        /// the flinch, and the elimination pipeline (interceptors, ragdoll). Elsa's "second hit while frozen" rule is a
        /// ball rule and does not apply here.
        /// </para>
        /// <para>
        /// <paramref name="damage"/> &lt;= 0 is a pure shove: it lands on any targetable player who is not
        /// <see cref="StatusEffectType.Invulnerable"/> (no HP change, no damage event, hit animation only).
        /// </para>
        /// <para>
        /// <paramref name="knockback"/> is a velocity change (m/s, world space, may point upward) applied as given when the hit
        /// connects (<see cref="HitOutcome.Damaged"/> or <see cref="HitOutcome.EliminationDelayed"/>); an eliminated victim
        /// ragdolls instead. <paramref name="point"/> is informational (Health reports the victim's chest as the hit point).
        /// No <see cref="BallHitPlayerEvent"/> is published (there is no ball).
        /// </para>
        /// </remarks>
        public static HitOutcome ResolveAbilityHit(DodgeballPlayer attacker, DodgeballPlayer victim, float damage, Vector3 point, Vector3 knockback)
        {
            if (victim == null) return HitOutcome.Ignored;
            if (!ThrowSolver.IsFinite(knockback)) knockback = Vector3.zero;
            if (float.IsNaN(damage) || damage < 0f) damage = 0f;
            float magnitude = knockback.magnitude;

            HitOutcome outcome;
            var health = victim.Health;
            if (damage > 0f && health != null)
            {
                outcome = health.ApplyDamage(damage, attacker, EliminationCause.Ability);
            }
            else if (!victim.IsTargetable)
            {
                outcome = HitOutcome.Ignored;
            }
            else if (victim.Status != null && victim.Status.Has(StatusEffectType.Invulnerable))
            {
                outcome = HitOutcome.Negated;
            }
            else
            {
                // Pure shove (or no Health component): it connects without HP loss; the body still reacts.
                outcome = HitOutcome.Damaged;
                var visual = victim.Visual;
                if (visual != null && visual.AnimatorDriver != null) visual.AnimatorDriver.TriggerHit();
            }

            if ((outcome == HitOutcome.Damaged || outcome == HitOutcome.EliminationDelayed) && magnitude > 1e-4f && victim.Motor != null)
                victim.Motor.AddImpulse(knockback);

            bool landed = outcome == HitOutcome.Damaged || outcome == HitOutcome.Eliminated;
            if (landed && damage > 0f && !CombatRuleValues.MatchOwnsPlayRewards && attacker != null &&
                attacker.Abilities != null && PlayerRegistry.AreEnemies(attacker, victim))
            {
                float gain = CombatRuleValues.UltGainOnHit + (outcome == HitOutcome.Eliminated ? CombatRuleValues.UltGainOnEliminate : 0f);
                if (gain > 0f) attacker.Abilities.AddUltimateCharge(gain, UltGainReason.HitLanded);
            }
            return outcome;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Knockback along the ball's (planar) travel + the hit animation, for outcomes PlayerHealth does not react to.</summary>
        private static void ApplyBodyReaction(DodgeballPlayer victim, in HitContext hit)
        {
            if (victim.Motor != null && hit.KnockbackImpulse > 0f)
            {
                Vector3 dir = hit.BallVelocity;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = -hit.Normal;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = -victim.Forward;
                victim.Motor.AddImpulse(dir.normalized * hit.KnockbackImpulse);
            }

            var visual = victim.Visual;
            if (visual != null && visual.AnimatorDriver != null) visual.AnimatorDriver.TriggerHit();
        }
    }
}
