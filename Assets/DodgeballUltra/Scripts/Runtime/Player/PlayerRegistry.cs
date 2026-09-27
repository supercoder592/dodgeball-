using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// Global list of spawned players with allocation-free query helpers. Players register themselves in
    /// <see cref="DodgeballPlayer.Initialize"/> and unregister in OnDestroy.
    /// </summary>
    public static class PlayerRegistry
    {
        private static readonly List<DodgeballPlayer> s_players = new List<DodgeballPlayer>(8);

        public static IReadOnlyList<DodgeballPlayer> All => s_players;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_players.Clear();

        public static void Register(DodgeballPlayer player)
        {
            if (player != null && !s_players.Contains(player)) s_players.Add(player);
        }

        public static void Unregister(DodgeballPlayer player) => s_players.Remove(player);

        /// <summary>The player driven by the local human (camera/HUD owner), or null.</summary>
        public static DodgeballPlayer LocalPlayer
        {
            get
            {
                for (int i = 0; i < s_players.Count; i++)
                    if (s_players[i] != null && s_players[i].IsLocalPlayer) return s_players[i];
                return null;
            }
        }

        public static DodgeballPlayer GetById(int playerId)
        {
            for (int i = 0; i < s_players.Count; i++)
                if (s_players[i] != null && s_players[i].PlayerId == playerId) return s_players[i];
            return null;
        }

        /// <summary>Fills <paramref name="results"/> with the players of <paramref name="team"/> (optionally only one zone).</summary>
        public static void GetTeam(TeamId team, List<DodgeballPlayer> results, CourtZone? zone = null)
        {
            results.Clear();
            for (int i = 0; i < s_players.Count; i++)
            {
                var p = s_players[i];
                if (p == null || p.Team != team) continue;
                if (zone.HasValue && p.Zone != zone.Value) continue;
                results.Add(p);
            }
        }

        public static int CountTeam(TeamId team, CourtZone? zone = null)
        {
            int n = 0;
            for (int i = 0; i < s_players.Count; i++)
            {
                var p = s_players[i];
                if (p == null || p.Team != team) continue;
                if (zone.HasValue && p.Zone != zone.Value) continue;
                n++;
            }
            return n;
        }

        public static bool AreEnemies(DodgeballPlayer a, DodgeballPlayer b) =>
            a != null && b != null && a.Team.IsValid() && b.Team.IsValid() && a.Team != b.Team;

        public static bool AreTeammates(DodgeballPlayer a, DodgeballPlayer b) =>
            a != null && b != null && a != b && a.Team == b.Team;

        /// <summary>Nearest infield, targetable enemy of <paramref name="self"/> within <paramref name="maxDistance"/>.</summary>
        public static DodgeballPlayer FindNearestEnemy(DodgeballPlayer self, Vector3 from, float maxDistance = float.PositiveInfinity)
        {
            DodgeballPlayer best = null;
            float bestSqr = maxDistance * maxDistance;
            for (int i = 0; i < s_players.Count; i++)
            {
                var p = s_players[i];
                if (!AreEnemies(self, p) || !p.IsTargetable) continue;
                float d = (p.Position - from).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Nearest teammate of <paramref name="self"/> (optionally restricted to a zone).</summary>
        public static DodgeballPlayer FindNearestTeammate(DodgeballPlayer self, Vector3 from, CourtZone? zone = null)
        {
            DodgeballPlayer best = null;
            float bestSqr = float.PositiveInfinity;
            for (int i = 0; i < s_players.Count; i++)
            {
                var p = s_players[i];
                if (!AreTeammates(self, p)) continue;
                if (zone.HasValue && p.Zone != zone.Value) continue;
                if (p.StateMachine != null && p.StateMachine.IsIn(PlayerStateId.Incapacitated)) continue;
                float d = (p.Position - from).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Fills <paramref name="results"/> with players whose position lies within <paramref name="radius"/> of <paramref name="center"/>.</summary>
        public static void OverlapSphere(Vector3 center, float radius, List<DodgeballPlayer> results, TeamId? onlyTeam = null)
        {
            results.Clear();
            float r2 = radius * radius;
            for (int i = 0; i < s_players.Count; i++)
            {
                var p = s_players[i];
                if (p == null) continue;
                if (onlyTeam.HasValue && p.Team != onlyTeam.Value) continue;
                if ((p.Position - center).sqrMagnitude <= r2) results.Add(p);
            }
        }
    }
}
