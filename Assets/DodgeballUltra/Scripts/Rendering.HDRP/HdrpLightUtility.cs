using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Physically based light setup for HDRP 17 (shared by the runtime hooks and the editor lighting builder).
    /// <para>HDRP 17 (Unity 6) moved light units to the engine: <c>Light.lightUnit</c> is the unit shown in the inspector
    /// and <c>Light.intensity</c> is always stored in the type's <i>native</i> unit (candela for spot/point, lux for
    /// directional, nits for area lights); <c>LightUnitUtils.ConvertIntensity</c> converts between them using the light's
    /// shape (spot angle, reflector, area size). The old <c>HDAdditionalLightData.SetIntensity/lightUnit</c> API is
    /// obsolete in 17.0.</para>
    /// </summary>
    public static class HdrpLightUtility
    {
        /// <summary>Apparent angular diameter of the sun (degrees) - gives physically sized soft shadow penumbrae.</summary>
        public const float SunAngularDiameter = 0.53f;

        /// <summary>Lowest / highest colour temperature accepted (K); the engine's CCT approximation covers this range.</summary>
        public const float MinTemperature = 1000f, MaxTemperature = 20000f;

        /// <summary>
        /// Returns the light's <see cref="HDAdditionalLightData"/>, adding and initialising it with HDRP's defaults when
        /// missing (lights created from script, e.g. <c>AddComponent&lt;Light&gt;()</c>, do not get it automatically).
        /// </summary>
        public static HDAdditionalLightData GetOrAddLightData(Light light)
        {
            if (light == null) return null;
            if (light.TryGetComponent(out HDAdditionalLightData data)) return data;

            data = light.gameObject.AddComponent<HDAdditionalLightData>();
            HDAdditionalLightData.InitDefaultHDAdditionalLightData(data);
            return data;
        }

        /// <summary>
        /// Configures <paramref name="light"/> with a physical intensity and colour temperature.
        /// Set the light's type, spot angle and area size <i>before</i> calling: the lumen to candela/nits conversion
        /// depends on them. Spot lights are treated as reflector luminaires (all lumens go into the cone), which is how
        /// sports floodlights are specified.
        /// </summary>
        /// <param name="light">Light to configure.</param>
        /// <param name="physicalIntensity">Lux for directional (and box) lights, lumen for spot/point/area lights.</param>
        /// <param name="temperatureKelvin">Correlated colour temperature in kelvin; 0 or less disables temperature mode
        /// (the light colour is then used as-is).</param>
        public static void ConfigurePhysicalLight(Light light, float physicalIntensity, float temperatureKelvin)
        {
            if (light == null) return;

            HDAdditionalLightData data = GetOrAddLightData(light);
            physicalIntensity = Mathf.Max(0f, physicalIntensity);

            // Colour temperature: HDRP multiplies Light.color (acting as a filter) by the black-body colour. The existing
            // colour is kept so a deliberately tinted light keeps its gel.
            if (temperatureKelvin > 0f)
            {
                light.useColorTemperature = true;
                light.colorTemperature = Mathf.Clamp(temperatureKelvin, MinTemperature, MaxTemperature);
            }
            else
            {
                light.useColorTemperature = false;
            }

            ApplyPhysicalIntensity(light, physicalIntensity);

            if (data != null)
            {
                data.affectsVolumetric = true;
                if (light.type == LightType.Directional)
                    data.angularDiameter = SunAngularDiameter;
            }
        }

        /// <summary>Writes <paramref name="physicalIntensity"/> (lux or lumen, see <see cref="ConfigurePhysicalLight"/>).</summary>
        public static void ApplyPhysicalIntensity(Light light, float physicalIntensity)
        {
            if (light == null) return;
#if UNITY_6000_0_OR_NEWER
            // HDRP 17 path (verified against com.unity.render-pipelines.core 17.0.4 LightUnitUtils and
            // HDAdditionalLightData.InitDefaultHDAdditionalLightData, which uses exactly this sequence).
            LightUnit unit = GetPhysicalUnit(light.type);
            if (light.type == LightType.Spot || light.type == LightType.Pyramid)
                light.enableSpotReflector = true;
            light.lightUnit = unit;
            light.intensity = LightUnitUtils.ConvertIntensity(light, physicalIntensity, unit, LightUnitUtils.GetNativeLightUnit(light.type));
#else
            // HDRP 17 requires Unity 6000.0; this branch only exists for the 2021.3 reference compile. It writes the same
            // native units by hand (candela = lumen / cone solid angle with reflector; lux unchanged).
            light.intensity = ToNativeIntensity(light, physicalIntensity);
#endif
        }

#if UNITY_6000_0_OR_NEWER
        /// <summary>Unit the game specifies intensities in: lux for directional/box lights, lumen for everything else.</summary>
        private static LightUnit GetPhysicalUnit(LightType type)
        {
            switch (type)
            {
                case LightType.Directional:
                case LightType.Box:
                    return LightUnit.Lux;
                default:
                    return LightUnit.Lumen;
            }
        }
#else
        /// <summary>Lux/lumen to HDRP's native intensity unit for the light types that exist in every Unity version.</summary>
        private static float ToNativeIntensity(Light light, float physicalIntensity)
        {
            switch (light.type)
            {
                case LightType.Point:
                    return LightUnitUtils.LumenToCandela(physicalIntensity, LightUnitUtils.GetSolidAngleFromPointLight());
                case LightType.Spot:
                    return LightUnitUtils.LumenToCandela(physicalIntensity, LightUnitUtils.GetSolidAngleFromSpotLight(light.spotAngle));
                default:
                    // Directional: lux is native. Area lights are not used by the game at runtime.
                    return physicalIntensity;
            }
        }
#endif

        /// <summary>
        /// Enables soft shadows with a given HDRP shadow-resolution tier (0..3 = Low..Ultra of the pipeline asset) and
        /// optional contact shadows. Used for the few "hero" lights allowed to cast shadows.
        /// </summary>
        public static void EnableShadows(Light light, int resolutionLevel, bool contactShadows)
        {
            HDAdditionalLightData data = GetOrAddLightData(light);
            if (data == null) return;

            data.EnableShadows(true);
            data.SetShadowResolutionOverride(false);
            data.SetShadowResolutionLevel(Mathf.Clamp(resolutionLevel, 0, 3));

            BoolScalableSettingValue contact = data.useContactShadow;
            if (contact != null)
            {
                contact.useOverride = true;
                contact.@override = contactShadows;
            }
        }
    }
}
