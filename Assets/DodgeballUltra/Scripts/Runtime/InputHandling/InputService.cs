using System;
using UnityEngine;

namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// Owns the process-wide <see cref="IInputBackend"/> and caches one <see cref="InputFrame"/> per rendered frame, so the
    /// local player's <see cref="HumanInputSource"/>, the pause menu and the hero select screen all read identical values
    /// no matter in which order Unity updates them (the first reader of a frame triggers the poll).
    /// <para>
    /// Backend choice (runtime): the Input System package when it is compiled in (<c>DU_INPUT_SYSTEM &amp;&amp;
    /// ENABLE_INPUT_SYSTEM</c>), otherwise the legacy Input Manager (<c>ENABLE_LEGACY_INPUT_MANAGER</c>), otherwise a
    /// silent <see cref="NullInputBackend"/>. If constructing the preferred backend throws, the next one is used.
    /// </para>
    /// </summary>
    public static class InputService
    {
        private static IInputBackend s_backend;
        private static InputFrame s_frame;
        private static int s_polledFrame = -1;
        private static InputDeviceKind s_lastDevice = InputDeviceKind.KeyboardMouse;
        private static bool s_quitHooked;

        /// <summary>Raised when the most recently used device family changes (key prompts switch between KBM and pad).</summary>
        public static event Action<InputDeviceKind> DeviceChanged;

        /// <summary>The active backend (created on first use).</summary>
        public static IInputBackend Backend
        {
            get
            {
                EnsureBackend();
                return s_backend;
            }
        }

        /// <summary>This frame's input snapshot (polled lazily, at most once per frame).</summary>
        public static InputFrame Current
        {
            get
            {
                Poll();
                return s_frame;
            }
        }

        /// <summary>Most recently used device family.</summary>
        public static InputDeviceKind LastDevice
        {
            get
            {
                Poll();
                return s_lastDevice;
            }
        }

        /// <summary>True when on-screen prompts should show gamepad glyphs.</summary>
        public static bool UsingGamepad => LastDevice == InputDeviceKind.Gamepad;

        /// <summary>Replaces the backend (tests, custom devices, rebinding UIs). The previous backend is disposed.</summary>
        public static void SetBackend(IInputBackend backend)
        {
            if (ReferenceEquals(backend, s_backend)) return;
            DisposeBackend();
            s_backend = backend;
            s_backend?.Enable();
            s_polledFrame = -1;
            HookQuit();
        }

        /// <summary>Forces a re-read on the next access (e.g. after the backend was re-enabled mid-frame).</summary>
        public static void Invalidate() => s_polledFrame = -1;

        /// <summary>Reads the backend if this frame has not been polled yet.</summary>
        public static void Poll()
        {
            int frame = Time.frameCount;
            if (frame == s_polledFrame) return;
            s_polledFrame = frame;
            EnsureBackend();

            var snapshot = default(InputFrame);
            try
            {
                s_backend.Read(ref snapshot);
            }
            catch (Exception e)
            {
                // A broken device/backend must never take gameplay down: log once and fall back to silence.
                Debug.LogException(e);
                SetBackend(new NullInputBackend());
                snapshot = default;
                s_polledFrame = frame;
            }

            snapshot.FrameCount = frame;
            // Keyboard/mouse wins ties so a resting controller never steals the prompts from an active mouse user.
            var previous = s_lastDevice;
            if (snapshot.KeyboardMouseActive) s_lastDevice = InputDeviceKind.KeyboardMouse;
            else if (snapshot.GamepadActive) s_lastDevice = InputDeviceKind.Gamepad;
            snapshot.LastDevice = s_lastDevice;
            s_frame = snapshot;

            if (previous != s_lastDevice) DeviceChanged?.Invoke(s_lastDevice);
        }

        private static void EnsureBackend()
        {
            if (s_backend != null) return;
            s_backend = CreateDefaultBackend();
            HookQuit();
        }

        /// <summary>Creates the preferred backend for this build (see class docs).</summary>
        public static IInputBackend CreateDefaultBackend()
        {
#if DU_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            try
            {
                return new InputSystemBackend();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Input System backend unavailable ({e.Message}); falling back.");
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try
            {
                return new LegacyInputBackend();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Legacy input backend unavailable ({e.Message}); input disabled.");
            }
#endif
            return new NullInputBackend();
        }

        private static void HookQuit()
        {
            if (s_quitHooked) return;
            s_quitHooked = true;
            Application.quitting += DisposeBackend;
        }

        private static void DisposeBackend()
        {
            var old = s_backend;
            s_backend = null;
            s_polledFrame = -1;
            if (old == null) return;
            try
            {
                old.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Input backend dispose failed: {e.Message}");
            }
        }

        /// <summary>Supports "Enter Play Mode Options" without domain reload: release the previous session's actions.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            DisposeBackend();
            if (s_quitHooked) Application.quitting -= DisposeBackend;
            s_quitHooked = false;
            s_frame = default;
            s_lastDevice = InputDeviceKind.KeyboardMouse;
            DeviceChanged = null;
        }
    }
}
