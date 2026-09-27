using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Null-safe, allocation-free world queries shared by the AI: court geometry (with a regulation-court fallback when no
    /// <see cref="Court"/> exists, e.g. in tests), planar maths and player-state predicates.
    /// </summary>
    public static class BotWorld
    {
        /// <summary>Regulation fallback geometry (m) used only when the scene has no Court.</summary>
        public const float FallbackCourtLength = 18f;
        public const float FallbackCourtWidth = 9f;
        public const float FallbackOutfieldDepth = 3f;

        // ------------------------------------------------------------------ match

        /// <summary>True while balls are live (or when there is no MatchManager, e.g. a sandbox scene).</summary>
        public static bool IsMatchLive => MatchManager.Instance == null || MatchManager.Instance.IsPlaying;

        /// <summary>Seconds left in the round (+inf without a MatchManager).</summary>
        public static float RoundTimeRemaining =>
            MatchManager.Instance != null ? MatchManager.Instance.RoundTimeRemaining : float.PositiveInfinity;

        // ------------------------------------------------------------------ court geometry

        public static Vector3 CourtCenter => Court.Instance != null ? Court.Instance.Center : Vector3.zero;
        public static float CourtLength => Court.Instance != null ? Court.Instance.length : FallbackCourtLength;
        public static float CourtWidth => Court.Instance != null ? Court.Instance.width : FallbackCourtWidth;

        /// <summary>-1 for Home (defends -Z), +1 for Away.</summary>
        public static float SideSign(TeamId team)
        {
            var court = Court.Instance;
            if (court != null) return court.SideSign(team);
            return team == TeamId.Away ? 1f : -1f;
        }

        /// <summary>Unit vector from <paramref name="team"/>'s half toward the opponent's half.</summary>
        public static Vector3 AttackDirection(TeamId team)
        {
            var court = Court.Instance;
            if (court != null) return court.AttackDirection(team);
            return new Vector3(0f, 0f, -SideSign(team));
        }

        /// <summary>Movement confinement of <paramref name="player"/> (own half when infield, outfield strip otherwise).</summary>
        public static Bounds GetConfinement(DodgeballPlayer player)
        {
            var court = Court.Instance;
            if (court != null) return court.GetConfinement(player.Team, player.Zone);

            float s = SideSign(player.Team);
            const float half = FallbackCourtLength * 0.5f;
            if (player.Zone == CourtZone.Infield)
                return new Bounds(new Vector3(0f, 1f, s * half * 0.5f), new Vector3(FallbackCourtWidth, 2f, half));

            // Outfield strip lies behind the OPPONENT's baseline.
            return new Bounds(new Vector3(0f, 1f, -s * (half + FallbackOutfieldDepth * 0.5f)),
                new Vector3(FallbackCourtWidth, 2f, FallbackOutfieldDepth));
        }

        /// <summary>Shrinks <paramref name="b"/> on X/Z by <paramref name="margin"/> (never below 10 cm).</summary>
        public static Bounds ShrinkPlanar(Bounds b, float margin)
        {
            var e = b.extents;
            e.x = Mathf.Max(0.05f, e.x - margin);
            e.z = Mathf.Max(0.05f, e.z - margin);
            return new Bounds(b.center, e * 2f);
        }

        /// <summary>Clamps X/Z of <paramref name="p"/> into <paramref name="b"/> (Y untouched).</summary>
        public static Vector3 ClampPlanar(Vector3 p, Bounds b)
        {
            var min = b.min;
            var max = b.max;
            return new Vector3(Mathf.Clamp(p.x, min.x, max.x), p.y, Mathf.Clamp(p.z, min.z, max.z));
        }

        /// <summary>Planar distance from <paramref name="p"/> to the rectangle of <paramref name="b"/> (0 inside).</summary>
        public static float PlanarDistanceToBounds(Vector3 p, Bounds b)
        {
            var min = b.min;
            var max = b.max;
            float dx = Mathf.Max(0f, Mathf.Max(min.x - p.x, p.x - max.x));
            float dz = Mathf.Max(0f, Mathf.Max(min.z - p.z, p.z - max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Free distance from <paramref name="p"/> to the edge of <paramref name="b"/> along the planar direction
        /// <paramref name="dir"/> (ray-box exit distance). 0 when outside or moving outward from the edge.
        /// </summary>
        public static float RoomAlong(Vector3 p, Vector3 dir, Bounds b)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) return 0f;
            dir.Normalize();
            var min = b.min;
            var max = b.max;
            float room = float.PositiveInfinity;
            if (dir.x > 1e-5f) room = Mathf.Min(room, (max.x - p.x) / dir.x);
            else if (dir.x < -1e-5f) room = Mathf.Min(room, (min.x - p.x) / dir.x);
            if (dir.z > 1e-5f) room = Mathf.Min(room, (max.z - p.z) / dir.z);
            else if (dir.z < -1e-5f) room = Mathf.Min(room, (min.z - p.z) / dir.z);
            return float.IsPositiveInfinity(room) ? 0f : Mathf.Max(0f, room);
        }

        /// <summary>0 in the middle of <paramref name="b"/>, 1 on its edge (how "cornered" a point is on its worst axis).</summary>
        public static float EdgeProximity(Vector3 p, Bounds b)
        {
            var c = b.center;
            var e = b.extents;
            float nx = e.x > 1e-4f ? Mathf.Abs(p.x - c.x) / e.x : 0f;
            float nz = e.z > 1e-4f ? Mathf.Abs(p.z - c.z) / e.z : 0f;
            return Mathf.Clamp01(Mathf.Max(nx, nz));
        }

        // ------------------------------------------------------------------ planar maths

        public static Vector3 Planar(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        public static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Normalised planar direction from <paramref name="from"/> to <paramref name="to"/> (or <paramref name="fallback"/>).</summary>
        public static Vector3 PlanarDirection(Vector3 from, Vector3 to, Vector3 fallback)
        {
            var d = to - from;
            d.y = 0f;
            float m = d.magnitude;
            return m > 1e-4f ? d / m : fallback;
        }

        // ------------------------------------------------------------------ player predicates

        public static bool Has(DodgeballPlayer p, StatusEffectType type) => p != null && p.Status != null && p.Status.Has(type);

        public static PlayerStateId StateOf(DodgeballPlayer p) =>
            p != null && p.StateMachine != null ? p.StateMachine.Current : PlayerStateId.Grounded;

        public static bool HoldsBall(DodgeballPlayer p) => p != null && p.Combat != null && p.Combat.HasBall;

        public static bool IsCharging(DodgeballPlayer p) => p != null && p.Combat != null && p.Combat.IsCharging;

        /// <summary>Cloaked (Gale) and not revealed.</summary>
        public static bool IsCloaked(DodgeballPlayer p) => Has(p, StatusEffectType.Cloaked) && !Has(p, StatusEffectType.Revealed);

        /// <summary>
        /// Can <paramref name="observer"/> currently see <paramref name="target"/>? Cloaked enemies are only noticed inside
        /// <paramref name="cloakDetectionRadius"/>.
        /// </summary>
        public static bool IsPerceivable(DodgeballPlayer observer, DodgeballPlayer target, float cloakDetectionRadius)
        {
            if (target == null || !target.IsInitialized) return false;
            if (!IsCloaked(target)) return true;
            return observer != null && PlanarDistance(observer.Position, target.Position) <= cloakDetectionRadius;
        }

        /// <summary>Planar speed (m/s) of <paramref name="p"/>.</summary>
        public static float PlanarSpeed(DodgeballPlayer p)
        {
            if (p == null) return 0f;
            var v = p.Velocity;
            v.y = 0f;
            return v.magnitude;
        }

        /// <summary>Number of targetable (infield, alive, not mid-elimination) players of <paramref name="team"/>.</summary>
        public static int CountTargetable(TeamId team)
        {
            int n = 0;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p != null && p.Team == team && p.IsTargetable) n++;
            }
            return n;
        }

        /// <summary>True if a teammate of <paramref name="self"/> has a pending (Chrono's Delayed Impact) elimination.</summary>
        public static bool AnyTeammatePendingElimination(DodgeballPlayer self)
        {
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (PlayerRegistry.AreTeammates(self, p) && p.Health != null && p.Health.IsEliminationPending) return true;
            }
            return false;
        }

        /// <summary>
        /// Stable rank of <paramref name="self"/> among its teammates in the same zone (ordered by PlayerId) and how many
        /// there are. Used to give every bot its own lane so the team spreads out naturally.
        /// </summary>
        public static int RankInZone(DodgeballPlayer self, out int count)
        {
            int rank = 0;
            count = 0;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Team != self.Team || p.Zone != self.Zone) continue;
                if (p != self && (p.Health == null || !p.Health.IsAlive) && p.Zone == CourtZone.Infield) continue;
                count++;
                if (p != self && p.PlayerId < self.PlayerId) rank++;
            }
            if (count == 0) count = 1;
            return Mathf.Min(rank, count - 1);
        }
    }
}
