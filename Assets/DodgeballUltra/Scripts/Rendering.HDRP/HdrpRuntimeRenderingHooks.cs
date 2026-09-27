using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// HDRP 17 implementation of <see cref="IRuntimeRenderingHooks"/>, installed as <see cref="RuntimeRenderingHooks.Active"/>
    /// by <see cref="HdrpRenderingInstaller"/> when the active render pipeline is HDRP. Lets the pipeline-agnostic runtime
    /// (arena builder, camera rig, material factory) get physically based lights, properly configured HD cameras,
    /// a realistic environment volume and valid HDRP material keywords without referencing the SRP packages.
    /// </summary>
    public sealed class HdrpRuntimeRenderingHooks : IRuntimeRenderingHooks
    {
        private readonly HdrpRenderingSettings m_Settings;

        /// <summary>Creates the hooks with <paramref name="settings"/> (project defaults when null).</summary>
        public HdrpRuntimeRenderingHooks(HdrpRenderingSettings settings)
        {
            m_Settings = settings != null ? settings : HdrpRenderingSettings.LoadOrCreateDefault();
        }

        /// <summary>Tuning in use.</summary>
        public HdrpRenderingSettings Settings => m_Settings;

        /// <inheritdoc />
        /// <remarks>Directional lights take lux, spot/point lights lumen (reflector spots). Set the light type and spot
        /// angle before calling. See <see cref="HdrpLightUtility.ConfigurePhysicalLight"/>.</remarks>
        public void ConfigureLight(Light light, float physicalIntensity, float temperatureKelvin)
        {
            HdrpLightUtility.ConfigurePhysicalLight(light, physicalIntensity, temperatureKelvin);
        }

        /// <inheritdoc />
        public void ConfigureCamera(Camera camera)
        {
            HdrpCameraUtility.Configure(camera, m_Settings);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Does nothing when the scene already contains an enabled global Volume (other than the screen-FX volumes) or
        /// when <see cref="HdrpRenderingSettings.createEnvironmentVolume"/> is off. The created profile is owned by a
        /// <see cref="HdrpRuntimeProfileOwner"/> and destroyed with the scene.
        /// </remarks>
        public void EnsureEnvironmentVolume(Transform parent)
        {
            if (!m_Settings.createEnvironmentVolume || SceneHasGlobalVolume()) return;

            bool physicalSky = UsePhysicalSky();

            var go = new GameObject("DU Environment Volume (HDRP)");
            go.layer = 0; // Default layer: part of every HD camera's volume mask
            if (parent != null) go.transform.SetParent(parent, false);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "DU Runtime Arena Environment";
            profile.hideFlags = HideFlags.DontSave;
            HdrpVolumeUtility.BuildRuntimeEnvironment(profile, m_Settings, physicalSky);

            // Own the profile before the Volume references it so it is cleaned up even if the object is destroyed early.
            go.AddComponent<HdrpRuntimeProfileOwner>().Own(profile);

            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = m_Settings.environmentVolumePriority;
            volume.weight = 1f;
            volume.sharedProfile = profile;
        }

        /// <inheritdoc />
        public void ValidateMaterial(Material material)
        {
            if (material == null || material.shader == null) return;
            // Sets keywords, passes, render queue and stencil state for HDRP/Lit, Unlit, LayeredLit and HDRP Shader
            // Graphs; returns false (harmlessly) for non-HDRP shaders such as legacy particle shaders.
            HDMaterial.ValidateMaterial(material);
        }

        // ------------------------------------------------------------------------------------------------------------

        private static bool SceneHasGlobalVolume()
        {
            // One-off query (arena build time), so the allocation of FindObjectsByType is acceptable here.
            Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++)
            {
                Volume volume = volumes[i];
                if (volume == null || !volume.isActiveAndEnabled || !volume.isGlobal) continue;
                if (HdrpScreenFxDriver.IsScreenFxVolume(volume)) continue;
                if (volume.sharedProfile == null && !volume.HasInstantiatedProfile()) continue;
                return true;
            }
            return false;
        }

        private bool UsePhysicalSky()
        {
            switch (m_Settings.skyModel)
            {
                case HdrpRenderingSettings.RuntimeSkyModel.PhysicallyBased: return true;
                case HdrpRenderingSettings.RuntimeSkyModel.Gradient: return false;
            }

            // Auto: the Physically Based Sky is lit by the directional light; without a sufficiently bright one it would
            // render black and leave the hall without ambient light, so fall back to the calibrated gradient.
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null || !light.isActiveAndEnabled || light.type != LightType.Directional) continue;
                // HDRP stores directional intensity natively in lux.
                if (light.intensity >= m_Settings.physicalSkyMinSunLux) return true;
            }
            return false;
        }
    }
}
