using System;
using System.Collections.Generic;
using DodgeballUltra.AI;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// Builds and validates <see cref="MatchSetup"/>s. The hero picker is <b>deterministic</b>: the same seed, pool and
    /// already-taken heroes always yield the same line-up (reproducible bug reports, CI smoke tests and attract mode),
    /// and it prefers heroes nobody else uses yet so a 3v3 shows six different characters whenever the roster allows.
    /// Used by <see cref="GameBootstrap"/> (auto-start) and <see cref="MatchManager"/> (filling incomplete setups).
    /// </summary>
    public static class MatchSetupUtil
    {
        /// <summary>Seed value that means "pick a fresh random seed" in <see cref="ResolveSeed"/>.</summary>
        public const int RandomSeed = -1;

        /// <summary>Returns <paramref name="seed"/>, or a time-based seed when it is <see cref="RandomSeed"/>.</summary>
        public static int ResolveSeed(int seed) => seed == RandomSeed ? Environment.TickCount & 0x7fffffff : seed;

        /// <summary>
        /// Picks <paramref name="count"/> heroes from <paramref name="pool"/>, deterministic for <paramref name="seed"/>.
        /// Heroes in <paramref name="taken"/> are avoided; once every pool hero is used, repeats are allowed (still in a
        /// seeded order) so any team size works. Picked heroes are appended to <paramref name="taken"/>.
        /// </summary>
        /// <param name="pool">Candidate heroes (typically the roster order). Empty/null = every <see cref="HeroId"/>.</param>
        /// <param name="taken">Heroes already in the match (input and output).</param>
        public static HeroId[] PickDistinctHeroes(int count, int seed, IReadOnlyList<HeroId> pool, List<HeroId> taken)
        {
            count = Math.Max(0, count);
            var result = new HeroId[count];
            if (count == 0) return result;

            var candidates = BuildPool(pool);
            var rng = new Random(seed);
            Shuffle(candidates, rng);

            int filled = 0;
            // Pass 1: heroes nobody has yet, in seeded order.
            for (int i = 0; i < candidates.Count && filled < count; i++)
            {
                if (taken != null && taken.Contains(candidates[i])) continue;
                result[filled++] = candidates[i];
                taken?.Add(candidates[i]);
            }

            // Pass 2 (only for more players than heroes): cycle through fresh shuffles.
            while (filled < count)
            {
                Shuffle(candidates, rng);
                for (int i = 0; i < candidates.Count && filled < count; i++)
                {
                    result[filled++] = candidates[i];
                    taken?.Add(candidates[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// A complete setup for an automatic start: the local human plays <paramref name="localHero"/> in slot 0 of
        /// <paramref name="localTeam"/> (unless <paramref name="spectate"/>), every other slot gets a distinct bot hero.
        /// </summary>
        public static MatchSetup CreateAutoSetup(HeroId localHero, TeamId localTeam, BotDifficulty difficulty,
            int playersPerTeam, int seed, bool spectate, IReadOnlyList<HeroId> pool = null)
        {
            playersPerTeam = Math.Max(1, playersPerTeam);
            if (!localTeam.IsValid()) localTeam = TeamId.Home;

            var setup = new MatchSetup
            {
                LocalHero = localHero,
                LocalTeam = localTeam,
                Difficulty = difficulty,
                Spectate = spectate,
                HomeHeroes = new HeroId[playersPerTeam],
                AwayHeroes = new HeroId[playersPerTeam],
            };

            var taken = new List<HeroId>(playersPerTeam * 2);
            if (!spectate) taken.Add(localHero);

            // Allies first (so the human's team gets first pick of the seeded order), then the opponents.
            int allySlots = spectate ? playersPerTeam : playersPerTeam - 1;
            var allies = PickDistinctHeroes(allySlots, seed, pool, taken);
            var enemies = PickDistinctHeroes(playersPerTeam, unchecked(seed * 31 + 17), pool, taken);

            var own = localTeam == TeamId.Home ? setup.HomeHeroes : setup.AwayHeroes;
            var other = localTeam == TeamId.Home ? setup.AwayHeroes : setup.HomeHeroes;
            int a = 0;
            if (!spectate) own[0] = localHero;
            for (int i = spectate ? 0 : 1; i < playersPerTeam; i++) own[i] = allies[a++];
            for (int i = 0; i < playersPerTeam; i++) other[i] = enemies[i];
            return setup;
        }

        /// <summary>
        /// Returns a copy of <paramref name="setup"/> that is safe to spawn: both hero arrays have exactly
        /// <paramref name="playersPerTeam"/> valid entries (missing slots get distinct heroes deterministically), a
        /// playing human's team is valid, and slot 0 of the human's team is the human's hero.
        /// </summary>
        public static MatchSetup Sanitize(MatchSetup setup, int playersPerTeam, IReadOnlyList<HeroId> pool = null)
        {
            playersPerTeam = Math.Max(1, playersPerTeam);
            if (!setup.Spectate && !setup.LocalTeam.IsValid()) setup.LocalTeam = TeamId.Home;

            var home = new HeroId[playersPerTeam];
            var away = new HeroId[playersPerTeam];
            var homeSet = new bool[playersPerTeam];
            var awaySet = new bool[playersPerTeam];
            var taken = new List<HeroId>(playersPerTeam * 2);

            CopyValid(setup.HomeHeroes, home, homeSet, taken);
            CopyValid(setup.AwayHeroes, away, awaySet, taken);

            if (!setup.Spectate)
            {
                var own = setup.LocalTeam == TeamId.Home ? home : away;
                var ownSet = setup.LocalTeam == TeamId.Home ? homeSet : awaySet;
                if (ownSet[0]) taken.Remove(own[0]); // the human's pick always wins slot 0
                own[0] = setup.LocalHero;
                ownSet[0] = true;
                taken.Add(setup.LocalHero);
            }

            // Seed from the provided heroes so the same partial setup always completes the same way.
            int seed = 7919;
            for (int i = 0; i < taken.Count; i++) seed = unchecked(seed * 31 + (int)taken[i] + 1);

            FillMissing(home, homeSet, seed, pool, taken);
            FillMissing(away, awaySet, unchecked(seed * 31 + 17), pool, taken);

            setup.HomeHeroes = home;
            setup.AwayHeroes = away;
            return setup;
        }

        // ------------------------------------------------------------------ internals

        private static List<HeroId> BuildPool(IReadOnlyList<HeroId> pool)
        {
            var list = new List<HeroId>(10);
            if (pool != null)
            {
                for (int i = 0; i < pool.Count; i++)
                    if (IsDefined(pool[i]) && !list.Contains(pool[i])) list.Add(pool[i]);
            }
            if (list.Count == 0)
            {
                var all = (HeroId[])Enum.GetValues(typeof(HeroId));
                list.AddRange(all);
            }
            return list;
        }

        private static void Shuffle(List<HeroId> list, Random rng)
        {
            // Fisher-Yates.
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static bool IsDefined(HeroId hero) => Enum.IsDefined(typeof(HeroId), hero);

        private static void CopyValid(HeroId[] source, HeroId[] target, bool[] set, List<HeroId> taken)
        {
            if (source == null) return;
            int n = Math.Min(source.Length, target.Length);
            for (int i = 0; i < n; i++)
            {
                if (!IsDefined(source[i])) continue;
                target[i] = source[i];
                set[i] = true;
                taken.Add(source[i]);
            }
        }

        private static void FillMissing(HeroId[] heroes, bool[] set, int seed, IReadOnlyList<HeroId> pool, List<HeroId> taken)
        {
            int missing = 0;
            for (int i = 0; i < set.Length; i++) if (!set[i]) missing++;
            if (missing == 0) return;

            var picks = PickDistinctHeroes(missing, seed, pool, taken);
            int p = 0;
            for (int i = 0; i < heroes.Length; i++)
            {
                if (set[i]) continue;
                heroes[i] = picks[p++];
                set[i] = true;
            }
        }
    }
}
