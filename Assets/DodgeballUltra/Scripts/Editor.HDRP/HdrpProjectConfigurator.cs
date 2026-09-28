using System;
using System.Reflection;
using System.Text;
using DodgeballUltra.Rendering.HDRP;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Editor.HDRP
{
    /// <summary>
    /// Project-level HDRP setup used by <see cref="HdrpEditorRenderingHooks.ConfigureProject"/>:
    /// <list type="number">
    /// <item>linear colour space;</item>
    /// <item>an HDRenderPipelineAsset (the project's current HDRP default, else
    /// <see cref="PipelineAssetPath"/>, created if missing) with the realistic feature set enabled: SSS, SSR, SSAO,
    /// volumetrics, motion vectors (TAA), decals, light layers, shadow mask, 4k punctual shadow atlas with PCSS-quality
    /// filtering, and Light Probe Groups as the probe system (the arena uses a baked LightProbeGroup grid);</item>
    /// <item>that asset assigned as the default render pipeline and to every quality level that has no HDRP asset
    /// (levels that already reference an HDRP asset keep it and get the feature set enabled);</item>
    /// <item>HDRP global settings (normally created by the engine when the pipeline starts; see
    /// <see cref="EnsureGlobalSettings"/>);</item>
    /// <item>the <see cref="HdrpRenderingSettings"/> tuning asset in a Resources folder.</item>
    /// </list>
    /// Idempotent: features are only ever switched on and resolutions only raised.
    /// </summary>
    public static class HdrpProjectConfigurator
    {
        /// <summary>Folder of the generated rendering assets.</summary>
        public const string GeneratedRenderingFolder = "Assets/DodgeballUltra/Generated/Rendering";

        /// <summary>Pipeline asset created when the project has none.</summary>
        public const string PipelineAssetPath = GeneratedRenderingFolder + "/DU_HDRenderPipeline.asset";

        /// <summary>Runtime tuning asset (loaded by the runtime installer through Resources).</summary>
        public const string SettingsAssetPath = "Assets/DodgeballUltra/Generated/Resources/" + HdrpRenderingSettings.ResourcesPath + ".asset";

        /// <summary>Punctual/area shadow atlas resolution requested (texels).</summary>
        public const int ShadowAtlasResolution = 4096;

        /// <summary>Maximum per-light shadow map resolution requested (texels).</summary>
        public const int MaxShadowMapResolution = 2048;

        /// <summary>Runs the whole project configuration. Returns the default HDRP asset in use.</summary>
        public static HDRenderPipelineAsset Configure()
        {
            var report = new StringBuilder("[DU HDRP] Project configuration:\n");

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                report.AppendLine("  - colour space set to Linear");
            }

            HDRenderPipelineAsset asset = ResolvePipelineAsset(report, out bool ownAsset);
            EnableRealisticFeatures(asset, ownAsset, report);
            AssignDefaultPipeline(asset, report);
            AssignQualityLevels(asset, report);
            EnsureSettingsAsset(report);
            EnsureGlobalSettings(report);

            AssetDatabase.SaveAssets();
            Debug.Log(report.ToString());
            return asset;
        }

        /// <summary>Loads the project's <see cref="HdrpRenderingSettings"/> asset, or in-memory defaults.</summary>
        public static HdrpRenderingSettings LoadSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<HdrpRenderingSettings>(SettingsAssetPath);
            return settings != null ? settings : HdrpRenderingSettings.LoadOrCreateDefault();
        }

        /// <summary>The HDRP asset assigned as the project's default render pipeline (null when none).</summary>
        public static HDRenderPipelineAsset GetDefaultPipelineAsset()
        {
#if UNITY_6000_0_OR_NEWER
            return GraphicsSettings.defaultRenderPipeline as HDRenderPipelineAsset;
#else
            return GraphicsSettings.renderPipelineAsset as HDRenderPipelineAsset;
#endif
        }

        // ------------------------------------------------------------------------------------------------------------

        private static HDRenderPipelineAsset ResolvePipelineAsset(StringBuilder report, out bool ownAsset)
        {
            HDRenderPipelineAsset current = GetDefaultPipelineAsset();
            var own = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelineAssetPath);
            if (current != null)
            {
                ownAsset = current == own;
                report.AppendLine($"  - using the project's HDRP asset '{AssetDatabase.GetAssetPath(current)}'");
                return current;
            }

            ownAsset = true;
            if (own != null) return own;

            HdrpEditorAssetUtility.EnsureFolder(GeneratedRenderingFolder);
            own = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            own.name = "DU_HDRenderPipeline";
            AssetDatabase.CreateAsset(own, PipelineAssetPath);
            report.AppendLine($"  - created '{PipelineAssetPath}'");
            return own;
        }

        /// <summary>
        /// Switches on the HDRP features the game's look relies on. <paramref name="ownAsset"/> additionally selects Light
        /// Probe Groups as the probe system (a user's own asset keeps its choice; a warning explains the consequence).
        /// </summary>
        public static void EnableRealisticFeatures(HDRenderPipelineAsset asset, bool ownAsset, StringBuilder report = null)
        {
            if (asset == null) return;

            RenderPipelineSettings settings = asset.currentPlatformRenderPipelineSettings;
            settings.supportSubsurfaceScattering = true;   // skin
            settings.supportSSR = true;                    // lacquered court, ball, skin sheen
            settings.supportSSAO = true;
            settings.supportVolumetrics = true;            // floodlight shafts through the haze
            settings.supportMotionVectors = true;          // TAA, motion blur-free reprojection
            settings.supportDecals = true;                 // scuffs, court logos
            settings.supportLightLayers = true;
            settings.supportShadowMask = true;
            settings.supportDistortion = true;             // heat haze / ability refraction
            settings.supportTransparentBackface = true;
            settings.supportTransparentDepthPrepass = true;
            settings.supportTransparentDepthPostpass = true;
            settings.supportCustomPass = true;

            if (ownAsset)
            {
                settings.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.LegacyLightProbes;
            }
            else if (settings.lightProbeSystem != RenderPipelineSettings.LightProbeSystem.LegacyLightProbes)
            {
                Debug.LogWarning($"[DU HDRP] '{asset.name}' uses Adaptive Probe Volumes: the arena's Light Probe Group is ignored. " +
                                 "Switch 'Light Probe System' to 'Light Probe Groups' or add and bake an Adaptive Probe Volume.");
            }

            HDShadowInitParameters shadows = settings.hdShadowInitParams;
            shadows.punctualLightShadowAtlas.shadowAtlasResolution = Mathf.Max(shadows.punctualLightShadowAtlas.shadowAtlasResolution, ShadowAtlasResolution);
            shadows.maxPunctualShadowMapResolution = Mathf.Max(shadows.maxPunctualShadowMapResolution, MaxShadowMapResolution);
            shadows.maxDirectionalShadowMapResolution = Mathf.Max(shadows.maxDirectionalShadowMapResolution, MaxShadowMapResolution);
            shadows.punctualShadowFilteringQuality = HDShadowFilteringQuality.High;    // PCSS: contact-hardening penumbrae
            shadows.directionalShadowFilteringQuality = HDShadowFilteringQuality.High;
            shadows.maxShadowRequests = Mathf.Max(shadows.maxShadowRequests, 128);
            settings.hdShadowInitParams = shadows;

            asset.currentPlatformRenderPipelineSettings = settings; // setter re-validates the pipeline
            EditorUtility.SetDirty(asset);
            report?.AppendLine($"  - realistic features enabled on '{asset.name}' (SSS, SSR, SSAO, volumetrics, motion vectors, decals, light layers, 4k shadow atlas)");
        }

        private static void AssignDefaultPipeline(HDRenderPipelineAsset asset, StringBuilder report)
        {
            if (GetDefaultPipelineAsset() == asset) return;
#if UNITY_6000_0_OR_NEWER
            GraphicsSettings.defaultRenderPipeline = asset;
#else
            GraphicsSettings.renderPipelineAsset = asset;
#endif
            report.AppendLine($"  - default render pipeline = '{asset.name}'");
        }

        private static void AssignQualityLevels(HDRenderPipelineAsset asset, StringBuilder report)
        {
            // QualitySettings.renderPipeline addresses the *current* level, so visit each level without applying the
            // expensive changes, then restore the user's level.
            int previousLevel = QualitySettings.GetQualityLevel();
            string[] levels = QualitySettings.names;
            try
            {
                for (int i = 0; i < levels.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    if (QualitySettings.renderPipeline is HDRenderPipelineAsset levelAsset)
                    {
                        if (levelAsset != asset) EnableRealisticFeatures(levelAsset, false);
                        continue;
                    }

                    QualitySettings.renderPipeline = asset;
                    report.AppendLine($"  - quality level '{levels[i]}' -> '{asset.name}'");
                }
            }
            finally
            {
                QualitySettings.SetQualityLevel(previousLevel, false);
            }
        }

        private static void EnsureSettingsAsset(StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<HdrpRenderingSettings>(SettingsAssetPath) != null) return;

            HdrpEditorAssetUtility.EnsureFolderForAsset(SettingsAssetPath);
            var settings = ScriptableObject.CreateInstance<HdrpRenderingSettings>();
            settings.name = "HdrpRenderingSettings";
            AssetDatabase.CreateAsset(settings, SettingsAssetPath);
            report.AppendLine($"  - created runtime tuning '{SettingsAssetPath}'");
        }

        /// <summary>
        /// HDRP 17 keeps <c>HDRenderPipelineGlobalSettings</c> and its <c>Ensure</c> method internal. The engine calls
        /// <c>HDRenderPipelineAsset.EnsureGlobalSettings()</c> (which runs <c>HDRenderPipelineGlobalSettings.Ensure()</c>)
        /// whenever the pipeline is (re)created, i.e. right after the asset is assigned. To make the settings exist
        /// immediately (e.g. for a batch-mode build right after configuration) the internal method is invoked via
        /// reflection when available; otherwise the user is pointed to the HDRP Wizard.
        /// </summary>
        public static void EnsureGlobalSettings(StringBuilder report = null)
        {
#if UNITY_6000_0_OR_NEWER
            if (GraphicsSettings.GetSettingsForRenderPipeline<HDRenderPipeline>() != null) return;
#endif
            const string wizardHint = "Open Window > Rendering > HDRP Wizard and press 'Fix All' to create the HDRP Global Settings.";
            try
            {
                Type globalSettingsType = typeof(HDRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineGlobalSettings");
                MethodInfo ensure = globalSettingsType?.GetMethod("Ensure", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(bool) }, null);
                if (ensure == null)
                {
                    Debug.LogWarning("[DU HDRP] HDRenderPipelineGlobalSettings.Ensure not found. " + wizardHint);
                    return;
                }

                object result = ensure.Invoke(null, new object[] { true });
                if (result == null) Debug.LogWarning("[DU HDRP] HDRP Global Settings could not be created. " + wizardHint);
                else report?.AppendLine("  - HDRP Global Settings ensured");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[DU HDRP] Ensuring HDRP Global Settings failed ({exception.GetBaseException().Message}). {wizardHint}");
            }
        }
    }
}
