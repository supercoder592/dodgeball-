using System;
using System.Collections.Generic;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - builds the ten launch heroes (CharacterData + their three AbilityData each) with the design-doc
    /// values, fully in memory. Used by:
    /// <list type="bullet">
    /// <item>the editor's GameDataGenerator (which saves them as assets and links the realistic model prefabs), and</item>
    /// <item>GameBootstrap as a fallback when no GameConfig asset is assigned (the game still runs).</item>
    /// </list>
    /// Hero -> Rocketbox avatar casting: Rayne=Sports_Male_02, Shadow=Security_Male_01, Gale=Sports_Female_02,
    /// Bear=Fire_Male_02, Gouki=Military_Male_01, Screws=Construction_Male_01, Houdini=Business_Male_01,
    /// Elsa=Pilot_Female_01, Specter=Sports_Male_04, Chrono=Military_Female_01.
    /// <para>Owner module: Abilities (because it instantiates every ability class).</para>
    /// </summary>
    public static class HeroRosterFactory
    {
        /// <summary>Creates all ten heroes (new ScriptableObject instances, not saved).</summary>
        public static List<CharacterData> CreateDefaultRoster() => throw new NotImplementedException();

        /// <summary>Creates one hero.</summary>
        public static CharacterData CreateHero(HeroId hero) => throw new NotImplementedException();

        /// <summary>Rocketbox avatar folder name for a hero (see class docs).</summary>
        public static string GetDefaultAvatar(HeroId hero) => throw new NotImplementedException();

        public static BodyType GetBodyType(HeroId hero) => throw new NotImplementedException();
    }
}
