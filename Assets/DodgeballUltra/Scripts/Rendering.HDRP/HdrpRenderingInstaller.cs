using DodgeballUltra.Juice;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Installs the HDRP adapters before the first scene loads: <see cref="RuntimeRenderingHooks.Active"/> =
    /// <see cref="HdrpRuntimeRenderingHooks"/> and <see cref="ScreenFx.Driver"/> = <see cref="HdrpScreenFxDriver"/>.
    /// Only acts when <c>GraphicsSettings.currentRenderPipeline</c> is an <see cref="HDRenderPipelineAsset"/>, so a project
    /// that has the HDRP package installed but runs another pipeline keeps the runtime's built-in fallbacks.
    /// <para>Order: the kernel facades reset their statics at SubsystemRegistration; this runs at BeforeSceneLoad, i.e.
    /// before any Awake of the first scene (GameBootstrap, arena builder, camera rig).</para>
    /// </summary>
    public static class HdrpRenderingInstaller
    {
        /// <summary>Settings used by the installed adapters (null when HDRP is not active).</summary>
        public static HdrpRenderingSettings Settings { get; private set; }

        /// <summary>The installed runtime hooks (null when HDRP is not active).</summary>
        public static HdrpRuntimeRenderingHooks Hooks { get; private set; }

        /// <summary>The installed screen-FX driver (null when HDRP is not active or screen FX are disabled).</summary>
        public static HdrpScreenFxDriver ScreenFxDriver { get; private set; }

        /// <summary>True when the active render pipeline (quality level override or default) is HDRP.</summary>
        public static bool IsHdrpActive => GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Settings = null;
            Hooks = null;
            ScreenFxDriver = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallOnLoad() => Install();

        /// <summary>
        /// Installs (or re-installs) the adapters. Safe to call again, e.g. after switching quality levels at runtime.
        /// Returns false when HDRP is not the active pipeline.
        /// </summary>
        public static bool Install()
        {
            if (!IsHdrpActive) return false;

            if (Settings == null) Settings = HdrpRenderingSettings.LoadOrCreateDefault();

            if (Hooks == null) Hooks = new HdrpRuntimeRenderingHooks(Settings);
            RuntimeRenderingHooks.Active = Hooks;

            if (Settings.enableScreenFx)
            {
                if (ScreenFxDriver == null) ScreenFxDriver = HdrpScreenFxDriver.Create(Settings);
                ScreenFx.Driver = ScreenFxDriver;
            }
            return true;
        }
    }
}
