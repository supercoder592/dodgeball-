using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Where a bot wants to stand and how it gets there. Produces destinations for the Position / Attack behaviours
    /// (lanes that spread the team, distance from enemy ball holders, centre-line attack depth, outfield spots behind the
    /// enemies' backs), unpredictable strafing, Perlin "alive" wander, and the final planar move vector (arrival, body
    /// separation, confinement guard). Allocation-free.
    /// </summary>
    public sealed class BotSteering
    {
        private readonly float _noiseSeedX;
        private readonly float _noiseSeedZ;

        private float _strafeTarget;
        private float _strafeCurrent;
        private float _strafeSwitchAt;

        public BotSteering(BotRandom rng)
        {
            _noiseSeedX = rng.Range(0f, 1000f);
            _noiseSeedZ = rng.Range(0f, 1000f);
        }

        /// <summary>Current lateral strafe offset (m).</summary>
        public float StrafeOffset => _strafeCurrent;

        public void ResetStrafe()
        {
            _strafeTarget = 0f;
            _strafeCurrent = 0f;
            _strafeSwitchAt = 0f;
        }

        /// <summary>
        /// Advances the attack strafe: a lateral offset that changes direction at random intervals (with a bias for
        /// reversals - jukes) so throwers are hard to read.
        /// </summary>
        /// <param name="now">Time.time.</param>
        /// <param name="deltaTime">Frame time (scaled).</param>
        /// <param name="amplitude">Maximum offset (m).</param>
        /// <param name="rng">Per-bot random source.</param>
        /// <returns>The offset (m) along the court's X axis.</returns>
        public float UpdateStrafe(float now, float deltaTime, float amplitude, BotRandom rng)
        {
            if (now >= _strafeSwitchAt)
            {
                float next = rng.Sign() * amplitude * rng.Range(0.35f, 1f);
                if (Mathf.Sign(next) == Mathf.Sign(_strafeCurrent) && rng.Chance(0.6f)) next = -next;
                _strafeTarget = next;
                _strafeSwitchAt = now + rng.Range(0.35f, 1.1f);
            }
            _strafeCurrent = Mathf.MoveTowards(_strafeCurrent, _strafeTarget, 3.5f * deltaTime);
            return _strafeCurrent;
        }

        /// <summary>Slow Perlin wander offset (x = lateral, y = depth) so idle bots never stand perfectly still.</summary>
        public Vector2 Wander(float now, float amplitudeX, float amplitudeZ, float frequency)
        {
            float x = (Mathf.PerlinNoise(_noiseSeedX, now * frequency) - 0.5f) * 2f * amplitudeX;
            float z = (Mathf.PerlinNoise(_noiseSeedZ, now * frequency) - 0.5f) * 2f * amplitudeZ;
            return new Vector2(x, z);
        }

        // ------------------------------------------------------------------ destinations

        /// <summary>
        /// Defensive spot in the bot's own half: its lane across the width, preferred depth from the centre line (deeper
        /// when cautious), pushed away from enemies that hold a ball and from teammates, kept off the edges and corners.
        /// </summary>
        public Vector3 InfieldPositionSpot(DodgeballPlayer self, Bounds inner, BotDifficultyProfile profile, float aggression,
            float now, float awarenessRadius)
        {
            float s = BotWorld.SideSign(self.Team);
            var center = BotWorld.CourtCenter;
            float halfLength = BotWorld.CourtLength * 0.5f;
            float width = BotWorld.CourtWidth;

            int rank = BotWorld.RankInZone(self, out int count);
            float lane = count > 1 ? (rank - (count - 1) * 0.5f) * (width / (count + 0.5f)) : 0f;
            var wander = Wander(now, 0.9f, 0.7f, 0.22f);
            float depth = Mathf.Clamp(profile.preferredDepth - (aggression - 0.5f) * 2f + wander.y, 1.2f, halfLength - 0.8f);

            var spot = new Vector3(center.x + lane + wander.x, self.Position.y, center.z + s * depth);
            spot += HolderRepulsion(self, spot, profile.holderAvoidDistance, profile.cloakDetectionRadius, awarenessRadius);
            spot += TeammateRepulsion(self, spot, profile.teammateSpacing);

            // Avoid being cornered: the closer to an edge, the stronger the pull back toward the middle of the half.
            float edge = BotWorld.EdgeProximity(spot, inner);
            if (edge > 0.7f) spot = Vector3.Lerp(spot, new Vector3(inner.center.x, spot.y, inner.center.z), (edge - 0.7f) * 0.8f);
            return BotWorld.ClampPlanar(spot, inner);
        }

        /// <summary>
        /// Attack spot while holding a ball: near the centre line (depth from aggression), drifting toward the target's lane
        /// plus the strafe offset. From the outfield: behind the target, as close to the court as the strip allows.
        /// </summary>
        public Vector3 AttackSpot(DodgeballPlayer self, DodgeballPlayer target, Bounds inner, BotDifficultyProfile profile,
            float aggression, float minLineDepth, float maxLineDepth, float idealRangeMax, float strafe, float awarenessRadius)
        {
            var center = BotWorld.CourtCenter;
            var pos = self.Position;

            if (!self.IsInfield)
            {
                // Outfield: line up behind the target (its back) at the court-side edge of the strip.
                float courtSideZ = Mathf.Abs(inner.max.z - center.z) < Mathf.Abs(inner.min.z - center.z) ? inner.max.z : inner.min.z;
                var o = new Vector3(target.Position.x + strafe * 0.6f, pos.y, Mathf.Lerp(inner.center.z, courtSideZ, 0.7f));
                o += TeammateRepulsion(self, o, profile.teammateSpacing);
                return BotWorld.ClampPlanar(o, inner);
            }

            float s = BotWorld.SideSign(self.Team);
            float depth = Mathf.Lerp(maxLineDepth, minLineDepth, Mathf.Clamp01(aggression));
            if (BotWorld.PlanarDistance(pos, target.Position) > idealRangeMax) depth = minLineDepth;

            var spot = new Vector3(Mathf.Lerp(pos.x, target.Position.x, 0.5f) + strafe, pos.y, center.z + s * depth);

            // Cautious bots still respect other enemy ball holders while attacking.
            spot += HolderRepulsion(self, spot, profile.holderAvoidDistance, profile.cloakDetectionRadius, awarenessRadius) *
                    (0.5f * (1f - Mathf.Clamp01(aggression)));
            spot += TeammateRepulsion(self, spot, profile.teammateSpacing) * 0.5f;
            return BotWorld.ClampPlanar(spot, inner);
        }

        /// <summary>
        /// Outfield waiting spot: behind the infield enemies' centroid (ready to throw at their backs), spread from other
        /// outfield teammates, on the court side of the strip.
        /// </summary>
        public Vector3 OutfieldSpot(DodgeballPlayer self, Bounds inner, BotDifficultyProfile profile, float now)
        {
            var center = BotWorld.CourtCenter;
            float sumX = 0f;
            int n = 0;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!PlayerRegistry.AreEnemies(self, e) || !e.IsTargetable) continue;
                if (!BotWorld.IsPerceivable(self, e, profile.cloakDetectionRadius)) continue;
                sumX += e.Position.x;
                n++;
            }
            float x = n > 0 ? sumX / n : inner.center.x;

            int rank = BotWorld.RankInZone(self, out int count);
            if (count > 1) x += (rank - (count - 1) * 0.5f) * 2.5f;
            var wander = Wander(now, 0.8f, 0.3f, 0.3f);

            float courtSideZ = Mathf.Abs(inner.max.z - center.z) < Mathf.Abs(inner.min.z - center.z) ? inner.max.z : inner.min.z;
            var spot = new Vector3(x + wander.x, self.Position.y, Mathf.Lerp(inner.center.z, courtSideZ, 0.5f) + wander.y);
            spot += TeammateRepulsion(self, spot, profile.teammateSpacing);
            return BotWorld.ClampPlanar(spot, inner);
        }

        // ------------------------------------------------------------------ forces

        /// <summary>
        /// Offset pushing <paramref name="spot"/> away from every enemy holding a ball closer than
        /// <paramref name="avoidDistance"/>. Enemies with Gale's Silent Footsteps are ignored while behind the bot and
        /// outside <paramref name="awarenessRadius"/> (the bot does not hear them coming).
        /// </summary>
        public static Vector3 HolderRepulsion(DodgeballPlayer self, Vector3 spot, float avoidDistance, float cloakDetectionRadius,
            float awarenessRadius)
        {
            var push = Vector3.zero;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!PlayerRegistry.AreEnemies(self, e) || !e.IsInitialized || !BotWorld.HoldsBall(e)) continue;
                if (!BotWorld.IsPerceivable(self, e, cloakDetectionRadius)) continue;
                if (BotWorld.Has(e, StatusEffectType.SilentFootsteps))
                {
                    var toEnemy = BotWorld.PlanarDirection(self.Position, e.Position, self.Forward);
                    bool behind = Vector3.Dot(self.Forward, toEnemy) < -0.2f;
                    if (behind && BotWorld.PlanarDistance(self.Position, e.Position) > awarenessRadius) continue;
                }

                var away = spot - e.Position;
                away.y = 0f;
                float d = away.magnitude;
                if (d >= avoidDistance) continue;
                if (d < 1e-3f) away = self.Forward * -1f;
                else away /= d;
                push += away * ((avoidDistance - d) * 0.6f);
            }
            return push;
        }

        /// <summary>Offset pushing <paramref name="spot"/> away from same-zone teammates closer than <paramref name="spacing"/>.</summary>
        public static Vector3 TeammateRepulsion(DodgeballPlayer self, Vector3 spot, float spacing)
        {
            var push = Vector3.zero;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var m = all[i];
                if (!PlayerRegistry.AreTeammates(self, m) || m.Zone != self.Zone || !m.IsInitialized) continue;
                var away = spot - m.Position;
                away.y = 0f;
                float d = away.magnitude;
                if (d >= spacing) continue;
                away = d < 1e-3f ? Vector3.Cross(Vector3.up, self.Forward) : away / d;
                push += away * ((spacing - d) * 0.5f);
            }
            return push;
        }

        /// <summary>Short-range push away from any nearby body (avoids running into people).</summary>
        public static Vector3 Separation(DodgeballPlayer self, float radius)
        {
            var push = Vector3.zero;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p == self || !p.IsInitialized) continue;
                var away = self.Position - p.Position;
                away.y = 0f;
                float d = away.magnitude;
                if (d >= radius || d < 1e-4f) continue;
                push += away / d * (1f - d / radius);
            }
            return push;
        }

        // ------------------------------------------------------------------ move vector

        /// <summary>
        /// Planar move vector (magnitude 0..1) toward <paramref name="to"/>, slowing down inside
        /// <paramref name="slowRadius"/> and stopping inside <paramref name="arriveRadius"/>.
        /// </summary>
        public static Vector3 Seek(Vector3 from, Vector3 to, float arriveRadius, float slowRadius, out float distance)
        {
            var d = to - from;
            d.y = 0f;
            distance = d.magnitude;
            if (distance <= arriveRadius) return Vector3.zero;
            float magnitude = Mathf.Clamp01((distance - arriveRadius) / Mathf.Max(0.01f, slowRadius - arriveRadius));
            return d / distance * Mathf.Max(0.3f, magnitude);
        }

        /// <summary>Removes the outward component of <paramref name="move"/> when the bot is already at the confinement edge.</summary>
        public static Vector3 KeepInside(Vector3 position, Vector3 move, Bounds bounds, float margin)
        {
            var min = bounds.min;
            var max = bounds.max;
            if (position.x <= min.x + margin && move.x < 0f) move.x = 0f;
            if (position.x >= max.x - margin && move.x > 0f) move.x = 0f;
            if (position.z <= min.z + margin && move.z < 0f) move.z = 0f;
            if (position.z >= max.z - margin && move.z > 0f) move.z = 0f;
            return move;
        }
    }
}
