using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>HDRP gameplay-camera setup shared by the runtime hooks and the editor scene builder.</summary>
    public static class HdrpCameraUtility
    {
        /// <summary>
        /// Adds/gets <see cref="HDAdditionalCameraData"/> and applies anti-aliasing, dithering, NaN protection and the
        /// volume layer mask from <paramref name="settings"/> (defaults when null). Returns the camera data.
        /// </summary>
        public static HDAdditionalCameraData Configure(Camera camera, HdrpRenderingSettings settings)
        {
            if (camera == null) return null;
            if (settings == null) settings = HdrpRenderingSettings.LoadOrCreateDefault();

            if (!camera.TryGetComponent(out HDAdditionalCameraData data))
                data = camera.gameObject.AddComponent<HDAdditionalCameraData>();

            // HDRP renders in HDR internally and ignores MSAA on the camera (MSAA is a pipeline-asset setting).
            camera.allowHDR = true;
            camera.allowMSAA = false;

            data.antialiasing = settings.antialiasing;
            data.TAAQuality = settings.taaQuality;
            data.taaSharpenStrength = settings.taaSharpenStrength;
            data.taaMotionVectorRejection = settings.taaMotionVectorRejection;
            data.taaAntiFlicker = settings.taaAntiFlicker;
            data.SMAAQuality = settings.smaaQuality;
            data.dithering = settings.dithering;
            data.stopNaNs = settings.stopNaNs;

            // Add (never remove) the layers carrying the environment and screen-FX volumes.
            data.volumeLayerMask = data.volumeLayerMask.value | settings.volumeLayerMask.value;

            // The arena is enclosed; clearing with the sky keeps exposure-correct background pixels if a gap shows.
            data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            return data;
        }
    }
}
