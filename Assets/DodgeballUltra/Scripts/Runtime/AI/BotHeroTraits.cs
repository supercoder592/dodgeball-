using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Hero-specific play-style biases layered on top of the difficulty profile, so each bot plays its hero's kit:
    /// Rayne holds Overcharge throws longer, Bear (Iron Mitts) and Gouki (Thick Hide) stand in for catches, Houdini
    /// (Hat Trick) loves to pass, Specter reacts faster thanks to Danger Sense, Gale plays like an assassin, etc.
    /// </summary>
    public struct BotHeroTraits
    {
        /// <summary>Charge-time range (s) that replaces the profile's range when non-zero (Rayne's Overcharge: up to 2 s).</summary>
        public Vector2 ChargeTimeOverride;
        /// <summary>Added to the catch attempt probability.</summary>
        public float CatchBias;
        /// <summary>Added to the dodge skill.</summary>
        public float DodgeBias;
        /// <summary>Added to the aggression (how close to the centre line the bot attacks from).</summary>
        public float AggressionBias;
        /// <summary>Added to the pass chance.</summary>
        public float PassBias;
        /// <summary>Hero reacts to <c>DangerSenseEvent</c> (Specter's passive).</summary>
        public bool HasDangerSense;

        /// <summary>Traits for <paramref name="hero"/>.</summary>
        public static BotHeroTraits For(HeroId hero)
        {
            var t = new BotHeroTraits();
            switch (hero)
            {
                case HeroId.Rayne:
                    // Overcharge: velocity +50% / radius +20% over 2 s of charging -> hold longer, especially at range.
                    t.ChargeTimeOverride = new Vector2(0.6f, 2.0f);
                    t.AggressionBias = 0.1f;
                    break;
                case HeroId.Shadow:
                    t.AggressionBias = 0.1f;
                    break;
                case HeroId.Gale:
                    // Assassin: pushes forward (stealth throws gain +30% speed).
                    t.AggressionBias = 0.15f;
                    t.DodgeBias = 0.05f;
                    break;
                case HeroId.Bear:
                    // Iron Mitts: perfect window 0.225 s -> much more willing to catch.
                    t.CatchBias = 0.25f;
                    t.AggressionBias = -0.05f;
                    break;
                case HeroId.Gouki:
                    // Thick Hide: 200 HP, survives a hit -> brawls near the line and risks catches.
                    t.CatchBias = 0.1f;
                    t.AggressionBias = 0.2f;
                    t.DodgeBias = -0.1f;
                    break;
                case HeroId.Houdini:
                    // Hat Trick: passes teleport straight into the teammate's hands.
                    t.PassBias = 0.35f;
                    break;
                case HeroId.Elsa:
                    t.PassBias = 0.05f;
                    break;
                case HeroId.Specter:
                    t.HasDangerSense = true;
                    t.DodgeBias = 0.1f;
                    break;
                case HeroId.Chrono:
                    t.CatchBias = 0.05f;
                    break;
            }
            return t;
        }
    }
}
