using System;
using DodgeballUltra.Abilities;

namespace DodgeballUltra.Audio
{
    /// <summary>Mixing bus of a sound (each has its own volume setting and optional AudioMixerGroup).</summary>
    public enum SfxCategory
    {
        /// <summary>Gameplay sound effects (balls, bodies, abilities).</summary>
        Sfx = 0,
        /// <summary>Crowd reactions and ambience.</summary>
        Crowd,
        /// <summary>Referee / scoreboard (whistle, countdown).</summary>
        Announcer,
        /// <summary>Menus and HUD.</summary>
        Ui,
    }

    /// <summary>Handle to a sound started with <see cref="AudioManager.PlayAttached"/> (loops, followed sources).</summary>
    public struct AudioHandle
    {
        public int Id;
        public int Voice;
        public bool IsValid => Id != 0;
    }

    /// <summary>Default mixing/playback parameters of one <see cref="SfxId"/> (an AudioLibrary entry can override volume/pitch).</summary>
    public struct SfxProfile
    {
        /// <summary>Base gain 0..1 (relative loudness between ids).</summary>
        public float Volume;
        /// <summary>Random +/- fraction applied to the volume of each play.</summary>
        public float VolumeJitter;
        /// <summary>Random +/- fraction applied to the pitch of each play.</summary>
        public float PitchJitter;
        /// <summary>Minimum unscaled seconds between two plays of this id (prevents stacking/phasing).</summary>
        public float MinInterval;
        /// <summary>Maximum simultaneous voices of this id (the oldest is replaced beyond this).</summary>
        public int MaxVoices;
        /// <summary>AudioSource priority (0 = most important, 256 = least) and voice-stealing rank.</summary>
        public int Priority;
        /// <summary>Not ducked during hitstop (the impact itself must cut through).</summary>
        public bool DuckExempt;
        public SfxCategory Category;

        public SfxProfile(float volume, float volumeJitter, float pitchJitter, float minInterval, int maxVoices, int priority,
            SfxCategory category = SfxCategory.Sfx, bool duckExempt = false)
        {
            Volume = volume;
            VolumeJitter = volumeJitter;
            PitchJitter = pitchJitter;
            MinInterval = minInterval;
            MaxVoices = maxVoices;
            Priority = priority;
            Category = category;
            DuckExempt = duckExempt;
        }
    }

    /// <summary>Built-in defaults for every <see cref="SfxId"/>.</summary>
    public static class SfxProfiles
    {
        private static readonly SfxProfile[] s_profiles = Build();

        public static int Count => s_profiles.Length;

        public static SfxProfile Get(SfxId id)
        {
            int i = (int)id;
            return i >= 0 && i < s_profiles.Length ? s_profiles[i] : new SfxProfile(1f, 0.05f, 0.05f, 0f, 4, 128);
        }

