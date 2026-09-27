namespace DodgeballUltra
{
    /// <summary>The two sides of a 3v3 match. Home defends the -Z half of the court, Away the +Z half.</summary>
    public enum TeamId
    {
        None = -1,
        Home = 0,
        Away = 1,
    }

    /// <summary>Where a player currently plays from (Taiwanese dodgeball: 內場 infield / 外場 outfield).</summary>
    public enum CourtZone
    {
        /// <summary>Inside the team's half. Can be hit.</summary>
        Infield = 0,
        /// <summary>Eliminated players stand behind the opponent's baseline; they can still throw but cannot be hit.</summary>
        Outfield = 1,
    }

    public static class TeamIdExtensions
    {
        public static TeamId Opponent(this TeamId team) =>
            team == TeamId.Home ? TeamId.Away : team == TeamId.Away ? TeamId.Home : TeamId.None;

        public static bool IsValid(this TeamId team) => team == TeamId.Home || team == TeamId.Away;

        /// <summary>0 for Home, 1 for Away. Throws for None.</summary>
        public static int Index(this TeamId team)
        {
            if (!team.IsValid()) throw new System.ArgumentOutOfRangeException(nameof(team), team, "TeamId.None has no index.");
            return (int)team;
        }
    }
}
