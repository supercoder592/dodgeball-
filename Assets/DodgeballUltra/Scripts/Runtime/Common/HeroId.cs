namespace DodgeballUltra
{
    /// <summary>The ten launch heroes of Dodgeball Ultra.</summary>
    public enum HeroId
    {
        Rayne = 0,   // DPS - Speedball
        Shadow = 1,  // DPS - Clones
        Gale = 2,    // DPS - Stealth / Assassin
        Bear = 3,    // Tank - Guardian
        Gouki = 4,   // Tank - Brawler
        Screws = 5,  // Support - Engineer
        Houdini = 6, // Support - Trickster
        Elsa = 7,    // Support - Ice Control
        Specter = 8, // Agility - Evasion
        Chrono = 9,  // Agility - Rewind
    }

    public enum HeroRole
    {
        Attacker,
        Defender,
        Support,
        Agility,
    }

    /// <summary>Selects the male or female motion-capture animation set for a realistic humanoid model.</summary>
    public enum BodyType
    {
        Male,
        Female,
    }
}
