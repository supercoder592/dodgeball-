using System;
using System.Collections.Generic;
using DodgeballUltra.Characters;
using DodgeballUltra.Editor.ArenaScene;
using DodgeballUltra.Editor.Characters;
using DodgeballUltra.Editor.Data;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// Executes Setup Wizard steps - one at a time or "Run All" - outside of OnGUI (dialogs and scene changes are safe),
    /// independent of the wizard window (closing it does not stop a run).
    /// <para>
    /// Run All is a small state machine driven by <see cref="EditorApplication.update"/>. Its position is kept in
    /// <see cref="SessionState"/> so it resumes after a domain reload (e.g. a script recompilation triggered by a package or
    /// pipeline change). A step that keeps triggering reloads is attempted at most <see cref="MaxAttemptsPerStep"/> times.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class SetupRunner
    {
        /// <summary>Raised whenever progress, a result or the running state changes (the window repaints).</summary>
        public static event Action Changed;

        private const int MaxAttemptsPerStep = 3;
        private const string KeyRunAll = "DodgeballUltra.Setup.RunAll";
        private const string KeyRunAllStep = "DodgeballUltra.Setup.RunAllStep";
        private const string KeyRunAllAttempts = "DodgeballUltra.Setup.RunAllAttempts";
        private const string KeyMessage = "DodgeballUltra.Setup.Message.";
        private const string KeyOutcome = "DodgeballUltra.Setup.Outcome.";

        private static bool s_busy;
        private static SetupStepId s_activeStep;
        private static bool s_waitingForDownload;
        private static bool s_cancelRequested;
        private static Action<SetupStepResult> s_downloadCallback;
        private static ArenaSceneSettings s_arenaSettings;

        static SetupRunner()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        // ------------------------------------------------------------------ state

        /// <summary>A step is executing (including a background download).</summary>
        public static bool IsBusy => s_busy;

        /// <summary>The executing step (valid while <see cref="IsBusy"/>).</summary>
        public static SetupStepId ActiveStep => s_activeStep;

        /// <summary>Run All is in progress.</summary>
        public static bool IsRunningAll => SessionState.GetBool(KeyRunAll, false);

        /// <summary>The step Run All is on.</summary>
        public static SetupStepId RunAllStep => (SetupStepId)Mathf.Clamp(SessionState.GetInt(KeyRunAllStep, 0), 0, (int)SetupStepId.Play);

        /// <summary>Latest download progress (0..1) and message.</summary>
        public static float DownloadProgress { get; private set; }
        public static string DownloadMessage { get; private set; } = string.Empty;

        /// <summary>How "Generate Game Data" treats existing assets (remembered per project).</summary>
        public static DataUpdateMode DataMode
        {
            get => (DataUpdateMode)Mathf.Clamp(SetupPrefs.GetInt("DataMode", 0), 0, 1); // Reset is never remembered
            set => SetupPrefs.SetInt("DataMode", Mathf.Clamp((int)value, 0, 1));
        }

        /// <summary>Enter Play mode at the end of Run All.</summary>
        public static bool PlayWhenDone
        {
            get => SetupPrefs.GetBool("PlayWhenDone", true);
            set => SetupPrefs.SetBool("PlayWhenDone", value);
        }

        /// <summary>Arena build options (persisted per project, except the optional HDRI).</summary>
        public static ArenaSceneSettings ArenaSettings
        {
            get
            {
                if (s_arenaSettings != null) return s_arenaSettings;
                s_arenaSettings = new ArenaSceneSettings();
                string json = SetupPrefs.GetString("ArenaSettings", string.Empty);
                if (!string.IsNullOrEmpty(json))
                {
                    try { JsonUtility.FromJsonOverwrite(json, s_arenaSettings); }
                    catch (Exception) { s_arenaSettings = new ArenaSceneSettings(); }
                }
                s_arenaSettings.hdri = null;
                string hdriPath = SetupPrefs.GetString("ArenaHdri", string.Empty);
                if (!string.IsNullOrEmpty(hdriPath)) s_arenaSettings.hdri = AssetDatabase.LoadAssetAtPath<Cubemap>(hdriPath);
                return s_arenaSettings;
            }
        }

        /// <summary>Saves <see cref="ArenaSettings"/> to EditorPrefs.</summary>
        public static void SaveArenaSettings()
        {
            ArenaSceneSettings settings = ArenaSettings;
            Cubemap hdri = settings.hdri;
            settings.hdri = null; // object references are not stable in JSON prefs: store the asset path instead
            SetupPrefs.SetString("ArenaSettings", JsonUtility.ToJson(settings));
            settings.hdri = hdri;
            SetupPrefs.SetString("ArenaHdri", hdri != null ? AssetDatabase.GetAssetPath(hdri) : string.Empty);
        }

        /// <summary>Resets the arena options to their defaults.</summary>
        public static void ResetArenaSettings()
        {
            s_arenaSettings = new ArenaSceneSettings();
            SaveArenaSettings();
        }

        /// <summary>Message of the last run of <paramref name="step"/> in this editor session.</summary>
        public static string GetLastMessage(SetupStepId step) => SessionState.GetString(KeyMessage + (int)step, string.Empty);

        /// <summary>Outcome of the last run: Done, Failed or (never run / cancelled) Todo.</summary>
        public static SetupStepState LastStatusOf(SetupStepId step) => (SetupStepState)SessionState.GetInt(KeyOutcome + (int)step, (int)SetupStepState.Todo);

        // ------------------------------------------------------------------ commands

        /// <summary>Runs one step (deferred to the next editor tick so it never executes inside OnGUI).</summary>
        public static void RunStep(SetupStepId step)
        {
            if (s_busy || IsRunningAll) return;
            EditorApplication.delayCall += () =>
            {
                if (s_busy || IsRunningAll) return;
                Execute(step, result => Record(step, result));
            };
        }

        /// <summary>Runs every step in order (stops at the first failure or cancellation).</summary>
        public static void RunAll()
        {
            if (s_busy || IsRunningAll) return;
            SessionState.SetBool(KeyRunAll, true);
            SessionState.SetInt(KeyRunAllStep, 0);
            SessionState.SetInt(KeyRunAllAttempts, 0);
            Notify();
        }

        /// <summary>Stops Run All after the current step (a running download is cancelled).</summary>
        public static void StopRunAll()
        {
            if (!IsRunningAll) return;
            SessionState.SetBool(KeyRunAll, false);
            if (s_waitingForDownload) CancelDownload();
            Notify();
        }

        /// <summary>Cancels the avatar download in progress.</summary>
        public static void CancelDownload()
        {
            if (!s_waitingForDownload) return;
            s_cancelRequested = true;
            try
            {
                CharacterPipeline.CancelDownload();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            Notify();
        }

        // ------------------------------------------------------------------ run-all state machine

        private static void Update()
        {
            // A cancelled download whose pipeline did not report completion: close it ourselves.
            if (s_waitingForDownload && s_cancelRequested && !SafeIsDownloading())
                CompleteDownload(false, "Download cancelled.");

            if (!IsRunningAll || s_busy) return;

            SetupStepId last = PlayWhenDone ? SetupStepId.Play : SetupStepId.BuildArena;
            int index = SessionState.GetInt(KeyRunAllStep, 0);
            if (index > (int)last)
            {
                FinishRunAll();
                return;
            }

            var step = (SetupStepId)index;
            int attempts = SessionState.GetInt(KeyRunAllAttempts, 0) + 1;
            SessionState.SetInt(KeyRunAllAttempts, attempts);
            if (attempts > MaxAttemptsPerStep)
            {
                Record(step, SetupStepResult.Fail("Stopped: this step was interrupted repeatedly (domain reloads). Run it on its own."));
                FinishRunAll();
                return;
            }

            if (step == SetupStepId.Play)
            {
                // Leave the run-all state before entering Play mode (which reloads the domain).
                FinishRunAll();
                Execute(step, r => Record(step, r));
                return;
            }

            Execute(step, result =>
            {
                Record(step, result);
                if (!IsRunningAll) return; // stopped meanwhile
                if (result.Success)
                {
                    SessionState.SetInt(KeyRunAllStep, index + 1);
                    SessionState.SetInt(KeyRunAllAttempts, 0);
                }
                else
                {
                    FinishRunAll();
                }
                Notify();
            });
        }

        private static void FinishRunAll()
        {
            SessionState.SetBool(KeyRunAll, false);
            SessionState.SetInt(KeyRunAllAttempts, 0);
            Notify();
        }

        // ------------------------------------------------------------------ step execution

        private static void Execute(SetupStepId step, Action<SetupStepResult> done)
        {
            s_busy = true;
            s_activeStep = step;
            Notify();

            // Async steps complete through their own callback; synchronous ones complete right here.
            bool completesLater = false;
            SetupStepResult result;
            try
            {
                switch (step)
                {
                    case SetupStepId.ConfigureProject:
                        result = ProjectConfigurator.Configure();
                        break;

                    case SetupStepId.GenerateData:
                    {
                        DataGenerationResult data = GameDataGenerator.Generate(DataMode);
                        result = data.Success ? SetupStepResult.Ok(data.Summary) : SetupStepResult.Fail(data.Summary);
                        break;
                    }

                    case SetupStepId.DownloadAvatars:
                        result = StartDownload(done, out completesLater);
                        break;

                    case SetupStepId.BuildCharacters:
                        result = BuildCharacters();
                        break;

                    case SetupStepId.BuildArena:
                    {
                        ArenaBuildResult arena = ArenaSceneBuilder.Build(ArenaSettings);
                        result = arena.Success ? SetupStepResult.Ok(arena.Summary)
                            : arena.Cancelled ? SetupStepResult.Cancel(arena.Summary)
                            : SetupStepResult.Fail(arena.Summary);
                        break;
                    }

                    case SetupStepId.Play:
                        result = DodgeballMenuItems.PlayArena()
                            ? SetupStepResult.Ok("Entering Play mode.")
                            : SetupStepResult.Cancel("Play cancelled.");
                        break;

                    default:
                        result = SetupStepResult.Fail("Unknown step.");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result = SetupStepResult.Fail($"{step} failed: {e.Message}");
                completesLater = false;
                s_waitingForDownload = false;
                s_downloadCallback = null;
            }

            if (completesLater) return;
            s_busy = false;
            done?.Invoke(result);
            Notify();
        }

        private static SetupStepResult StartDownload(Action<SetupStepResult> done, out bool completesLater)
        {
            completesLater = false;
            List<CharacterData> roster = GameDataGenerator.LoadRoster();
            if (roster.Count == 0) return SetupStepResult.Fail("Generate the game data first (step 2).");

            List<string> avatars = CharacterPipeline.GetRequiredAvatars(roster);
            if (avatars == null || avatars.Count == 0) return SetupStepResult.Ok("No Rocketbox avatars are needed.");
            if (CharacterPipeline.AreAssetsDownloaded(avatars)) return SetupStepResult.Ok($"All {avatars.Count} avatars and the animation set are already on disk.");

            DownloadProgress = 0f;
            DownloadMessage = $"Starting download of {avatars.Count} avatars…";
            s_cancelRequested = false;
            s_waitingForDownload = true;
            s_downloadCallback = done;
            completesLater = true;

            CharacterPipeline.DownloadAsync(avatars, true,
                (progress, message) =>
                {
                    DownloadProgress = Mathf.Clamp01(progress);
                    if (!string.IsNullOrEmpty(message)) DownloadMessage = message;
                    Notify();
                },
                CompleteDownload);

            return SetupStepResult.Ok(DownloadMessage);
        }

        private static void CompleteDownload(bool success, string message)
        {
            if (!s_waitingForDownload) return; // already completed (e.g. both our poll and the pipeline reported)
            s_waitingForDownload = false;
            bool cancelled = s_cancelRequested && !success;
            s_cancelRequested = false;
            s_busy = false;
            DownloadProgress = success ? 1f : DownloadProgress;
            DownloadMessage = message ?? string.Empty;

            Action<SetupStepResult> callback = s_downloadCallback;
            s_downloadCallback = null;
            SetupStepResult result = success
                ? SetupStepResult.Ok(string.IsNullOrEmpty(message) ? "Realistic avatars downloaded." : message)
                : cancelled ? SetupStepResult.Cancel(string.IsNullOrEmpty(message) ? "Download cancelled." : message)
                : SetupStepResult.Fail(string.IsNullOrEmpty(message) ? "Download failed." : message);
            callback?.Invoke(result);
            Notify();
        }

        private static SetupStepResult BuildCharacters()
        {
            List<CharacterData> roster = GameDataGenerator.LoadRoster();
            if (roster.Count == 0) return SetupStepResult.Fail("Generate the game data first (step 2).");

            string report = CharacterPipeline.BuildCharacters(roster);
            AssetDatabase.SaveAssets();

            int missing = 0;
            foreach (CharacterData hero in roster)
                if (hero != null && (hero.modelPrefab == null || hero.animatorController == null)) missing++;

            string text = string.IsNullOrEmpty(report) ? "Characters built." : report;
            return missing == 0
                ? SetupStepResult.Ok(text)
                : SetupStepResult.Fail($"{missing} hero(es) still have no realistic model/animator.\n{text}");
        }

        private static void Record(SetupStepId step, SetupStepResult result)
        {
            SessionState.SetString(KeyMessage + (int)step, result.Message);
            SetupStepState outcome = result.Success ? SetupStepState.Done : result.Cancelled ? SetupStepState.Todo : SetupStepState.Failed;
            SessionState.SetInt(KeyOutcome + (int)step, (int)outcome);

            string line = $"[Dodgeball Ultra] Setup · {step}: {result.Message}";
            if (result.Success || result.Cancelled) Debug.Log(line);
            else Debug.LogWarning(line);
        }

        private static bool SafeIsDownloading()
        {
            try
            {
                return CharacterPipeline.IsDownloading;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Notify()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
