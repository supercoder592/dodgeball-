using System;
using System.Collections.Generic;
using System.Text;
using DodgeballUltra.Characters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - entry points of the realistic-human character pipeline (Microsoft Rocketbox, MIT):
    /// download -> Humanoid import (Biped map + enforced T-pose) -> PBR materials (skin SSS, hair alpha, smoothness from
    /// specular) -> LODGroup prefab -> generated motion-capture AnimatorControllers -> links into CharacterData.
    /// Downloads go to Assets/ThirdParty/Rocketbox/ (git-ignored); generated assets to Assets/DodgeballUltra/Generated/.
    /// <para>Owner module: Editor/Characters. Called by the Setup Wizard.</para>
    /// </summary>
    public static class CharacterPipeline
    {
        public const string DownloadRoot = "Assets/ThirdParty/Rocketbox";
        public const string GeneratedRoot = "Assets/DodgeballUltra/Generated/Characters";

        private const string ProgressTitle = "Dodgeball Ultra · Build Characters";

        /// <summary>Avatar folder names needed by <paramref name="roster"/> (CharacterData.rocketboxAvatar).</summary>
        public static List<string> GetRequiredAvatars(IList<CharacterData> roster)
        {
            var result = new List<string>();
            if (roster == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CharacterData hero in roster)
            {
                string avatar = AvatarOf(hero);
                if (avatar != null && seen.Add(avatar)) result.Add(avatar);
            }
            return result;
        }

        /// <summary>True when every avatar (FBX + textures) and the animation set are present on disk.</summary>
        public static bool AreAssetsDownloaded(IList<string> avatarNames)
        {
            RocketboxManifest manifest = RocketboxAssetSet.LoadManifest();
            if (avatarNames != null)
            {
                foreach (string raw in avatarNames)
                {
                    string avatar = raw?.Trim();
                    if (string.IsNullOrEmpty(avatar)) continue;
                    if (!IsAvatarDownloaded(avatar, manifest)) return false;
                }
            }
            return AreAnimationsDownloaded(manifest);
        }

        /// <summary>
        /// Downloads avatars (FBX + textures + portrait) and the curated animation set, pinned to the manifest commit.
        /// Non-blocking (EditorApplication.update driven). <paramref name="onProgress"/> (0..1, message);
        /// <paramref name="onComplete"/> (success, message). Already-present files are skipped.
        /// </summary>
        public static void DownloadAsync(IList<string> avatarNames, bool includeAnimations, Action<float, string> onProgress,
            Action<bool, string> onComplete)
        {
            if (RocketboxDownloader.IsRunning)
            {
                onComplete?.Invoke(false, "A Rocketbox download is already running.");
                return;
            }

            RocketboxManifest manifest = RocketboxAssetSet.LoadManifest();
            if (manifest == null)
            {
                onComplete?.Invoke(false, RocketboxAssetSet.ManifestError ?? "The Rocketbox manifest could not be loaded.");
                return;
            }

            bool portraits = CharacterPipelineSettings.Instance.downloadPortraits;
            var items = new List<RocketboxDownloader.Item>();
            var problems = new List<string>();
            if (avatarNames != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in avatarNames)
                {
                    string avatar = raw?.Trim();
                    if (string.IsNullOrEmpty(avatar) || !seen.Add(avatar)) continue;
                    RocketboxAvatarEntry entry = manifest.FindAvatar(avatar);
                    if (entry == null)
                    {
                        problems.Add($"'{avatar}' is not a Rocketbox avatar of the pinned commit (check CharacterData.rocketboxAvatar).");
                        continue;
                    }
                    foreach (KeyValuePair<string, string> file in RocketboxAssetSet.GetAvatarFiles(entry, portraits))
                        items.Add(Item(manifest, file.Key, file.Value));
                }
            }

            if (includeAnimations)
            {
                foreach (KeyValuePair<string, string> file in RocketboxAssetSet.GetAnimationFiles(manifest))
                    items.Add(Item(manifest, file.Key, file.Value));
            }

            if (items.Count == 0 && problems.Count == 0)
            {
                try
                {
                    RocketboxDownloader.WriteLicense(manifest.commit);
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning("[Dodgeball Ultra] " + e.Message);
                }
                onProgress?.Invoke(1f, "Nothing to download.");
                onComplete?.Invoke(true, "Nothing to download.");
                return;
            }

            RocketboxDownloader.Start(manifest.commit, items, problems, onProgress, onComplete);
        }

        /// <summary>Cancels a running download.</summary>
        public static void CancelDownload() => RocketboxDownloader.Cancel();

        public static bool IsDownloading => RocketboxDownloader.IsRunning;

        /// <summary>
        /// Imports/configures everything for <paramref name="roster"/> and assigns modelPrefab, animatorController and
        /// portrait on each CharacterData (and marks them dirty). Returns a human-readable report.
        /// </summary>
        public static string BuildCharacters(IList<CharacterData> roster)
        {
            var heroes = new List<CharacterData>();
            if (roster != null)
                foreach (CharacterData hero in roster)
                    if (hero != null && !heroes.Contains(hero)) heroes.Add(hero);
            if (heroes.Count == 0) return "No heroes to build (generate the game data first).";
            if (IsDownloading) return "The Rocketbox download is still running: build the characters once it has finished.";

            var log = new List<string>();
            int built = 0;
            try
            {
                built = BuildInternal(heroes, log);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                log.Add("Build Characters stopped with an error: " + e.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var report = new StringBuilder();
            report.Append($"{built}/{heroes.Count} heroes have a realistic Rocketbox model and motion-capture animator.");
            foreach (string line in log) report.Append("\n• ").Append(line);
            string text = report.ToString();
            if (built == heroes.Count) Debug.Log("[Dodgeball Ultra] " + text);
            else Debug.LogWarning("[Dodgeball Ultra] " + text);
            return text;
        }

        // ============================================================================================ build pipeline

        private static int BuildInternal(List<CharacterData> heroes, List<string> log)
        {
            RocketboxManifest manifest = RocketboxAssetSet.LoadManifest();
            if (manifest == null && RocketboxAssetSet.ManifestError != null) log.Add(RocketboxAssetSet.ManifestError);

            // ---- 1. Humanoid setup of the avatar models and clips ----------------------------------------------------
            var modelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (HumanoidAvatarBuilder.SuspendAutoConfigure())
            {
                // Files fetched outside Unity (Tools/fetch_rocketbox.py) must be imported before anything is configured;
                // the automatic Humanoid setup is suspended because everything is configured explicitly below.
                Progress("Importing downloaded files…", 0.02f);
                AssetDatabase.Refresh();

                foreach (string avatar in GetRequiredAvatars(heroes))
                {
                    RocketboxAvatarEntry entry = manifest?.FindAvatar(avatar);
                    string modelPath = RocketboxAssetSet.ModelAssetPath(entry != null ? entry.name : avatar, entry);
                    if (RocketboxAssetSet.FileExistsNonEmpty(modelPath)) modelPaths[avatar] = modelPath;
                }

                Progress("Configuring Humanoid avatars (Biped map, T-pose)…", 0.08f);
                ConfigureModels(modelPaths, log);
                Progress("Configuring motion-capture clips…", 0.25f);
                ConfigureClips(modelPaths, manifest, log);
            }

            // ---- 2. animator controllers ----------------------------------------------------------------------------
            Progress("Generating animator controllers…", 0.4f);
            Dictionary<BodyType, AnimatorController> controllers = AnimatorControllerBuilder.BuildAll(log);

            // ---- 3. materials, portraits and prefabs ----------------------------------------------------------------
            var materialsByAvatar = new Dictionary<string, Dictionary<string, Material>>(StringComparer.OrdinalIgnoreCase);
            var portraits = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            var prefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int built = 0;
            for (int i = 0; i < heroes.Count; i++)
            {
                CharacterData hero = heroes[i];
                string heroName = HeroLabel(hero);
                string avatar = AvatarOf(hero);
                Progress($"Building {heroName}…", 0.45f + 0.5f * i / heroes.Count);

                if (avatar == null)
                {
                    if (hero.modelPrefab != null && hero.animatorController == null &&
                        controllers.TryGetValue(hero.bodyType, out AnimatorController fallback))
                    {
                        hero.animatorController = fallback;
                        EditorUtility.SetDirty(hero);
                    }
                    if (hero.modelPrefab != null && hero.animatorController != null) built++;
                    log.Add($"{heroName}: no Rocketbox avatar set - custom model kept.");
                    continue;
                }

                if (!modelPaths.TryGetValue(avatar, out string modelPath))
                {
                    log.Add($"{heroName}: avatar '{avatar}' is not downloaded (Setup Wizard step 3).");
                    continue;
                }

                RocketboxAvatarEntry entry = manifest?.FindAvatar(avatar);
                if (entry != null) avatar = entry.name; // folder names are case-sensitive on disk
                BodyType body = RocketboxAssetSet.GetAvatarBodyType(avatar, manifest);
                if (hero.bodyType != body)
                {
                    log.Add($"{heroName}: body type set to {body} to match '{avatar}'.");
                    hero.bodyType = body;
                }
                controllers.TryGetValue(body, out AnimatorController controller);

                if (!materialsByAvatar.TryGetValue(avatar, out Dictionary<string, Material> materials))
                {
                    materials = CharacterMaterialBuilder.BuildMaterials(avatar, log);
                    materialsByAvatar[avatar] = materials;
                    portraits[avatar] = CharacterMaterialBuilder.BuildPortrait(avatar, entry, log);
                }

                string prefabName = UniquePrefabName(hero, prefabNames);
                GameObject prefab = CharacterPrefabBuilder.Build(prefabName, avatar, modelPath, materials, controller, log);
                if (prefab == null) continue;

                hero.modelPrefab = prefab;
                if (controller != null) hero.animatorController = controller;
                if (portraits.TryGetValue(avatar, out Sprite portrait) && portrait != null) hero.portrait = portrait;
                EditorUtility.SetDirty(hero);

                if (controller != null)
                {
                    built++;
                    log.Add($"{heroName}: {avatar} -> {AssetDatabase.GetAssetPath(prefab)} ({controller.name}, {materials.Count} material(s)).");
                }
                else
                {
                    log.Add($"{heroName}: no animator controller for {body}.");
                }
            }

            Progress("Saving assets…", 0.98f);
            AssetDatabase.SaveAssets();
            return built;
        }

        private static void ConfigureModels(Dictionary<string, string> modelPaths, List<string> log)
        {
            var pending = new List<string>();
            foreach (string path in modelPaths.Values)
                if (!HumanoidAvatarBuilder.IsModelConfigured(path)) pending.Add(path);
            if (pending.Count == 0) return;

            var messages = new List<string>();
            using (new AssetEditingScope())
            {
                foreach (string path in pending)
                {
                    HumanoidAvatarBuilder.ConfigureModel(path, true, out string message);
                    messages.Add(message);
                }
            }

            // The editing scope has ended: the models are re-imported with their Humanoid avatars now.
            for (int i = 0; i < pending.Count; i++)
            {
                if (HumanoidAvatarBuilder.IsModelConfigured(pending[i])) log.Add(messages[i]);
                else log.Add(HumanoidAvatarBuilder.GetFailure(pending[i]) is string failure
                    ? $"{System.IO.Path.GetFileName(pending[i])}: Humanoid setup failed - {failure}"
                    : messages[i]);
            }
        }

        private static void ConfigureClips(Dictionary<string, string> modelPaths, RocketboxManifest manifest, List<string> log)
        {
            var preferred = new Dictionary<BodyType, List<string>>
            {
                { BodyType.Male, new List<string>() },
                { BodyType.Female, new List<string>() },
            };
            foreach (KeyValuePair<string, string> kv in modelPaths)
                preferred[RocketboxAssetSet.GetAvatarBodyType(kv.Key, manifest)].Add(kv.Value);

            var jobs = new List<KeyValuePair<string, string>>(); // clip path -> reference model
            int missing = 0;
            foreach (BodyType body in new[] { BodyType.Male, BodyType.Female })
            {
                string reference = HumanoidAvatarBuilder.FindReferenceModel(body, preferred[body], true);
                foreach (RocketboxClip clip in RocketboxAssetSet.CuratedClips)
                {
                    string clipPath = RocketboxAssetSet.ClipAssetPath(body, clip.BaseName);
                    if (!RocketboxAssetSet.FileExistsNonEmpty(clipPath))
                    {
                        missing++;
                        continue;
                    }
                    if (IsClipUpToDate(clipPath, body, reference, manifest)) continue;
                    if (reference == null)
                    {
                        log.Add($"{System.IO.Path.GetFileName(clipPath)}: no configured Humanoid avatar to copy the skeleton from.");
                        continue;
                    }
                    jobs.Add(new KeyValuePair<string, string>(clipPath, reference));
                }
            }
            if (missing > 0) log.Add($"{missing} curated motion-capture clip file(s) are not downloaded.");
            if (jobs.Count == 0) return;

            var messages = new List<string>();
            using (new AssetEditingScope())
            {
                foreach (KeyValuePair<string, string> job in jobs)
                {
                    HumanoidAvatarBuilder.ConfigureClip(job.Key, job.Value, true, out string message);
                    messages.Add(message);
                }
            }

            int configured = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                if (HumanoidAvatarBuilder.IsClipConfigured(jobs[i].Key, jobs[i].Value)) configured++;
                else log.Add(messages[i]);
            }
            if (configured > 0) log.Add($"{configured} motion-capture clip(s) configured as Humanoid.");
        }

        /// <summary>
        /// True when a clip already copies a valid Humanoid avatar of the right body type (or of any body type when no
        /// avatar of its own body is available) - avoids needless re-imports on every build.
        /// </summary>
        private static bool IsClipUpToDate(string clipPath, BodyType body, string reference, RocketboxManifest manifest)
        {
            string current = HumanoidAvatarBuilder.GetClipReferenceModel(clipPath);
            if (current == null || !HumanoidAvatarBuilder.IsClipConfigured(clipPath, current) || !HumanoidAvatarBuilder.IsModelConfigured(current))
                return false;
            if (current == reference) return true;
            string currentAvatar = RocketboxAssetSet.AvatarNameFromPath(current);
            bool currentMatches = currentAvatar != null && RocketboxAssetSet.GetAvatarBodyType(currentAvatar, manifest) == body;
            string referenceAvatar = RocketboxAssetSet.AvatarNameFromPath(reference);
            bool referenceMatches = referenceAvatar != null && RocketboxAssetSet.GetAvatarBodyType(referenceAvatar, manifest) == body;
            return currentMatches || !referenceMatches;
        }

        // ================================================================================================ helpers

        private static bool IsAvatarDownloaded(string avatar, RocketboxManifest manifest)
        {
            RocketboxAvatarEntry entry = manifest?.FindAvatar(avatar);
            if (entry != null) avatar = entry.name;
            if (!RocketboxAssetSet.FileExistsNonEmpty(RocketboxAssetSet.ModelAssetPath(avatar, entry))) return false;
            if (entry != null)
            {
                string folder = RocketboxAssetSet.AvatarFolder(entry.name);
                foreach (string texture in entry.textures)
                    if (RocketboxAssetSet.IsWantedTexture(texture) && !RocketboxAssetSet.FileExistsNonEmpty(folder + "/" + texture)) return false;
                return true;
            }

            // Unknown to the manifest (e.g. copied in by hand): accept any texture next to the model.
            string textures = RocketboxAssetSet.ToAbsolutePath(RocketboxAssetSet.AvatarTexturesFolder(avatar));
            try
            {
                return System.IO.Directory.Exists(textures) && System.IO.Directory.GetFiles(textures).Length > 0;
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool AreAnimationsDownloaded(RocketboxManifest manifest)
        {
            foreach (KeyValuePair<string, string> file in RocketboxAssetSet.GetAnimationFiles(manifest))
                if (!RocketboxAssetSet.FileExistsNonEmpty(file.Value)) return false;
            return true;
        }

        private static RocketboxDownloader.Item Item(RocketboxManifest manifest, string repositoryPath, string assetPath)
            => new RocketboxDownloader.Item { Source = repositoryPath, Url = manifest.GetUrl(repositoryPath), AssetPath = assetPath };

        private static string AvatarOf(CharacterData hero)
        {
            if (hero == null) return null;
            string avatar = hero.rocketboxAvatar?.Trim();
            return string.IsNullOrEmpty(avatar) ? null : avatar;
        }

        private static string HeroLabel(CharacterData hero)
            => !string.IsNullOrEmpty(hero.displayName) && hero.displayName != "Hero" ? hero.displayName : hero.name;

        private static string UniquePrefabName(CharacterData hero, HashSet<string> used)
        {
            string name = hero.heroId.ToString();
            if (!used.Add(name))
            {
                name = RocketboxAssetSet.SanitizeFileName(string.IsNullOrEmpty(hero.name) ? name + "_" + used.Count : hero.name);
                while (!used.Add(name)) name += "_" + used.Count;
            }
            return name;
        }

        private static void Progress(string message, float progress)
        {
            if (Application.isBatchMode) return;
            EditorUtility.DisplayProgressBar(ProgressTitle, message, Mathf.Clamp01(progress));
        }
    }
}
