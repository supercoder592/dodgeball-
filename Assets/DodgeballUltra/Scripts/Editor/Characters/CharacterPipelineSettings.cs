using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Tuning of the realistic-human character pipeline (download, texture/material conversion, LODs, animator).
    /// Stored per project in <c>ProjectSettings/DodgeballUltraCharacterPipeline.asset</c> and edited in
    /// <b>Project Settings ▸ Dodgeball Ultra ▸ Character Pipeline</b>. Defaults are the values the game ships with.
    /// <para>
    /// Persistence mirrors <c>ScriptableSingleton</c> (serialized file outside Assets/, loaded on first access) but is
    /// implemented explicitly so the settings also work in batch mode and with the compile-check reference assemblies.
    /// </para>
    /// </summary>
    public sealed class CharacterPipelineSettings : ScriptableObject
    {
        /// <summary>Settings file, relative to the project folder.</summary>
        public const string SettingsFilePath = "ProjectSettings/DodgeballUltraCharacterPipeline.asset";

        private static CharacterPipelineSettings s_instance;

        /// <summary>The project's settings (loaded from <see cref="SettingsFilePath"/> or created with defaults).</summary>
        public static CharacterPipelineSettings Instance
        {
            get
            {
                if (s_instance == null) s_instance = LoadOrCreate();
                return s_instance;
            }
        }

        private static CharacterPipelineSettings LoadOrCreate()
        {
            CharacterPipelineSettings settings = null;
            if (System.IO.File.Exists(System.IO.Path.Combine(RocketboxAssetSet.ProjectRoot, SettingsFilePath)))
            {
                Object[] loaded = InternalEditorUtility.LoadSerializedFileAndForget(SettingsFilePath);
                if (loaded != null && loaded.Length > 0) settings = loaded[0] as CharacterPipelineSettings;
            }
            if (settings == null) settings = CreateInstance<CharacterPipelineSettings>();
            // Not an asset: never saved with a scene, never unloaded, but editable in the settings UI.
            settings.hideFlags = HideFlags.DontSave;
            return settings;
        }

        // ------------------------------------------------------------------------------------------------ download
        [Header("Download (Microsoft Rocketbox, pinned commit)")]
        [Tooltip("Parallel HTTP downloads. raw.githubusercontent.com serves 4 connections per client comfortably.")]
        [Range(1, 8)] public int maxConcurrentDownloads = 4;

        [Tooltip("Attempts per file before giving up (network errors, HTTP 5xx and 429 are retried; 404 is not).")]
        [Range(1, 8)] public int maxDownloadAttempts = 4;

        [Tooltip("First retry delay in seconds; doubles on each further attempt (exponential backoff with jitter).")]
        [Min(0.1f)] public float retryBaseDelaySeconds = 1.5f;

        [Tooltip("Per-request timeout in seconds (a 12 MB texture over a slow link needs a generous value).")]
        [Min(10)] public int requestTimeoutSeconds = 600;

        [Tooltip("Also download each avatar's portrait PNG (used as CharacterData.portrait in hero select).")]
        public bool downloadPortraits = true;

        // ------------------------------------------------------------------------------------------------ textures
        [Header("Texture import")]
        [Tooltip("Max import size of Rocketbox textures and generated maps (the source art is 2048 px).")]
        [Range(256, 4096)] public int maxTextureSize = 2048;

        [Tooltip("Anisotropic filtering for character textures (skin/cloth viewed at grazing angles).")]
        [Range(0, 16)] public int anisoLevel = 4;

        // --------------------------------------------------------------------------------------------- smoothness
        [Header("Smoothness from Rocketbox specular maps (HDRP mask map alpha)")]
        [Tooltip("Face/head skin: x = smoothness of the darkest specular value (dry skin), y = brightest (eyes, lips, " +
                 "oily T-zone). Measured human skin sits around 0.45-0.55.")]
        public Vector2 skinSmoothness = new Vector2(0.36f, 0.72f);

        [Tooltip("Body texture (clothing with exposed arms/legs): cotton/poly sportswear is rough, leather/rubber trims are glossier.")]
        public Vector2 bodySmoothness = new Vector2(0.18f, 0.60f);

        [Tooltip("Hair / eyelash cards.")]
        public Vector2 hairSmoothness = new Vector2(0.30f, 0.55f);

        [Tooltip("Helmets, equipment, hats and other props.")]
        public Vector2 genericSmoothness = new Vector2(0.20f, 0.78f);

        [Tooltip("Curve applied to the normalised specular value before remapping (< 1 lifts mid-tones).")]
        [Range(0.25f, 4f)] public float smoothnessGamma = 0.8f;

        [Tooltip("Specular percentile treated as 'darkest' (robust against black UV gutters).")]
        [Range(0f, 20f)] public float specularLowPercentile = 2f;

        [Tooltip("Specular percentile treated as 'brightest' (robust against a few hot pixels).")]
        [Range(80f, 100f)] public float specularHighPercentile = 99.5f;

        [Tooltip("Smoothness used for hair cards when the avatar ships no opacity specular map.")]
        [Range(0f, 1f)] public float hairFallbackSmoothness = 0.45f;

        // --------------------------------------------------------------------------------------------------- model
        [Header("Prefab / model")]
        [Tooltip("Remove weapon sub-meshes (combat knives, pistols...) from military/police outfits: dodgeball players " +
                 "carry no weapons. Matched against the material slot name.")]
        public bool stripWeaponSubmeshes = true;

        [Tooltip("Material-slot name fragments treated as weapons when stripping is enabled.")]
        public string[] weaponMaterialTokens = { "knife", "pistol", "gun", "rifle", "holster" };

        [Tooltip("Screen height (fraction) below which LOD0 (hipoly) switches to LOD1 (midpoly), when present.")]
        [Range(0.05f, 1f)] public float lod0ScreenHeight = 0.6f;

        [Tooltip("Screen height below which LOD1 (midpoly) switches to LOD2 (lowpoly), when present.")]
        [Range(0.02f, 1f)] public float lod1ScreenHeight = 0.3f;

        [Tooltip("Screen height below which LOD2 (lowpoly) switches to LOD3 (ultra-low), when present.")]
        [Range(0.01f, 1f)] public float lod2ScreenHeight = 0.12f;

        [Tooltip("Screen height below which the last available LOD is culled.")]
        [Range(0.001f, 0.2f)] public float cullScreenHeight = 0.02f;

        [Tooltip("Animator culling. AlwaysAnimate keeps hand sockets (held balls) and IK correct even when a player is " +
                 "off-screen; only six characters are ever animated.")]
        public AnimatorCullingMode animatorCulling = AnimatorCullingMode.AlwaysAnimate;

        [Tooltip("Per-object motion vectors for skinned meshes (sharper TAA / motion blur on fast throws).")]
        public bool skinnedMotionVectors = true;

        // ------------------------------------------------------------------------------------------------ animator
        [Header("Animator controllers")]
        [Tooltip("Blend into Airborne when leaving the ground (s).")]
        [Range(0f, 1f)] public float airborneTransition = 0.12f;

        [Tooltip("Blend back to locomotion on landing (s).")]
        [Range(0f, 1f)] public float landingTransition = 0.1f;

        [Tooltip("Blend into / out of the crouch (slide, catch stance) (s).")]
        [Range(0f, 1f)] public float crouchTransition = 0.15f;

        [Tooltip("Blend into / out of the dizzy stunned idle (s).")]
        [Range(0f, 1f)] public float stunnedTransition = 0.2f;

        [Tooltip("Blend into Cheer / Wave (s).")]
        [Range(0f, 1f)] public float cheerTransition = 0.25f;

        [Tooltip("Blend into Defeat (s).")]
        [Range(0f, 1f)] public float defeatTransition = 0.35f;

        [Tooltip("Blend of authored upper-body Throw / Catch clips (s).")]
        [Range(0f, 0.5f)] public float upperBodyTransition = 0.08f;

        [Tooltip("Playback speed of the tucked-knees crouch pose used while airborne (slow = held tuck).")]
        [Range(0.1f, 2f)] public float airborneClipSpeed = 0.6f;

        [Tooltip("Scale each locomotion clip so its measured root speed matches its blend threshold (no foot sliding).")]
        public bool matchLocomotionSpeed = true;

        [Tooltip("Clamp for the locomotion time-scale correction (x = min, y = max).")]
        public Vector2 locomotionTimeScaleRange = new Vector2(0.6f, 1.6f);

        [Tooltip("Planar speed (m/s) above which Cheer / Wave / Defeat are left for locomotion (player moves again).")]
        [Min(0.1f)] public float leaveGestureSpeed = 1f;

        /// <summary>Persists the settings to ProjectSettings/.</summary>
        public void SaveSettings()
            => InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { this }, SettingsFilePath, true);

        /// <summary>Restores every value to its shipped default and saves.</summary>
        public void ResetToDefaults()
        {
            var defaults = CreateInstance<CharacterPipelineSettings>();
            try
            {
                EditorUtility.CopySerialized(defaults, this);
            }
            finally
            {
                DestroyImmediate(defaults);
            }
            SaveSettings();
        }

        /// <summary>Import max size rounded to a power of two.</summary>
        public int TextureSizePowerOfTwo => Mathf.Clamp(Mathf.ClosestPowerOfTwo(maxTextureSize), 256, 4096);

        private void OnValidate()
        {
            skinSmoothness = ClampRange(skinSmoothness);
            bodySmoothness = ClampRange(bodySmoothness);
            hairSmoothness = ClampRange(hairSmoothness);
            genericSmoothness = ClampRange(genericSmoothness);
            if (specularHighPercentile <= specularLowPercentile) specularHighPercentile = Mathf.Min(100f, specularLowPercentile + 1f);
            lod1ScreenHeight = Mathf.Min(lod1ScreenHeight, lod0ScreenHeight - 0.001f);
            lod2ScreenHeight = Mathf.Min(lod2ScreenHeight, lod1ScreenHeight - 0.001f);
            cullScreenHeight = Mathf.Min(cullScreenHeight, lod2ScreenHeight - 0.001f);
            locomotionTimeScaleRange.x = Mathf.Clamp(locomotionTimeScaleRange.x, 0.1f, 1f);
            locomotionTimeScaleRange.y = Mathf.Clamp(locomotionTimeScaleRange.y, 1f, 4f);
        }

        private static Vector2 ClampRange(Vector2 v)
        {
            v.x = Mathf.Clamp01(v.x);
            v.y = Mathf.Clamp(v.y, v.x, 1f);
            return v;
        }
    }
}