        private static SfxProfile[] Build()
        {
            var p = new SfxProfile[Enum.GetValues(typeof(SfxId)).Length];
            for (int i = 0; i < p.Length; i++) p[i] = new SfxProfile(0.8f, 0.08f, 0.05f, 0.02f, 4, 128);

            // Ball & combat --------------------------------------------------------------------------------
            p[(int)SfxId.Throw] = new SfxProfile(0.7f, 0.1f, 0.06f, 0.02f, 6, 96);
            p[(int)SfxId.ThrowHeavy] = new SfxProfile(0.85f, 0.08f, 0.05f, 0.02f, 4, 80);
            p[(int)SfxId.BallHitPlayer] = new SfxProfile(1f, 0.06f, 0.05f, 0.015f, 6, 32, SfxCategory.Sfx, true);
            p[(int)SfxId.BallHitHeavy] = new SfxProfile(1f, 0.05f, 0.04f, 0.015f, 4, 24, SfxCategory.Sfx, true);
            p[(int)SfxId.BallBounceFloor] = new SfxProfile(0.75f, 0.12f, 0.06f, 0.01f, 8, 110);
            p[(int)SfxId.BallBounceWall] = new SfxProfile(0.7f, 0.12f, 0.06f, 0.01f, 6, 115);
            p[(int)SfxId.Catch] = new SfxProfile(0.9f, 0.08f, 0.05f, 0.02f, 4, 48, SfxCategory.Sfx, true);
            p[(int)SfxId.PerfectCatch] = new SfxProfile(1f, 0.03f, 0.02f, 0.03f, 3, 16, SfxCategory.Sfx, true);
            p[(int)SfxId.CatchWhiff] = new SfxProfile(0.5f, 0.1f, 0.08f, 0.05f, 3, 150);
            p[(int)SfxId.Pickup] = new SfxProfile(0.55f, 0.1f, 0.06f, 0.03f, 4, 140);
            p[(int)SfxId.Pass] = new SfxProfile(0.55f, 0.1f, 0.06f, 0.03f, 4, 120);

            // Locomotion --------------------------------------------------------------------------------------
            p[(int)SfxId.Footstep] = new SfxProfile(0.45f, 0.15f, 0.08f, 0f, 10, 200);
            p[(int)SfxId.Jump] = new SfxProfile(0.5f, 0.1f, 0.06f, 0.05f, 4, 170);
            p[(int)SfxId.Land] = new SfxProfile(0.6f, 0.1f, 0.06f, 0.05f, 4, 160);
            p[(int)SfxId.Slide] = new SfxProfile(0.6f, 0.08f, 0.06f, 0.1f, 4, 150);

            // Match flow (2D announcer / crowd) ---------------------------------------------------------------
            p[(int)SfxId.Whistle] = new SfxProfile(0.8f, 0.02f, 0.01f, 0.25f, 2, 8, SfxCategory.Announcer, true);
            p[(int)SfxId.Countdown] = new SfxProfile(0.7f, 0f, 0f, 0.2f, 2, 8, SfxCategory.Announcer, true);
            p[(int)SfxId.RoundStart] = new SfxProfile(0.8f, 0f, 0f, 0.5f, 1, 8, SfxCategory.Announcer, true);
            p[(int)SfxId.Elimination] = new SfxProfile(0.95f, 0.04f, 0.03f, 0.05f, 3, 20, SfxCategory.Sfx, true);
            p[(int)SfxId.Revive] = new SfxProfile(0.7f, 0.03f, 0.02f, 0.1f, 3, 40);
            p[(int)SfxId.CrowdCheer] = new SfxProfile(0.65f, 0.1f, 0.04f, 0.6f, 2, 64, SfxCategory.Crowd);
            p[(int)SfxId.CrowdGasp] = new SfxProfile(0.5f, 0.1f, 0.05f, 0.8f, 2, 72, SfxCategory.Crowd);
            p[(int)SfxId.CrowdAmbience] = new SfxProfile(0.5f, 0f, 0f, 0f, 1, 200, SfxCategory.Crowd);

            // Abilities ---------------------------------------------------------------------------------------
            p[(int)SfxId.AbilityCast] = new SfxProfile(0.75f, 0.06f, 0.05f, 0.05f, 4, 60);
            p[(int)SfxId.UltimateCast] = new SfxProfile(0.95f, 0.03f, 0.02f, 0.2f, 2, 20);
            p[(int)SfxId.UltimateReady] = new SfxProfile(0.6f, 0f, 0f, 0.5f, 2, 40);
            p[(int)SfxId.Shockwave] = new SfxProfile(1f, 0.05f, 0.04f, 0.05f, 3, 20, SfxCategory.Sfx, true);
            p[(int)SfxId.Beam] = new SfxProfile(0.9f, 0.04f, 0.03f, 0.1f, 2, 30);
            p[(int)SfxId.Freeze] = new SfxProfile(0.8f, 0.06f, 0.05f, 0.06f, 4, 50);
            p[(int)SfxId.Teleport] = new SfxProfile(0.75f, 0.06f, 0.05f, 0.05f, 4, 60);
            p[(int)SfxId.Glue] = new SfxProfile(0.8f, 0.08f, 0.06f, 0.05f, 4, 60);
            p[(int)SfxId.Turret] = new SfxProfile(0.7f, 0.06f, 0.04f, 0.1f, 3, 90);
            p[(int)SfxId.Shield] = new SfxProfile(0.6f, 0.04f, 0.02f, 0.1f, 3, 70);
            p[(int)SfxId.Magnet] = new SfxProfile(0.6f, 0.04f, 0.02f, 0.1f, 3, 70);
            p[(int)SfxId.Stasis] = new SfxProfile(0.55f, 0.04f, 0.02f, 0.1f, 3, 70);
            p[(int)SfxId.Rewind] = new SfxProfile(0.8f, 0.04f, 0.02f, 0.1f, 3, 50);
            p[(int)SfxId.Cloak] = new SfxProfile(0.6f, 0.05f, 0.04f, 0.1f, 3, 80);
            p[(int)SfxId.Clone] = new SfxProfile(0.7f, 0.06f, 0.05f, 0.05f, 4, 70);
            p[(int)SfxId.Earthquake] = new SfxProfile(1f, 0.03f, 0.03f, 0.2f, 2, 20, SfxCategory.Sfx, true);

            // UI ---------------------------------------------------------------------------------------------
            p[(int)SfxId.UiClick] = new SfxProfile(0.6f, 0.05f, 0.03f, 0.03f, 3, 0, SfxCategory.Ui, true);
            p[(int)SfxId.UiConfirm] = new SfxProfile(0.7f, 0f, 0f, 0.05f, 2, 0, SfxCategory.Ui, true);
            return p;
        }
    }

