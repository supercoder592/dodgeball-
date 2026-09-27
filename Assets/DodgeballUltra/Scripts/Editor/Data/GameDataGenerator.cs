using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DodgeballUltra.Abilities;
using DodgeballUltra.Characters;
using DodgeballUltra.Editor.Setup;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Data
{
    /// <summary>How <see cref="GameDataGenerator.Generate"/> treats assets that already exist.</summary>
    public enum DataUpdateMode
    {
        /// <summary>
        /// Default. Creates whatever is missing and repairs broken links (unassigned ability slots, abilities whose logic
        /// class was lost, heroes missing from the roster). Every existing value - designer tuning included - is kept.
        /// </summary>
        KeepTuning = 0,

        /// <summary>
        /// Overwrites stats, abilities, match rules and juice with the design-spec defaults, but keeps the realistic
        /// model, portrait, animator controller, ability icons and other user-assigned references.
        /// </summary>
        SyncSpecValues = 1,

        /// <summary>Overwrites everything with factory defaults, including model / portrait / controller references.</summary>
        ResetToDefaults = 2,
    }

    /// <summary>Outcome of one <see cref="GameDataGenerator.Generate"/> run.</summary>
    public sealed class DataGenerationResult
    {
        public bool Success;
        public GameConfig Config;
        public int Created;
        public int Updated;
        public int Repaired;
        public readonly List<string> Log = new List<string>();

        /// <summary>One-line summary for the wizard.</summary>
        public string Summary =>
            Success
                ? $"Game data ready: {Created} created, {Updated} updated, {Repaired} repaired."
                : (Log.Count > 0 ? Log[Log.Count - 1] : "Game data generation failed.");

        /// <summary>Full multi-line report.</summary>
        public string Report
        {
            get
            {
                var sb = new StringBuilder(Summary);
                for (int i = 0; i < Log.Count; i++) sb.Append('\n').Append("• ").Append(Log[i]);
                return sb.ToString();
            }
        }
    }

    /// <summary>Status of the generated data, used by the Setup Wizard to show what is missing.</summary>
    public sealed class GameDataStatus
    {
        public GameConfig Config;
        public int HeroCount;
        public readonly List<string> Issues = new List<string>();
        public bool IsComplete => Config != null && Issues.Count == 0;
    }

    /// <summary>
    /// Turns <see cref="HeroRosterFactory.CreateDefaultRoster"/> (the ten launch heroes with design-doc values) into
    /// saved assets:
    /// <code>
    /// Generated/Data/Heroes/Hero_&lt;HeroId&gt;.asset              CharacterData
    /// Generated/Data/Abilities/Ability_&lt;Hero&gt;_&lt;Slot&gt;_&lt;Name&gt;.asset   AbilityData (+ its [SerializeReference] logic)
    /// Generated/Data/MatchRules.asset, JuiceProfile.asset, GameConfig.asset (roster in hero-select order)
    /// </code>
    /// Idempotent: existing assets are found (by path, by hero id / ability id, or through the GameConfig roster) and
    /// updated in place so GUIDs - and every scene / prefab reference to them - survive re-generation.
    /// </summary>
    public static class GameDataGenerator
    {
        // ------------------------------------------------------------------ public API

        /// <summary>Creates / updates all game data assets. Never throws: failures are reported in the result.</summary>
        public static DataGenerationResult Generate(DataUpdateMode mode = DataUpdateMode.KeepTuning)
        {
            var result = new DataGenerationResult();

            List<CharacterData> defaults;
            try
            {
                defaults = HeroRosterFactory.CreateDefaultRoster();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result.Log.Add("HeroRosterFactory.CreateDefaultRoster() failed: " + e.Message);
                return result;
            }

            if (defaults == null || defaults.Count == 0)
            {
                result.Log.Add("HeroRosterFactory returned no heroes.");
                return result;
            }

            // Every in-memory object the factory produced; whatever is not saved as an asset is destroyed at the end.
            var inMemory = new List<Object>();
            foreach (CharacterData hero in defaults)
            {
                if (hero == null) continue;
                inMemory.Add(hero);
                if (hero.passive != null) inMemory.Add(hero.passive);
                if (hero.skill != null) inMemory.Add(hero.skill);
                if (hero.ultimate != null) inMemory.Add(hero.ultimate);
            }

            try
            {
                DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.HeroesFolder);
                DodgeballEditorPaths.EnsureFolder(DodgeballEditorPaths.AbilitiesFolder);

                var config = AssetDatabase.LoadAssetAtPath<GameConfig>(DodgeballEditorPaths.GameConfigPath);
                var abilitiesById = IndexAbilityAssets();

                // Deterministic hero-select order (HeroId order).
                defaults.RemoveAll(h => h == null);
                defaults.Sort((a, b) => ((int)a.heroId).CompareTo((int)b.heroId));

                var heroes = new List<CharacterData>(defaults.Count);
                foreach (CharacterData def in defaults)
                    heroes.Add(UpsertHero(def, config, abilitiesById, mode, result));

                MatchRules rules = UpsertSimple(DodgeballEditorPaths.MatchRulesPath, () => ScriptableObject.CreateInstance<MatchRules>(), mode, result);
                JuiceProfile juice = UpsertSimple(DodgeballEditorPaths.JuiceProfilePath, JuiceProfile.CreateDefault, mode, result);
                result.Config = UpsertConfig(config, heroes, rules, juice, mode, result);

                AssetDatabase.SaveAssets();
                result.Success = true;
                Debug.Log("[Dodgeball Ultra] " + result.Report);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result.Success = false;
                result.Log.Add("Game data generation failed: " + e.Message);
            }
            finally
            {
                foreach (Object o in inMemory)
                    if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
            }

            return result;
        }

        /// <summary>The generated GameConfig asset (null if the data has not been generated yet).</summary>
        public static GameConfig LoadGameConfig() => AssetDatabase.LoadAssetAtPath<GameConfig>(DodgeballEditorPaths.GameConfigPath);

        /// <summary>
        /// Heroes to build: the GameConfig roster (non-null entries, roster order) or, without a config, every CharacterData
        /// in the generated Heroes folder sorted by hero id. Empty if nothing was generated.
        /// </summary>
        public static List<CharacterData> LoadRoster()
        {
            var roster = new List<CharacterData>();
            GameConfig config = LoadGameConfig();
            if (config != null && config.roster != null)
            {
                foreach (CharacterData hero in config.roster)
                    if (hero != null && !roster.Contains(hero)) roster.Add(hero);
                if (roster.Count > 0) return roster;
            }

            if (!AssetDatabase.IsValidFolder(DodgeballEditorPaths.HeroesFolder)) return roster;
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterData", new[] { DodgeballEditorPaths.HeroesFolder }))
            {
                var hero = AssetDatabase.LoadAssetAtPath<CharacterData>(AssetDatabase.GUIDToAssetPath(guid));
                if (hero != null) roster.Add(hero);
            }
            roster.Sort((a, b) => ((int)a.heroId).CompareTo((int)b.heroId));
            return roster;
        }

        /// <summary>What is missing from the generated data (for the wizard's status list).</summary>
        public static GameDataStatus GetStatus()
        {
            var status = new GameDataStatus { Config = LoadGameConfig() };
            if (status.Config == null)
            {
                status.Issues.Add("GameConfig.asset has not been generated yet.");
                return status;
            }

            GameConfig config = status.Config;
            if (config.rules == null) status.Issues.Add("GameConfig has no MatchRules.");
            if (config.juice == null) status.Issues.Add("GameConfig has no JuiceProfile.");

            foreach (HeroId id in (HeroId[])Enum.GetValues(typeof(HeroId)))
            {
                CharacterData hero = config.GetHero(id);
                if (hero == null)
                {
                    status.Issues.Add($"{id}: missing from the GameConfig roster.");
                    continue;
                }
                status.HeroCount++;
                CheckAbility(status, hero, hero.passive, AbilitySlot.Passive);
                CheckAbility(status, hero, hero.skill, AbilitySlot.Skill);
                CheckAbility(status, hero, hero.ultimate, AbilitySlot.Ultimate);
            }
            return status;
        }

        /// <summary>Canonical asset path of a hero.</summary>
        public static string GetHeroAssetPath(HeroId hero) => $"{DodgeballEditorPaths.HeroesFolder}/Hero_{hero}.asset";

        /// <summary>Canonical asset path of an ability, e.g. "Ability_Rayne_Skill_SupersonicMeteor.asset".</summary>
        public static string GetAbilityAssetPath(HeroId hero, AbilitySlot slot, AbilityData ability)
        {
            string label = ToPascalIdentifier(!string.IsNullOrEmpty(ability.displayName) ? ability.displayName : ability.abilityId);
            if (string.IsNullOrEmpty(label)) label = "Ability";
            return $"{DodgeballEditorPaths.AbilitiesFolder}/Ability_{hero}_{slot}_{label}.asset";
        }

        // ------------------------------------------------------------------ heroes

        private static CharacterData UpsertHero(CharacterData def, GameConfig config, Dictionary<string, AbilityData> abilitiesById,
            DataUpdateMode mode, DataGenerationResult result)
        {
            HeroId id = def.heroId;
            string path = GetHeroAssetPath(id);
            CharacterData existing = FindExistingHero(id, path, config);

            AbilityData passive = UpsertAbility(id, AbilitySlot.Passive, def.passive, existing != null ? existing.passive : null, abilitiesById, mode, result);
            AbilityData skill = UpsertAbility(id, AbilitySlot.Skill, def.skill, existing != null ? existing.skill : null, abilitiesById, mode, result);
            AbilityData ultimate = UpsertAbility(id, AbilitySlot.Ultimate, def.ultimate, existing != null ? existing.ultimate : null, abilitiesById, mode, result);

            if (existing == null)
            {
                // Abilities must be persistent before the hero asset references them.
                def.passive = passive;
                def.skill = skill;
                def.ultimate = ultimate;
                path = UniquePath(path);
                def.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(def, path);
                result.Created++;
                result.Log.Add($"{id}: created {path}");
                return def;
            }

            if (mode == DataUpdateMode.KeepTuning)
            {
                bool relinked = false;
                if (existing.passive != passive) { existing.passive = passive; relinked = true; }
                if (existing.skill != skill) { existing.skill = skill; relinked = true; }
                if (existing.ultimate != ultimate) { existing.ultimate = ultimate; relinked = true; }
                if (relinked)
                {
                    EditorUtility.SetDirty(existing);
                    result.Repaired++;
                    result.Log.Add($"{id}: re-linked missing ability slots.");
                }
                return existing;
            }

            // Sync / Reset: overwrite in place (GUID kept), then restore what must survive.
            GameObject model = existing.modelPrefab;
            Sprite portrait = existing.portrait;
            RuntimeAnimatorController controller = existing.animatorController;
            string avatar = existing.rocketboxAvatar;
            BodyType bodyType = existing.bodyType;
            float modelScale = existing.modelScale;
            string assetName = existing.name;

            EditorUtility.CopySerialized(def, existing);
            existing.name = assetName;
            existing.passive = passive;
            existing.skill = skill;
            existing.ultimate = ultimate;

            if (mode == DataUpdateMode.SyncSpecValues)
            {
                existing.modelPrefab = model;
                existing.portrait = portrait;
                existing.animatorController = controller;
                if (model != null)
                {
                    // The body-related casting only follows the spec when no model has been built for it yet.
                    existing.rocketboxAvatar = avatar;
                    existing.bodyType = bodyType;
                    existing.modelScale = modelScale;
                }
                result.Log.Add($"{id}: updated to spec values (model, portrait and animator kept).");
            }
            else
            {
                result.Log.Add($"{id}: reset to factory defaults (re-run 'Build Characters' to re-link the realistic model).");
            }

            EditorUtility.SetDirty(existing);
            result.Updated++;
            return existing;
        }

        private static CharacterData FindExistingHero(HeroId id, string canonicalPath, GameConfig config)
        {
            // 1) The hero the GameConfig already uses (may live anywhere in the project).
            if (config != null && config.roster != null)
            {
                foreach (CharacterData hero in config.roster)
                    if (hero != null && hero.heroId == id && EditorUtility.IsPersistent(hero)) return hero;
            }

            // 2) The canonical file.
            var atPath = AssetDatabase.LoadAssetAtPath<CharacterData>(canonicalPath);
            if (atPath != null) return atPath;

            // 3) A renamed file in the generated Heroes folder.
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterData", new[] { DodgeballEditorPaths.HeroesFolder }))
            {
                var hero = AssetDatabase.LoadAssetAtPath<CharacterData>(AssetDatabase.GUIDToAssetPath(guid));
                if (hero != null && hero.heroId == id) return hero;
            }
            return null;
        }

        // ------------------------------------------------------------------ abilities

        private static AbilityData UpsertAbility(HeroId hero, AbilitySlot slot, AbilityData def, AbilityData existingRef,
            Dictionary<string, AbilityData> abilitiesById, DataUpdateMode mode, DataGenerationResult result)
        {
            bool existingPersistent = existingRef != null && EditorUtility.IsPersistent(existingRef);

            if (def == null)
            {
                // The factory has nothing for this slot: keep whatever the designer assigned.
                if (existingRef == null) result.Log.Add($"{hero}: the roster factory provides no {slot} ability.");
                return existingPersistent ? existingRef : null;
            }

            bool existingIsCanonical = existingPersistent && existingRef.abilityId == def.abilityId;

            if (mode == DataUpdateMode.KeepTuning && existingPersistent)
            {
                // Designer's choice (possibly a custom ability): keep it, only restore a lost logic object on the canonical one.
                if (existingIsCanonical) RepairLogic(existingRef, def, hero, slot, result);
                return existingRef;
            }

            AbilityData target = existingIsCanonical ? existingRef : null;
            if (target == null && !string.IsNullOrEmpty(def.abilityId)) abilitiesById.TryGetValue(def.abilityId, out target);
            string canonicalPath = GetAbilityAssetPath(hero, slot, def);
            if (target == null) target = AssetDatabase.LoadAssetAtPath<AbilityData>(canonicalPath);

            if (target == null)
            {
                string path = UniquePath(canonicalPath);
                def.name = Path.GetFileNameWithoutExtension(path);
                if (def.slot != slot) def.slot = slot;
                AssetDatabase.CreateAsset(def, path);
                if (!string.IsNullOrEmpty(def.abilityId)) abilitiesById[def.abilityId] = def;
                result.Created++;
                return def;
            }

            if (mode == DataUpdateMode.KeepTuning)
            {
                // An unlinked asset for this slot already exists: re-link it instead of duplicating it.
                RepairLogic(target, def, hero, slot, result);
                return target;
            }

            Sprite icon = target.icon;
            string assetName = target.name;
            EditorUtility.CopySerialized(def, target); // deep-copies the [SerializeReference] logic object as well
            target.name = assetName;
            if (mode == DataUpdateMode.SyncSpecValues && icon != null) target.icon = icon;
            EditorUtility.SetDirty(target);
            result.Updated++;
            return target;
        }

        private static void RepairLogic(AbilityData target, AbilityData def, HeroId hero, AbilitySlot slot, DataGenerationResult result)
        {
            if (target.logic != null || def.logic == null) return;
            target.logic = def.logic; // plain C# object: serialized into the target asset
            EditorUtility.SetDirty(target);
            result.Repaired++;
            result.Log.Add($"{hero}: restored the missing logic ({def.logic.GetType().Name}) of {slot} '{target.displayName}'.");
        }

        private static Dictionary<string, AbilityData> IndexAbilityAssets()
        {
            var map = new Dictionary<string, AbilityData>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(DodgeballEditorPaths.AbilitiesFolder)) return map;
            foreach (string guid in AssetDatabase.FindAssets("t:AbilityData", new[] { DodgeballEditorPaths.AbilitiesFolder }))
            {
                var ability = AssetDatabase.LoadAssetAtPath<AbilityData>(AssetDatabase.GUIDToAssetPath(guid));
                if (ability != null && !string.IsNullOrEmpty(ability.abilityId) && !map.ContainsKey(ability.abilityId))
                    map.Add(ability.abilityId, ability);
            }
            return map;
        }

        private static void CheckAbility(GameDataStatus status, CharacterData hero, AbilityData ability, AbilitySlot slot)
        {
            if (ability == null)
            {
                status.Issues.Add($"{hero.heroId}: no {slot} ability assigned.");
                return;
            }
            if (ability.logic == null) status.Issues.Add($"{hero.heroId}: {slot} '{ability.displayName}' has no logic class.");
        }

        // ------------------------------------------------------------------ rules / juice / config

        private static T UpsertSimple<T>(string path, Func<T> factory, DataUpdateMode mode, DataGenerationResult result) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                T created = factory();
                created.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(created, path);
                result.Created++;
                result.Log.Add("Created " + path);
                return created;
            }

            if (mode == DataUpdateMode.KeepTuning) return existing;

            T fresh = factory();
            try
            {
                string assetName = existing.name;
                EditorUtility.CopySerialized(fresh, existing);
                existing.name = assetName;
                EditorUtility.SetDirty(existing);
                result.Updated++;
                result.Log.Add("Reset " + path + " to spec values.");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
            return existing;
        }

        private static GameConfig UpsertConfig(GameConfig config, List<CharacterData> heroes, MatchRules rules, JuiceProfile juice,
            DataUpdateMode mode, DataGenerationResult result)
        {
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<GameConfig>();
                config.name = Path.GetFileNameWithoutExtension(DodgeballEditorPaths.GameConfigPath);
                config.roster = new List<CharacterData>(heroes);
                config.rules = rules;
                config.juice = juice;
                AssetDatabase.CreateAsset(config, DodgeballEditorPaths.GameConfigPath);
                result.Created++;
                result.Log.Add("Created " + DodgeballEditorPaths.GameConfigPath);
                return config;
            }

            if (config.roster == null) config.roster = new List<CharacterData>();

            switch (mode)
            {
                case DataUpdateMode.KeepTuning:
                {
                    // Keep the designer's order and custom heroes; drop nulls/duplicates; append missing launch heroes.
                    int before = config.roster.Count;
                    var cleaned = new List<CharacterData>(config.roster.Count + heroes.Count);
                    foreach (CharacterData hero in config.roster)
                        if (hero != null && !cleaned.Contains(hero)) cleaned.Add(hero);
                    int added = 0;
                    foreach (CharacterData hero in heroes)
                    {
                        if (hero == null || ContainsHeroId(cleaned, hero.heroId)) continue;
                        cleaned.Add(hero);
                        added++;
                    }
                    bool changed = added > 0 || cleaned.Count != before;
                    config.roster = cleaned;
                    if (config.rules == null) { config.rules = rules; changed = true; }
                    if (config.juice == null) { config.juice = juice; changed = true; }
                    if (changed)
                    {
                        result.Repaired++;
                        result.Log.Add($"GameConfig: roster repaired ({added} hero(es) added).");
                    }
                    break;
                }

                case DataUpdateMode.SyncSpecValues:
                {
                    // Launch heroes in hero-select order, followed by any custom heroes the designer added.
                    var roster = new List<CharacterData>(heroes);
                    foreach (CharacterData hero in config.roster)
                        if (hero != null && !roster.Contains(hero) && !ContainsHeroId(heroes, hero.heroId)) roster.Add(hero);
                    config.roster = roster;
                    if (config.rules == null) config.rules = rules;
                    if (config.juice == null) config.juice = juice;
                    result.Updated++;
                    break;
                }

                default: // ResetToDefaults
                {
                    GameConfig fresh = ScriptableObject.CreateInstance<GameConfig>();
                    try
                    {
                        string assetName = config.name;
                        EditorUtility.CopySerialized(fresh, config);
                        config.name = assetName;
                    }
                    finally
                    {
                        Object.DestroyImmediate(fresh);
                    }
                    config.roster = new List<CharacterData>(heroes);
                    config.rules = rules;
                    config.juice = juice;
                    result.Updated++;
                    result.Log.Add("GameConfig reset to defaults.");
                    break;
                }
            }

            EditorUtility.SetDirty(config);
            return config;
        }

        // ------------------------------------------------------------------ helpers

        private static bool ContainsHeroId(List<CharacterData> list, HeroId id)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].heroId == id) return true;
            return false;
        }

        /// <summary><paramref name="path"/> if no asset occupies it, else a unique variant ("... 1.asset").</summary>
        private static string UniquePath(string path)
        {
            Object atPath = AssetDatabase.LoadMainAssetAtPath(path);
            if (atPath == null) return path;
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        /// <summary>"Supersonic Meteor" / "rayne.supersonic_meteor" -> "SupersonicMeteor" / "RayneSupersonicMeteor".</summary>
        private static string ToPascalIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            bool upperNext = true;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) && c < 128)
                {
                    sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                    upperNext = false;
                }
                else
                {
                    upperNext = true;
                }
            }
            return sb.ToString();
        }
    }
}
