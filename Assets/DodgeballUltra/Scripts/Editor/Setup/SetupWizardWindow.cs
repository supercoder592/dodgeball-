using System;
using System.Collections.Generic;
using System.IO;
using DodgeballUltra.Editor.ArenaScene;
using DodgeballUltra.Editor.Characters;
using DodgeballUltra.Editor.Data;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// <c>Dodgeball Ultra ▸ Setup Wizard</c>: takes a fresh clone to a playable, realistic 3v3 match in six explained steps
    /// (English + Traditional Chinese), each with a status badge, what is missing, and a button - plus "Run All".
    /// Opens automatically the first time the project is opened (<see cref="SetupWizardAutoOpen"/>).
    /// <para>All work is delegated to <see cref="SetupRunner"/> so it runs outside OnGUI and survives closing the window.</para>
    /// </summary>
    public sealed class SetupWizardWindow : EditorWindow
    {
        private struct StepText
        {
            public string TitleEn, TitleZh, BodyEn, BodyZh;
        }

        private static readonly SetupStepId[] Steps = (SetupStepId[])Enum.GetValues(typeof(SetupStepId));
        private static readonly int[] TextureSizes = { 512, 1024, 2048 };
        private static readonly string[] TextureSizeLabels = { "512 (fast)", "1024 (default)", "2048 (sharpest)" };
        private const int MaxListedItems = 8;

        private readonly Dictionary<SetupStepId, SetupStepStatus> _status = new Dictionary<SetupStepId, SetupStepStatus>();
        private Vector2 _scroll;
        private bool _arenaOptions;
        private bool _refreshPending;
        private double _lastRefreshTime;
        private double _lastRepaintTime;

        private bool _stylesReady;
        private GUIStyle _title, _subtitle, _stepTitle, _stepTitleZh, _body, _bodyZh, _badge, _listItem, _box, _summary;

        // ------------------------------------------------------------------ open

        /// <summary>Opens (or focuses) the wizard.</summary>
        public static SetupWizardWindow Open()
        {
            var window = GetWindow<SetupWizardWindow>(false, "Dodgeball Setup", true);
            window.titleContent = new GUIContent("Dodgeball Setup", "Dodgeball Ultra · Setup Wizard · 設定精靈");
            window.minSize = new Vector2(560f, 600f);
            window.RefreshStatus();
            window.Show();
            return window;
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            SetupRunner.Changed += OnRunnerChanged;
            RefreshStatus();
        }

        private void OnDisable() => SetupRunner.Changed -= OnRunnerChanged;

        private void OnFocus() => RefreshStatus();

        private void OnProjectChange() => _refreshPending = true;

        private void OnRunnerChanged()
        {
            // Downloads report progress many times per second: repaint always, re-evaluate at most every 0.5 s.
            _refreshPending = true;
            Repaint();
        }

        private void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_refreshPending && now - _lastRefreshTime > 0.5)
            {
                _refreshPending = false;
                RefreshStatus();
                Repaint();
            }
            else if ((SetupRunner.IsBusy || SetupRunner.IsRunningAll) && now - _lastRepaintTime > 0.25)
            {
                _lastRepaintTime = now;
                Repaint();
            }
        }

        private void RefreshStatus()
        {
            _lastRefreshTime = EditorApplication.timeSinceStartup;
            foreach (SetupStepId step in Steps)
            {
                SetupStepStatus status = SetupChecks.Evaluate(step);

                if (SetupRunner.IsBusy && SetupRunner.ActiveStep == step)
                {
                    status.State = SetupStepState.Running;
                }
                else if (SetupRunner.LastStatusOf(step) == SetupStepState.Failed && status.State != SetupStepState.Done)
                {
                    status.State = SetupStepState.Failed;
                }
                _status[step] = status;
            }
        }

        // ------------------------------------------------------------------ GUI

        private void OnGUI()
        {
            InitStyles();
            if (_status.Count == 0) RefreshStatus();

            DrawHeader();
            DrawEnvironment();
            DrawRunAll();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (SetupStepId step in Steps) DrawStep(step);
            EditorGUILayout.Space(6f);
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label("Dodgeball Ultra · Setup Wizard  設定精靈", _title);
            GUILayout.Label("Six steps from a fresh clone to a realistic 3v3 superpowered dodgeball match with real, motion-captured " +
                            "human characters. Run them one by one or press Run All.", _subtitle);
            GUILayout.Label("六個步驟，從全新專案到使用真人動作捕捉角色的寫實 3v3 超能力躲避球。可逐步執行，或按「全部執行」。", _subtitle);
            EditorGUILayout.Space(4f);
        }

        private void DrawEnvironment()
        {
            IEditorRenderingHooks hooks = EditorRenderingHooks.Active;
            string pipeline = hooks.PipelineName + (hooks.IsActive ? " (active)" : " (not active yet)");
            string text = $"Unity {Application.unityVersion}   ·   Render pipeline: {pipeline}   ·   Colour space: {PlayerSettings.colorSpace}   ·   " +
                          $"Active input: {ProjectConfigurator.GetConfiguredInputHandling()}";
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawRunAll()
        {
            bool busy = SetupRunner.IsBusy || SetupRunner.IsRunningAll;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(busy || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button(new GUIContent("Run All · 全部執行", "Runs steps 1-5 in order, stopping at the first problem."),
                            GUILayout.Height(30f)))
                        SetupRunner.RunAll();
                }

                bool play = GUILayout.Toggle(SetupRunner.PlayWhenDone,
                    new GUIContent(" Play when finished · 完成後開始遊戲", "Enter Play mode in the arena after the last step."),
                    GUILayout.Width(250f), GUILayout.Height(30f));
                if (play != SetupRunner.PlayWhenDone) SetupRunner.PlayWhenDone = play;
            }

            if (SetupRunner.IsRunningAll)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    SetupStepId step = SetupRunner.RunAllStep;
                    EditorGUILayout.HelpBox($"Running step {(int)step + 1}/{Steps.Length}: {Text(step).TitleEn}…  執行中：{Text(step).TitleZh}",
                        MessageType.Info);
                    if (GUILayout.Button("Stop · 停止", GUILayout.Width(90f), GUILayout.Height(38f))) SetupRunner.StopRunAll();
                }
            }
            EditorGUILayout.Space(2f);
        }

        private void DrawStep(SetupStepId step)
        {
            if (!_status.TryGetValue(step, out SetupStepStatus status)) return;
            StepText text = Text(step);

            using (new EditorGUILayout.VerticalScope(_box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBadge(status.State);
                    GUILayout.Space(6f);
                    GUILayout.Label($"{(int)step + 1}. {text.TitleEn}", _stepTitle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(text.TitleZh, _stepTitleZh, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    DrawStepButton(step, status);
                }

                GUILayout.Label(text.BodyEn, _body);
                GUILayout.Label(text.BodyZh, _bodyZh);

                if (!string.IsNullOrEmpty(status.Summary)) GUILayout.Label(status.Summary, _summary);
                DrawList(status.Missing, "Missing · 缺少:");
                if (status.Warnings.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", status.Warnings), MessageType.Warning);

                if (status.State == SetupStepState.Failed)
                {
                    string message = SetupRunner.GetLastMessage(step);
                    if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(Truncate(message, 900), MessageType.Error);
                }

                DrawStepExtras(step, status);
            }
        }

        private void DrawStepButton(SetupStepId step, SetupStepStatus status)
        {
            bool canRun = !SetupRunner.IsBusy && !SetupRunner.IsRunningAll && !EditorApplication.isPlayingOrWillChangePlaymode &&
                          status.State != SetupStepState.Blocked;
            string label = step == SetupStepId.Play
                ? "Play · 開始"
                : status.State == SetupStepState.Done || status.State == SetupStepState.Warning ? "Re-run · 重新執行" : "Run · 執行";

            using (new EditorGUI.DisabledScope(!canRun))
            {
                if (GUILayout.Button(label, GUILayout.Width(130f), GUILayout.Height(22f))) SetupRunner.RunStep(step);
            }
        }

        // ------------------------------------------------------------------ per-step extras

        private void DrawStepExtras(SetupStepId step, SetupStepStatus status)
        {
            switch (step)
            {
                case SetupStepId.ConfigureProject: DrawProjectExtras(); break;
                case SetupStepId.GenerateData: DrawDataExtras(status); break;
                case SetupStepId.DownloadAvatars: DrawDownloadExtras(status); break;
                case SetupStepId.BuildCharacters: DrawCharacterExtras(); break;
                case SetupStepId.BuildArena: DrawArenaExtras(); break;
                case SetupStepId.Play: DrawPlayExtras(); break;
            }
        }

        private void DrawProjectExtras()
        {
            InputHandlingMode input = ProjectConfigurator.GetConfiguredInputHandling();
            if (input != InputHandlingMode.Both)
            {
                EditorGUILayout.HelpBox("Active Input Handling · 輸入系統設定\n" + ProjectConfigurator.InputHelpEnglish + "\n" +
                                        ProjectConfigurator.InputHelpChinese, MessageType.Info);
            }

            ProjectConfigurator.CheckLayers(out bool conflicts);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Player Settings · 開啟 Player 設定", EditorStyles.miniButton, GUILayout.Width(230f)))
                    ProjectConfigurator.OpenPlayerSettings();
                if (conflicts && GUILayout.Button("Rename layers 8-15 · 修正圖層名稱", EditorStyles.miniButton, GUILayout.Width(210f)))
                {
                    if (EditorUtility.DisplayDialog("Rename layers? · 重新命名圖層？",
                            "Layers 8-15 are used by Dodgeball Ultra (GameLayers). Rename them to DU_Player … DU_Visual?\n" +
                            "Objects of other projects/assets using those layers keep their layer index.",
                            "Rename · 重新命名", "Cancel · 取消"))
                    {
                        ProjectConfigurator.EnsureLayersAndTags(true, out _);
                        RefreshStatus();
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawDataExtras(SetupStepStatus status)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("If assets exist · 已有資料時", EditorStyles.miniLabel, GUILayout.Width(150f));
                var options = new[]
                {
                    new GUIContent("Keep my tuning (create + repair) · 保留我的調整",
                        "Only missing assets and broken links are created/repaired; all existing values stay."),
                    new GUIContent("Update to spec values (keep models) · 更新為規格數值",
                        "Stats and abilities return to the design spec; model, portrait, animator and icons are kept."),
                };
                int mode = EditorGUILayout.Popup((int)SetupRunner.DataMode, options);
                if (mode != (int)SetupRunner.DataMode) SetupRunner.DataMode = (DataUpdateMode)mode;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(SetupRunner.IsBusy || SetupRunner.IsRunningAll))
                {
                    if (GUILayout.Button("Reset to defaults… · 重設為預設值", EditorStyles.miniButton, GUILayout.Width(210f)) &&
                        DodgeballMenuItems.ConfirmResetData())
                    {
                        EditorApplication.delayCall += () =>
                        {
                            DataGenerationResult result = GameDataGenerator.Generate(DataUpdateMode.ResetToDefaults);
                            if (!result.Success) EditorUtility.DisplayDialog("Dodgeball Ultra", result.Report, "OK");
                            RefreshStatus();
                            Repaint();
                        };
                    }
                }

                using (new EditorGUI.DisabledScope(status.State == SetupStepState.Todo && GameDataGenerator.LoadGameConfig() == null))
                {
                    if (GUILayout.Button("Select GameConfig · 選取", EditorStyles.miniButton, GUILayout.Width(170f)))
                    {
                        var config = GameDataGenerator.LoadGameConfig();
                        if (config != null)
                        {
                            Selection.activeObject = config;
                            EditorGUIUtility.PingObject(config);
                        }
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawDownloadExtras(SetupStepStatus status)
        {
            EditorGUILayout.HelpBox(
                "Licence · 授權: Microsoft Rocketbox Avatar Library, © 2020 Microsoft, MIT licence. Models and motion-capture clips are " +
                "downloaded from the pinned commit on GitHub (Tools/rocketbox_manifest.json) into " + CharacterPipeline.DownloadRoot +
                " (git-ignored, ≈90 MB per avatar). Research use: please cite Gonzalez-Franco et al., Frontiers in Virtual Reality 2020.\n" +
                "Microsoft Rocketbox 真人模型庫，© 2020 Microsoft，MIT 授權。模型與動作捕捉動畫從 GitHub 固定版本下載到本機（不提交到 git，每名角色約 90 MB）。",
                MessageType.None);

            if (SetupRunner.IsBusy && SetupRunner.ActiveStep == SetupStepId.DownloadAvatars)
            {
                Rect bar = GUILayoutUtility.GetRect(18f, 20f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(bar, SetupRunner.DownloadProgress,
                    $"{SetupRunner.DownloadProgress * 100f:0}%  {Truncate(SetupRunner.DownloadMessage, 90)}");
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel download · 取消下載", GUILayout.Width(180f))) SetupRunner.CancelDownload();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Licence & notices · 授權聲明", EditorStyles.miniButton, GUILayout.Width(190f)))
                {
                    string notices = DodgeballEditorPaths.ToAbsolute(DodgeballEditorPaths.ThirdPartyNoticesPath);
                    if (File.Exists(notices)) EditorUtility.OpenWithDefaultApp(notices);
                    else Application.OpenURL(DodgeballEditorPaths.RocketboxRepositoryUrl);
                }
                if (GUILayout.Button("Rocketbox on GitHub", EditorStyles.miniButton, GUILayout.Width(150f)))
                    Application.OpenURL(DodgeballEditorPaths.RocketboxRepositoryUrl);

                string folder = DodgeballEditorPaths.ToAbsolute(CharacterPipeline.DownloadRoot);
                using (new EditorGUI.DisabledScope(!Directory.Exists(folder)))
                {
                    if (GUILayout.Button("Show folder · 開啟資料夾", EditorStyles.miniButton, GUILayout.Width(160f)))
                        EditorUtility.RevealInFinder(folder);
                }
                GUILayout.FlexibleSpace();
            }

            if (status.State != SetupStepState.Running && status.State != SetupStepState.Done)
                GUILayout.Label("Command line alternative: python3 Tools/fetch_rocketbox.py --avatars all-heroes --animations", EditorStyles.miniLabel);
        }

        private void DrawCharacterExtras()
        {
            string report = SetupRunner.GetLastMessage(SetupStepId.BuildCharacters);
            if (string.IsNullOrEmpty(report) || SetupRunner.LastStatusOf(SetupStepId.BuildCharacters) != SetupStepState.Done) return;
            GUILayout.Label("Last build report (full report in the Console) · 最近一次建立報告", EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(Truncate(report, 1200), EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(Mathf.Min(120f, 14f * (1 + CountLines(report)))));
        }

        private void DrawArenaExtras()
        {
            EditorGUILayout.HelpBox(
                "Optional: bake indirect light for extra realism - Window ▸ Rendering ▸ Lighting ▸ Generate Lighting (a few minutes; " +
                "architecture is already static with lightmap UVs and light probes). Not needed to play.\n" +
                "選用：烘焙間接光以增加真實感 - Window ▸ Rendering ▸ Lighting ▸ Generate Lighting（需數分鐘，遊玩不需要）。",
                MessageType.None);

            _arenaOptions = EditorGUILayout.Foldout(_arenaOptions, "Arena options · 場景選項", true);
            if (_arenaOptions)
            {
                ArenaSceneSettings s = SetupRunner.ArenaSettings;
                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.IndentLevelScope())
                {
                    s.textureResolution = EditorGUILayout.IntPopup(new GUIContent("Texture resolution", "Per PBR map."),
                        s.textureResolution, ToContents(TextureSizeLabels), TextureSizes);
                    s.regenerateTextures = EditorGUILayout.Toggle(new GUIContent("Regenerate textures",
                        "Rebuild the procedural textures even if an up-to-date set exists."), s.regenerateTextures);
                    s.floodlightLumen = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Floodlight flux (lm)",
                        "Per fixture. Default ≈1000 lux on the court."), s.floodlightLumen));
                    s.floodlightTemperatureK = EditorGUILayout.Slider(new GUIContent("Floodlight temperature (K)"), s.floodlightTemperatureK, 2700f, 7500f);
                    s.exposureEV100 = EditorGUILayout.Slider(new GUIContent("Exposure (EV100)", "Lower = brighter."), s.exposureEV100, 6f, 16f);
                    s.fillIlluminanceLux = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Overhead fill (lux)",
                        "Soft light bounced off roof and walls."), s.fillIlluminanceLux));
                    s.createLightProbes = EditorGUILayout.Toggle(new GUIContent("Light probes"), s.createLightProbes);
                    using (new EditorGUI.DisabledScope(!s.createLightProbes))
                        s.lightProbeSpacing = EditorGUILayout.Slider(new GUIContent("Probe spacing (m)"), s.lightProbeSpacing, 1f, 6f);
                    s.hdri = (Cubemap)EditorGUILayout.ObjectField(new GUIContent("HDRI (optional)", "Cubemap for sky reflections."),
                        s.hdri, typeof(Cubemap), false);
                }
                if (EditorGUI.EndChangeCheck()) SetupRunner.SaveArenaSettings();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Reset options · 重設選項", EditorStyles.miniButton, GUILayout.Width(160f)))
                    {
                        SetupRunner.ResetArenaSettings();
                        GUI.FocusControl(null);
                    }
                }
            }

            if (ArenaSceneBuilder.SceneExists)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || SetupRunner.IsBusy))
                    {
                        if (GUILayout.Button("Open Arena scene · 開啟場景", EditorStyles.miniButton, GUILayout.Width(190f)))
                            EditorApplication.delayCall += () => DodgeballMenuItems.OpenArenaScene();
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawPlayExtras()
        {
            GUILayout.Label("Controls: WASD move · mouse look · LMB hold to charge-throw · RMB catch (time it!) · Q pass · E pick up · " +
                            "F skill · R ultimate · Shift sprint · Space jump · C slide", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || SetupRunner.IsBusy))
                {
                    if (GUILayout.Button("Open QuickPlay · 快速遊戲場景", EditorStyles.miniButton, GUILayout.Width(200f)))
                    {
                        EditorApplication.delayCall += () =>
                        {
                            if (UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(DodgeballEditorPaths.QuickPlayScenePath);
                        };
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh · 重新整理", EditorStyles.toolbarButton, GUILayout.Width(130f))) RefreshStatus();
                if (GUILayout.Button("Architecture docs", EditorStyles.toolbarButton, GUILayout.Width(130f)))
                {
                    string docs = DodgeballEditorPaths.ToAbsolute("docs/ARCHITECTURE.md");
                    if (File.Exists(docs)) EditorUtility.OpenWithDefaultApp(docs);
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label("Tip: QuickPlay.unity plays any time (built at runtime).", EditorStyles.miniLabel);
            }
        }

        // ------------------------------------------------------------------ drawing helpers

        private void DrawBadge(SetupStepState state)
        {
            string text;
            Color color;
            switch (state)
            {
                case SetupStepState.Done: text = "DONE 完成"; color = new Color(0.20f, 0.58f, 0.30f); break;
                case SetupStepState.Warning: text = "CHECK 注意"; color = new Color(0.80f, 0.56f, 0.10f); break;
                case SetupStepState.Blocked: text = "WAIT 等待"; color = new Color(0.42f, 0.42f, 0.42f); break;
                case SetupStepState.Running: text = "RUNNING 執行中"; color = new Color(0.18f, 0.48f, 0.85f); break;
                case SetupStepState.Failed: text = "FAILED 失敗"; color = new Color(0.76f, 0.22f, 0.20f); break;
                default: text = "TO DO 待辦"; color = new Color(0.30f, 0.40f, 0.58f); break;
            }
            Rect rect = GUILayoutUtility.GetRect(104f, 20f, GUILayout.Width(104f), GUILayout.Height(20f));
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, text, _badge);
        }

        private void DrawList(List<string> items, string heading)
        {
            if (items.Count == 0) return;
            GUILayout.Label(heading, EditorStyles.miniBoldLabel);
            int shown = Mathf.Min(items.Count, MaxListedItems);
            for (int i = 0; i < shown; i++) GUILayout.Label("•  " + items[i], _listItem);
            if (items.Count > shown) GUILayout.Label($"   … and {items.Count - shown} more", _listItem);
        }

        private void InitStyles()
        {
            if (_stylesReady && _title != null) return;
            _stylesReady = true;

            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, wordWrap = true };
            _subtitle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            _stepTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _stepTitleZh = new GUIStyle(EditorStyles.label) { fontSize = 12 };
            _stepTitleZh.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.3f, 0.3f, 0.3f);
            _body = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 11 };
            _bodyZh = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            _summary = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { fontStyle = FontStyle.Bold };
            _listItem = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            _listItem.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.62f, 0.55f) : new Color(0.6f, 0.12f, 0.08f);
            _badge = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
            _badge.normal.textColor = Color.white;
            _box = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(4, 4, 4, 6),
            };
        }

        private static GUIContent[] ToContents(string[] labels)
        {
            var result = new GUIContent[labels.Length];
            for (int i = 0; i < labels.Length; i++) result[i] = new GUIContent(labels[i]);
            return result;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
            return text.Substring(0, max) + " …";
        }

        private static int CountLines(string text)
        {
            int lines = 0;
            foreach (char c in text) if (c == '\n') lines++;
            return lines;
        }

        // ------------------------------------------------------------------ step texts

        private static StepText Text(SetupStepId step)
        {
            switch (step)
            {
                case SetupStepId.ConfigureProject:
                    return new StepText
                    {
                        TitleEn = "Configure Project",
                        TitleZh = "設定專案",
                        BodyEn = "Activates the realistic render pipeline (HDRP: subsurface-scattering skin, screen-space reflections, " +
                                 "ambient occlusion, contact shadows, volumetric haze), switches to Linear colour space and checks the " +
                                 "physics layers 8-15 and the input settings.",
                        BodyZh = "啟用寫實渲染管線（HDRP：皮膚次表面散射、螢幕空間反射、環境光遮蔽、接觸陰影、體積霧），切換為線性色彩空間，" +
                                 "並檢查物理圖層 8-15 與輸入設定。",
                    };
                case SetupStepId.GenerateData:
                    return new StepText
                    {
                        TitleEn = "Generate Game Data",
                        TitleZh = "產生遊戲資料",
                        BodyEn = "Creates the 10 heroes and their 30 abilities as editable assets with the design-spec values, plus the " +
                                 "match rules, the hit-feel (juice) profile and the GameConfig. Safe to re-run: your tuning and model " +
                                 "links are kept.",
                        BodyZh = "以設計規格數值建立 10 名英雄與 30 個技能的可編輯資料，以及比賽規則、打擊感設定與 GameConfig。" +
                                 "可重複執行：會保留你的調整與模型連結。",
                    };
                case SetupStepId.DownloadAvatars:
                    return new StepText
                    {
                        TitleEn = "Download Realistic Avatars",
                        TitleZh = "下載真人模型",
                        BodyEn = "Downloads the real, rigged human models the heroes use and the motion-capture animation clips " +
                                 "(Microsoft Rocketbox, MIT). Runs in the background - you can keep working.",
                        BodyZh = "下載英雄使用的真人模型（完整骨架）與動作捕捉動畫（Microsoft Rocketbox，MIT 授權）。在背景下載，可繼續工作。",
                    };
                case SetupStepId.BuildCharacters:
                    return new StepText
                    {
                        TitleEn = "Build Characters",
                        TitleZh = "建立角色",
                        BodyEn = "Imports each avatar as a Humanoid (enforced T-pose), creates realistic skin/hair/clothing materials " +
                                 "and LODs, generates the animator controllers from the mocap clips, makes a prefab and links it to the hero.",
                        BodyZh = "將每個模型匯入為 Humanoid（強制 T-Pose），建立寫實的皮膚、頭髮、服裝材質與 LOD，由動捕片段產生動畫控制器，" +
                                 "製作 Prefab 並連結到英雄資料。",
                    };
                case SetupStepId.BuildArena:
                    return new StepText
                    {
                        TitleEn = "Build Arena Scene",
                        TitleZh = "建立球場場景",
                        BodyEn = "Generates the indoor arena - maple court with painted lines, run-off, padded walls, bleachers and a " +
                                 "floodlight rig - with PBR textures, physically based lighting, a camera and the game bootstrap, and " +
                                 "saves Scenes/Arena.unity (first in Build Settings).",
                        BodyZh = "產生室內體育館：楓木球場與邊線、緩衝區、防護牆、看台與泛光燈架，使用 PBR 貼圖與物理正確燈光，" +
                                 "加入攝影機與遊戲啟動器，並儲存為 Scenes/Arena.unity（加入 Build Settings 第一位）。",
                    };
                default:
                    return new StepText
                    {
                        TitleEn = "Play",
                        TitleZh = "開始遊戲",
                        BodyEn = "Opens the arena and enters Play mode: pick a hero and play 3v3 (you + 5 AI).",
                        BodyZh = "開啟球場並進入 Play 模式：選擇英雄，進行 3 對 3 對戰（你 + 5 名 AI）。",
                    };
            }
        }
    }
}