    /// <summary>
    /// Maps each hero's skill/ultimate to the sound played when <c>AbilityCastEvent</c> fires, so ability code only has
    /// to play effect sounds (impacts, puddles, freezes) and not its cast cue.
    /// </summary>
    public static class AbilitySfxTable
    {
        /// <summary>Cast cue for <paramref name="hero"/>'s <paramref name="slot"/>. False for passives.</summary>
        public static bool TryGetCastSound(HeroId hero, AbilitySlot slot, out SfxId sfx, out float volume)
        {
            volume = 1f;
            sfx = SfxId.AbilityCast;
            if (slot == AbilitySlot.Passive) return false;
            bool ultimate = slot == AbilitySlot.Ultimate;
            switch (hero)
            {
                case HeroId.Rayne: sfx = ultimate ? SfxId.Beam : SfxId.AbilityCast; break;       // Meteor ignition / Hyperbeam charge
                case HeroId.Shadow: sfx = SfxId.Clone; break;                                     // Night Parade / Mirage Formation
                case HeroId.Gale: sfx = ultimate ? SfxId.Teleport : SfxId.Cloak; break;           // Optical Camouflage / Shadow Strike
                case HeroId.Bear: sfx = ultimate ? SfxId.Shield : SfxId.Magnet; break;            // Magnetic Pull / Aegis Barrier
                case HeroId.Gouki: sfx = ultimate ? SfxId.Earthquake : SfxId.AbilityCast; break;  // Tackle Intercept / Earthquake Slam
                case HeroId.Screws: sfx = ultimate ? SfxId.Turret : SfxId.AbilityCast; break;     // Glue Trap Ball / Auto-Turret
                case HeroId.Houdini: sfx = SfxId.Teleport; volume = ultimate ? 1f : 0.7f; break;  // Swap Places / Grand Vanish
                case HeroId.Elsa: sfx = ultimate ? SfxId.Freeze : SfxId.AbilityCast; break;       // Glacier Freeze / Absolute Zero
                case HeroId.Specter: sfx = ultimate ? SfxId.Rewind : SfxId.AbilityCast; volume = ultimate ? 0.8f : 0.7f; break; // Precognition / Time Reversal
                case HeroId.Chrono: sfx = ultimate ? SfxId.Rewind : SfxId.Stasis; break;          // Stasis Field / Temporal Reset
            }
            return true;
        }
    }
}
