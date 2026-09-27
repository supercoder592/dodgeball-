using System;
using System.Collections.Generic;
using DodgeballUltra.Characters;
using DodgeballUltra.Editor.ArenaScene;
using DodgeballUltra.Editor.Characters;
using DodgeballUltra.Editor.Data;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// Evaluates what each Setup Wizard step still needs (read-only; safe to call on focus / project change).
    /// Calls into modules that may not be ready (e.g. the character pipeline) are guarded so the wizard always renders.
    /// </summary>
    public static class SetupChecks
    {
        /// <summary>Evaluates one step.</summary>
        public static SetupStepStatus Evaluate(SetupStepId step)
        {
            try
            {
                switch (step)
                {
                    case SetupStepId.ConfigureProject: return EvaluateProject();
                    case SetupStepId.GenerateData: return EvaluateData();
                    case SetupStepId.DownloadAvatars: return EvaluateDownload();
                    case SetupStepId.BuildCharacters: return EvaluateCharacters();
                    case SetupStepId.BuildArena: return EvaluateArena();
                    case SetupStepId.Play: return EvaluatePlay();
                }
            }
            catch (Exception e)
            {
                var failed = new SetupStepStatus { State = SetupStepState.Failed, Summary = "Status check failed: " + e.Message };
                failed.Missing.Add(e.GetType().Name + ": " + e.Message);
                return failed;
            }
            return new SetupStepStatus();
        }

        // ------------------------------------------------------------------ 1

        private static SetupStepStatus EvaluateProject()
        {
            var s = new SetupStepStatus();
            IEditorRenderingHooks hooks = EditorRenderingHooks.Active;

            if (EditorRenderingHooks.HasPipelineHooks)
            {
                if (!hooks.IsActive) s.Missing.Add($"{hooks.PipelineName} is not the active render pipeline yet.");
            }
            else if (ProjectConfigurator.IsHdrpPackageInstalled)
            {
                s.Warnings.Add("The HDRP package is installed but the HDRP editor hooks are not loaded " +
                               "(check the Console for compile errors in DodgeballUltra.Editor.HDRP). Using the Built-in fallback.");
            }
            else
            {
                s.Warnings.Add("HDRP is not installed: using the Built-in fallback (Standard shader). Install " +
                               "com.unity.render-pipelines.high-definition for the realistic look (skin SSS, SSR, volumetrics).");
            }

            if (PlayerSettings.colorSpace != ColorSpace.Linear) s.Missing.Add("Colour space is Gamma (physically based lighting needs Linear).");

            List<string> layerIssues = ProjectConfigurator.CheckLayers(out bool conflicts);
            if (conflicts) s.Warnings.AddRange(layerIssues);
            else s.Missing.AddRange(layerIssues);

            s.Warnings.AddRange(ProjectConfigurator.CheckInputHandling());

            return s.Resolve($"{hooks.PipelineName}, Linear colour space, layers OK.", "Project settings need to be configured.");
        }

        // ------------------------------------------------------------------ 2

        private static SetupStepStatus EvaluateData()
        {
            var s = new SetupStepStatus();
            GameDataStatus data = GameDataGenerator.GetStatus();
            s.Missing.AddRange(data.Issues);
            return s.Resolve($"{data.HeroCount} heroes with their abilities, rules and juice profile.", "Game data is missing or incomplete.");
        }

        // ------------------------------------------------------------------ 3

        private static SetupStepStatus EvaluateDownload()
        {
            var s = new SetupStepStatus();
            if (SetupRunner.IsBusy && SetupRunner.ActiveStep == SetupStepId.DownloadAvatars)
            {
                s.State = SetupStepState.Running;
                s.Summary = SetupRunner.DownloadMessage;
                return s;
            }

            List<CharacterData> roster = GameDataGenerator.LoadRoster();
            if (roster.Count == 0)
            {
                s.State = SetupStepState.Blocked;
                s.Summary = "Generate the game data first (step 2): it lists which avatars the heroes use.";
                return s;
            }

            List<string> avatars;
            try
            {
                avatars = CharacterPipeline.GetRequiredAvatars(roster);
            }
            catch (Exception e)
            {
                s.State = SetupStepState.Failed;
                s.Summary = "The character pipeline is unavailable: " + e.Message;
                return s;
            }

            if (avatars == null || avatars.Count == 0)
            {
                s.Warnings.Add("No hero references a Rocketbox avatar (custom models only).");
                return s.Resolve("Nothing to download.", "Nothing to download.");
            }

            bool all = SafeAreDownloaded(avatars);
            if (!all)
            {
                foreach (CharacterData hero in roster)
                {
                    if (hero == null || string.IsNullOrEmpty(hero.rocketboxAvatar)) continue;
                    if (!SafeAreDownloaded(new List<string> { hero.rocketboxAvatar }))
                        s.Missing.Add($"{hero.displayName}: {hero.rocketboxAvatar}");
                }
                if (s.Missing.Count == 0) s.Missing.Add("Motion-capture animation set.");
            }

            return s.Resolve($"{avatars.Count} realistic avatars + motion-capture clips on disk.",
                $"{Math.Max(1, s.Missing.Count)} download(s) missing (≈90 MB per avatar).");
        }

        // ------------------------------------------------------------------ 4

        private static SetupStepStatus EvaluateCharacters()
        {
            var s = new SetupStepStatus();
            List<CharacterData> roster = GameDataGenerator.LoadRoster();
            if (roster.Count == 0)
            {
                s.State = SetupStepState.Blocked;
                s.Summary = "Generate the game data first (step 2).";
                return s;
            }

            int ready = 0;
            foreach (CharacterData hero in roster)
            {
                if (hero == null) continue;
                bool ok = true;
                if (hero.modelPrefab == null)
                {
                    s.Missing.Add($"{hero.displayName}: no realistic model prefab.");
                    ok = false;
                }
                else
                {
                    var animator = hero.modelPrefab.GetComponentInChildren<Animator>(true);
                    if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                        s.Warnings.Add($"{hero.displayName}: model is not a Humanoid (IK, ragdoll and mocap retargeting disabled).");
                }
                if (hero.animatorController == null)
                {
                    s.Missing.Add($"{hero.displayName}: no animator controller.");
                    ok = false;
                }
                if (ok) ready++;
            }

            if (s.Missing.Count > 0 && SetupRunner.LastStatusOf(SetupStepId.DownloadAvatars) != SetupStepState.Done)
            {
                // Tell the user why: most of the time the avatars are simply not downloaded yet.
                SetupStepStatus download = EvaluateDownload();
                if (download.State == SetupStepState.Todo || download.State == SetupStepState.Blocked)
                    s.Warnings.Add("Some avatars are not downloaded yet (step 3); those heroes would use a placeholder body.");
            }

            return s.Resolve($"{ready}/{roster.Count} heroes have a realistic Humanoid model and animator.",
                $"{roster.Count - ready} hero(es) still need their realistic model.");
        }

        // ------------------------------------------------------------------ 5

        private static SetupStepStatus EvaluateArena()
        {
            var s = new SetupStepStatus();
            if (!ArenaSceneBuilder.SceneExists)
            {
                s.Missing.Add(DodgeballEditorPaths.ArenaScenePath + " has not been built yet.");
                return s.Resolve(string.Empty, "Arena scene not built yet.");
            }
            if (!ArenaSceneBuilder.IsInBuildSettings()) s.Warnings.Add("Arena.unity is not in Build Settings (re-run to add it).");
            if (GameDataGenerator.LoadGameConfig() == null) s.Warnings.Add("GameConfig is missing: the bootstrap will use the in-memory roster.");
            return s.Resolve("Arena.unity saved (floodlit indoor hall, PBR materials, probes).", string.Empty);
        }

        // ------------------------------------------------------------------ 6

        private static SetupStepStatus EvaluatePlay()
        {
            var s = new SetupStepStatus();
            if (EditorApplication.isPlaying)
            {
                s.State = SetupStepState.Running;
                s.Summary = "Playing.";
                return s;
            }
            if (!ArenaSceneBuilder.SceneExists)
                s.Warnings.Add("Arena.unity is not built yet: Play will offer QuickPlay.unity (everything is built at runtime).");
            s.State = s.Warnings.Count > 0 ? SetupStepState.Warning : SetupStepState.Todo;
            s.Summary = "Ready: pick a hero and play 3v3 against AI.";
            return s;
        }

        // ------------------------------------------------------------------ helpers

        private static bool SafeAreDownloaded(List<string> avatars)
        {
            try
            {
                return CharacterPipeline.AreAssetsDownloaded(avatars);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
