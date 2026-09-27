using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using DodgeballUltra.Audio;
using DodgeballUltra.CameraSystem;
using DodgeballUltra.Characters;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Rendering;
using DodgeballUltra.UI;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// CONTRACT (kernel) - scene entry point. Ensures every manager exists (MatchManager, BallManager, JuiceManager,
    /// VfxManager, AudioManager, camera rig, HUD, Court), configures GameLayers, loads GameConfig (or builds the default
    /// in-memory roster via HeroRosterFactory), builds the runtime arena if the scene has none, then shows hero select
    /// (or auto-starts when <see cref="autoStart"/>).
    /// <para>
    /// Boot order (runs before every other script thanks to <c>DefaultExecutionOrder(-1000)</c>):
    /// <code>
    /// Awake: singleton, collision matrix, frame rate, ActiveConfig (asset or in-memory default), managers (find or create)
    /// Start: court (scene Court or RuntimeArenaBuilder), camera rig, HUD, environment volume + fallback light,
    ///        missing-model warning -> (one frame later, so every manager finished its own Start) hero select / auto-start
    /// </code>
    /// The QuickPlay scene contains nothing but this component: everything else is created here, so the game runs from an
    /// empty scene. Scene-authored managers (e.g. from the editor ArenaSceneBuilder) are reused instead of duplicated.
    /// </para>
    /// <para>Owner module: Match.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        public GameConfig config;
        [Tooltip("Skip hero select and start immediately with the given hero (useful for testing).")]
        public bool autoStart;
        public HeroId autoStartHero = HeroId.Rayne;
        public AI.BotDifficulty autoStartDifficulty = AI.BotDifficulty.Normal;

        [Tooltip("Team of the local player when auto-starting.")]
        public TeamId autoStartTeam = TeamId.Home;
        [Tooltip("Auto-start an AI-vs-AI match with no human player (attract mode, soak tests).")]
        public bool autoStartSpectate;
        [Tooltip("Seed for the auto-start bot line-up (distinct heroes, deterministic per seed). -1 = new line-up every launch.")]
        public int autoStartSeed = 1337;

        [Header("Application")]
        [Tooltip("Application.targetFrameRate on desktop/console (ignored by the platform while vSync is on). Mobile uses 60. " +
                 "0 = leave the platform default.")]
        [Min(0)] public int targetFrameRate = 120;

        [Header("Fallback lighting (only when the scene has no light at all)")]
        [Tooltip("Illuminance (lux) of the fallback key light. Televised indoor sport is lit at ~1500-2500 lux on the court; " +
                 "a single directional key needs a little more to compensate for the missing fill lights.")]
        [Min(0f)] public float fallbackLightLux = 3000f;
        [Tooltip("Colour temperature (K) of the fallback key light. ~5600 K matches LED sports floodlights.")]
        [Range(1500f, 20000f)] public float fallbackLightTemperature = 5600f;

        [Header("Warnings")]
        [Tooltip("Seconds the 'realistic models missing' banner stays on screen when a match starts.")]
        [Min(0f)] public float missingModelBannerSeconds = 8f;

        public static GameBootstrap Instance { get; private set; }
        public GameConfig ActiveConfig { get; private set; }

        private const string SetupWizardMenu = "Dodgeball Ultra > Setup Wizard";
        private static readonly Color WarningColor = new Color(1f, 0.72f, 0.18f);

        private readonly List<UnityEngine.Object> _runtimeObjects = new List<UnityEngine.Object>();
        private readonly List<CharacterData> _missingModels = new List<CharacterData>();
        private readonly List<HeroId> _heroPool = new List<HeroId>(10);

        private Transform _systemsRoot;
        private ThirdPersonCameraRig _cameraRig;
        private HudController _hud;
        private bool _booted;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Dodgeball Ultra] A second GameBootstrap ('{name}') was disabled; '{Instance.name}' drives the game.", this);
                enabled = false;
                Destroy(this);
                return;
            }
            Instance = this;

            GameLayers.ConfigureCollisionMatrix();
            if (targetFrameRate > 0) Application.targetFrameRate = Application.isMobilePlatform ? 60 : targetFrameRate;
            if (Time.timeScale <= 0f) Time.timeScale = 1f;

            ActiveConfig = BuildActiveConfig();
            EnsureManagers();
        }

        private IEnumerator Start()
        {
            if (Instance != this) yield break;

            // Court / arena, camera, HUD and lighting are created in Start: pipeline adapters (the HDRP runtime hooks) install
            // themselves from [RuntimeInitializeOnLoadMethod], which may run after scene Awake, and the arena's floodlights
            // and the gameplay camera need those hooks for physical light units and HDRP camera settings.
            EnsureCourt();
            InitializePresentation();
            CollectMissingModels(true);

            // Let every manager (scene-authored or created in Awake) run its own Start before the first menu / match.
            yield return null;

            _booted = true;
            if (autoStart) StartMatch(CreateAutoSetup());
            else ShowHeroSelect();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            for (int i = 0; i < _runtimeObjects.Count; i++)
                if (_runtimeObjects[i] != null) Destroy(_runtimeObjects[i]);
            _runtimeObjects.Clear();
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Hides hero select and starts a match with <paramref name="setup"/> (bots fill every other slot).</summary>
        public void StartMatch(MatchSetup setup)
        {
            try { HeroSelectScreen.Hide(); }
            catch (Exception e) { Debug.LogException(e, this); }

            var match = MatchManager.Instance != null ? MatchManager.Instance : EnsureComponent<MatchManager>("MatchManager", true);
            if (_hud != null)
            {
                try { _hud.SetVisible(true); }
                catch (Exception e) { Debug.LogException(e, _hud); }
            }

            match.StartMatch(setup, ActiveConfig);
            ShowMissingModelBanner(match);
        }

        /// <summary>Ends the running match (if any) and shows the hero select screen again.</summary>
        public void ReturnToHeroSelect()
        {
            var match = MatchManager.Instance;
            if (match != null)
            {
                match.EndMatch();
                match.EnterHeroSelect();
            }
            ShowHeroSelect();
        }

        /// <summary>
        /// A complete setup with distinct bot heroes, deterministic for <paramref name="seed"/>
        /// (<see cref="MatchSetupUtil.RandomSeed"/> = a fresh line-up). The hero select screen can use it to fill bots.
        /// </summary>
        public MatchSetup CreateSetup(HeroId localHero, TeamId localTeam, AI.BotDifficulty difficulty, bool spectate = false,
            int seed = MatchSetupUtil.RandomSeed)
        {
            var cfg = ActiveConfig != null ? ActiveConfig : BuildActiveConfig();
            cfg.GetAvailableHeroes(_heroPool);
            int playersPerTeam = cfg.rules != null ? cfg.rules.playersPerTeam : 3;
            return MatchSetupUtil.CreateAutoSetup(localHero, localTeam, difficulty, playersPerTeam,
                MatchSetupUtil.ResolveSeed(seed), spectate, _heroPool);
        }

        // ------------------------------------------------------------------ boot steps

        /// <summary>The assigned config (copied and completed if it lacks rules/juice/heroes) or an in-memory default.</summary>
        private GameConfig BuildActiveConfig()
        {
            if (config == null)
            {
                var runtime = ScriptableObject.CreateInstance<GameConfig>();
                runtime.name = "GameConfig (Runtime Default)";
                runtime.roster = CreateDefaultRoster();
                runtime.rules = Track(ScriptableObject.CreateInstance<MatchRules>());
                runtime.rules.name = "MatchRules (Default)";
                runtime.juice = Track(JuiceProfile.CreateDefault());
                Track(runtime);
                Debug.Log("[Dodgeball Ultra] No GameConfig assigned to GameBootstrap: using the built-in hero roster and default rules. " +
                          $"Run '{SetupWizardMenu}' to generate the config with the realistic Rocketbox human models.", this);
                return runtime;
            }

            bool hasHero = false;
            for (int i = 0; i < config.roster.Count && !hasHero; i++) hasHero = config.roster[i] != null;
            if (config.rules != null && config.juice != null && hasHero) return config;

            // Never mutate the asset at runtime (in the editor that would silently change it on disk): complete a copy.
            var copy = Track(Instantiate(config));
            copy.name = config.name + " (Runtime)";
            copy.roster = new List<CharacterData>(config.roster);
            if (copy.rules == null) copy.rules = Track(ScriptableObject.CreateInstance<MatchRules>());
            if (copy.juice == null) copy.juice = Track(JuiceProfile.CreateDefault());
            if (!hasHero) copy.roster = CreateDefaultRoster();
            Debug.LogWarning($"[Dodgeball Ultra] GameConfig '{config.name}' is incomplete (rules, juice or heroes missing); " +
                             "defaults were filled in for this session.", config);
            return copy;
        }

        private List<CharacterData> CreateDefaultRoster()
        {
            try
            {
                var roster = HeroRosterFactory.CreateDefaultRoster();
                if (roster != null)
                {
                    for (int i = 0; i < roster.Count; i++) if (roster[i] != null) Track(roster[i]);
                    return roster;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            Debug.LogError("[Dodgeball Ultra] The default hero roster could not be created.", this);
            return new List<CharacterData>();
        }

        /// <summary>Finds every manager in the scene, creating the missing ones.</summary>
        private void EnsureManagers()
        {
            EnsureComponent<BallManager>("BallManager", true);
            EnsureComponent<MatchManager>("MatchManager", true);

            var juice = EnsureComponent<JuiceManager>("JuiceManager", true);
            if (juice != null && ActiveConfig != null && ActiveConfig.juice != null) juice.Profile = ActiveConfig.juice;

            EnsureComponent<VfxManager>("VfxManager", true);
            EnsureComponent<AudioManager>("AudioManager", true);
            _cameraRig = EnsureComponent<ThirdPersonCameraRig>("Camera Rig", false);
            _hud = EnsureComponent<HudController>("HUD", false);
        }

        private void EnsureCourt()
        {
            if (Court.Instance != null || FindFirstObjectByType<Court>() != null) return;
            try
            {
                var court = Arena.RuntimeArenaBuilder.Build();
                if (court != null) return;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            BuildEmergencyCourt();
        }

        /// <summary>Last-resort playable surface if the arena builder failed: a court component and a collidable floor.</summary>
        private void BuildEmergencyCourt()
        {
            Debug.LogError("[Dodgeball Ultra] The runtime arena could not be built; using a bare emergency floor.", this);
            var root = new GameObject("Court (Emergency)");
            var court = root.AddComponent<Court>();

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localScale = new Vector3(court.WallDistanceX * 2f, 0.2f, court.WallDistanceZ * 2f);
            floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            floor.layer = GameLayers.Court;
            try
            {
                var renderer = floor.GetComponent<Renderer>();
                if (renderer != null)
                {
                    // Lacquered maple tone, physically plausible smoothness.
                    renderer.sharedMaterial = Track(MaterialFactory.CreateLit("Court Floor (Emergency)", new Color(0.72f, 0.52f, 0.32f), 0.62f));
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private void InitializePresentation()
        {
            if (_cameraRig != null)
            {
                try { _cameraRig.Initialize(Camera.main); }
                catch (Exception e) { Debug.LogException(e, _cameraRig); }
            }

            EnsureLighting();

            if (_hud != null)
            {
                try
                {
                    _hud.Initialize();
                    _hud.SetVisible(false); // shown when a match starts
                }
                catch (Exception e)
                {
                    Debug.LogException(e, _hud);
                }
            }
        }

        /// <summary>Environment volume through the pipeline hooks, plus a physically based key light if the scene has none.</summary>
        private void EnsureLighting()
        {
            var hooks = RuntimeRenderingHooks.Active;
            if (hooks != null)
            {
                try { hooks.EnsureEnvironmentVolume(SystemsRoot); }
                catch (Exception e) { Debug.LogException(e, this); }
            }

            if (FindFirstObjectByType<Light>() != null) return;

            var go = new GameObject("Key Light (Fallback)");
            go.transform.SetParent(SystemsRoot, false);
            // High, slightly off-axis key: readable silhouettes and contact shadows without flattening the faces.
            go.transform.rotation = Quaternion.Euler(55f, -32f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;

            if (hooks != null)
            {
                try
                {
                    hooks.ConfigureLight(light, fallbackLightLux, fallbackLightTemperature);
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }

            // Non-physical pipelines: plausible relative intensity, colour from the correlated colour temperature and a
            // neutral indoor trilight ambient so shadowed sides are not pitch black.
            light.intensity = 1.15f;
            light.color = Mathf.CorrelatedColorTemperatureToRGB(fallbackLightTemperature);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.53f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.36f, 0.34f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.16f, 0.14f);
        }

        private void ShowHeroSelect()
        {
            if (_hud != null)
            {
                try { _hud.SetVisible(false); }
                catch (Exception e) { Debug.LogException(e, _hud); }
            }

            var match = MatchManager.Instance;
            if (match != null && match.Phase != MatchPhase.HeroSelect) match.EnterHeroSelect();

            try
            {
                HeroSelectScreen.Show(ActiveConfig, StartMatch);
            }
            catch (Exception e)
            {
                // The game must stay playable even if the menu fails: fall back to a quick-play start.
                Debug.LogException(e, this);
                Debug.LogWarning("[Dodgeball Ultra] Hero select is unavailable; starting a quick-play match instead.", this);
                StartMatch(CreateAutoSetup());
            }
        }

        private MatchSetup CreateAutoSetup() =>
            CreateSetup(autoStartHero, autoStartTeam, autoStartDifficulty, autoStartSpectate, autoStartSeed);

        // ------------------------------------------------------------------ missing realistic models

        /// <summary>Heroes without a realistic model prefab: logs a clear, actionable warning once at boot.</summary>
        private void CollectMissingModels(bool log)
        {
            _missingModels.Clear();
            if (ActiveConfig == null) return;
            ActiveConfig.GetHeroesWithoutModel(_missingModels);
            if (!log || _missingModels.Count == 0) return;

            var names = new StringBuilder();
            for (int i = 0; i < _missingModels.Count; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(string.IsNullOrEmpty(_missingModels[i].displayName) ? _missingModels[i].name : _missingModels[i].displayName);
            }
            Debug.LogWarning(
                $"[Dodgeball Ultra] {_missingModels.Count} hero(es) have no realistic human model (CharacterData.modelPrefab): {names}. " +
                $"They will use a labelled placeholder. Open Unity and run '{SetupWizardMenu}' to download the Microsoft Rocketbox " +
                "avatars (MIT) and motion-capture clips, import them as Humanoid and link them to the heroes.", this);
        }

        private void ShowMissingModelBanner(MatchManager match)
        {
            if (_hud == null || match == null || missingModelBannerSeconds <= 0f) return;

            int missing = 0;
            var players = match.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && (p.Character == null || p.Character.modelPrefab == null)) missing++;
            }
            if (missing == 0) return;

            try
            {
                _hud.ShowBanner($"REALISTIC MODELS MISSING - run {SetupWizardMenu} in the Unity editor", WarningColor, missingModelBannerSeconds);
            }
            catch (Exception e)
            {
                Debug.LogException(e, _hud);
            }
        }

        // ------------------------------------------------------------------ helpers

        private T EnsureComponent<T>(string objectName, bool underSystemsRoot) where T : Component
        {
            var existing = FindFirstObjectByType<T>();
            if (existing != null) return existing;

            var go = new GameObject(objectName);
            if (underSystemsRoot) go.transform.SetParent(SystemsRoot, false);
            return go.AddComponent<T>();
        }

        private Transform SystemsRoot
        {
            get
            {
                if (_systemsRoot == null) _systemsRoot = new GameObject("[Dodgeball Ultra Systems]").transform;
                return _systemsRoot;
            }
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            if (obj != null) _runtimeObjects.Add(obj);
            return obj;
        }

        /// <summary>True once the boot sequence finished and the first menu / match was shown.</summary>
        public bool IsBooted => _booted;
    }
}
