using System;
using System.Reflection;
using UnityEngine;

namespace DodgeballUltra.Editor.Pipeline
{
    /// <summary>
    /// Access to UnityEngine members that exist only in the <em>Editor</em> build of the engine (e.g.
    /// <c>MeshRenderer.receiveGI</c>, the <c>LightProbeGroup.probePositions</c> setter, <c>LightingSettings.lightmapper</c>).
    /// They are stripped from the player reference assemblies used by the command-line compile check, so they are set by
    /// reflection on their documented public names. Every call is safe: a missing member is reported, never thrown.
    /// </summary>
    public static class EditorOnlyApi
    {
        private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

        /// <summary>
        /// Sets a public instance property. Enum properties accept either a value of the enum or its member name as a string
        /// (so enum types that are themselves editor-only can be addressed). Returns false if the property is missing,
        /// read-only or the value cannot be converted.
        /// </summary>
        public static bool TrySet(object target, string propertyName, object value)
        {
            if (target == null) return false;
            PropertyInfo property = target.GetType().GetProperty(propertyName, InstancePublic);
            if (property == null || !property.CanWrite) return false;

            try
            {
                Type type = property.PropertyType;
                object converted = value;
                if (type.IsEnum)
                {
                    converted = value is string name ? Enum.Parse(type, name) : Enum.ToObject(type, Convert.ToInt32(value));
                }
                else if (value != null && !type.IsInstanceOfType(value))
                {
                    converted = Convert.ChangeType(value, type);
                }
                property.SetValue(target, converted);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Could not set {target.GetType().Name}.{propertyName}: {e.Message}");
                return false;
            }
        }

        /// <summary>Reads a public instance property (false when missing).</summary>
        public static bool TryGet<T>(object target, string propertyName, out T value)
        {
            value = default;
            if (target == null) return false;
            PropertyInfo property = target.GetType().GetProperty(propertyName, InstancePublic);
            if (property == null || !property.CanRead) return false;
            try
            {
                object raw = property.GetValue(target);
                if (raw is T typed)
                {
                    value = typed;
                    return true;
                }
                if (raw != null && typeof(T) == typeof(int) && raw.GetType().IsEnum)
                {
                    value = (T)(object)Convert.ToInt32(raw);
                    return true;
                }
            }
            catch (Exception)
            {
                // fall through
            }
            return false;
        }

        /// <summary>Lightmaps (true) or light probes (false) as the GI source of a static renderer.</summary>
        public static bool SetReceiveGIFromLightmaps(MeshRenderer renderer, bool lightmaps)
            => TrySet(renderer, "receiveGI", lightmaps ? ReceiveGI.Lightmaps : ReceiveGI.LightProbes);

        /// <summary>Assigns the probe positions (local space) of a Light Probe Group.</summary>
        public static bool SetProbePositions(LightProbeGroup group, Vector3[] positions)
            => TrySet(group, "probePositions", positions);
    }
}
