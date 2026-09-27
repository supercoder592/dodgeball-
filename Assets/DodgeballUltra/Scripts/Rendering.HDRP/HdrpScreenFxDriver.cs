using System.Collections.Generic;
using DodgeballUltra.Juice;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// HDRP implementation of <see cref="IScreenFxDriver"/>: one high-priority global <see cref="Volume"/> per
    /// <see cref="ScreenPulse"/> type, each holding only that pulse's overrides (see <see cref="HdrpScreenFxLook"/>).
    /// A pulse animates the Volume's <c>weight</c> - HDRP then interpolates from the scene's own grade towards the pulse
    /// look - so effects stack naturally and never overwrite the environment.
    /// <para>Timing is in <b>unscaled</b> time so pulses keep animating during hitstop (timeScale ~ 0) and slow motion.
    /// Envelope: smooth attack over <see cref="HdrpRenderingSettings.pulseAttackTime"/>, then a quadratic ease-out decay
    /// until the requested duration. A pulse arriving while one is running restarts from the current value (no pop).
    /// Sustained amounts (<see cref="SetSustained"/>) act as a floor under the pulse envelope.</para>
    /// <para>Created by <see cref="HdrpRenderingInstaller"/> on a DontDestroyOnLoad object; the component disables
    /// itself while every channel is idle, so it costs nothing between pulses.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class HdrpScreenFxDriver : MonoBehaviour, IScreenFxDriver
    {
        /// <summary>Number of <see cref="ScreenPulse"/> values (Hit .. DangerSense).</summary>
        public const int PulseCount = (int)ScreenPulse.DangerSense + 1;

        private sealed class Channel
        {
            public ScreenPulse Pulse;
            public Volume Volume;
            public VolumeProfile Profile;
            public float From;         // envelope value when the current pulse started (smooth restart)
            public float Peak;         // requested intensity (0..1) scaled by the global strength
            public float StartTime;    // unscaled time
            public float Duration;     // unscaled seconds
            public bool PulseActive;
            public float Sustained;    // held floor (0..1)
            public float AppliedWeight = -1f;
        }

        private static readonly HashSet<Volume> s_OwnedVolumes = new HashSet<Volume>();

        [SerializeField, Tooltip("Tuning (looks, attack time, priority). Assigned by the installer.")]
        private HdrpRenderingSettings settings;

        private readonly Channel[] m_Channels = new Channel[PulseCount];
        private bool m_Initialised;

        // Enter-play-mode without domain reload keeps statics alive: start every session with an empty registry.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_OwnedVolumes.Clear();

        /// <summary>True when <paramref name="volume"/> is one of the screen-FX volumes (used to ignore them when
        /// looking for a scene's environment volume).</summary>
        public static bool IsScreenFxVolume(Volume volume) => volume != null && s_OwnedVolumes.Contains(volume);

        /// <summary>Settings the driver was built from.</summary>
        public HdrpRenderingSettings Settings => settings;

        /// <summary>Creates the driver on a persistent (DontDestroyOnLoad) GameObject.</summary>
        public static HdrpScreenFxDriver Create(HdrpRenderingSettings settings)
        {
            var go = new GameObject("DU HDRP Screen FX");
            go.layer = 0; // Default: included in every HD camera's volume layer mask by default
            DontDestroyOnLoad(go);

            var driver = go.AddComponent<HdrpScreenFxDriver>();
            driver.Initialise(settings != null ? settings : HdrpRenderingSettings.LoadOrCreateDefault());
            return driver;
        }

        private void Initialise(HdrpRenderingSettings source)
        {
            if (m_Initialised) return;
            settings = source;
            m_Initialised = true;

            for (int i = 0; i < PulseCount; i++)
            {
                var pulse = (ScreenPulse)i;
                var channel = new Channel { Pulse = pulse };
                m_Channels[i] = channel;

                HdrpScreenFxLook look = settings.GetLook(pulse);
                if (look == null || !look.HasAnyEffect) continue; // pulse deliberately disabled in the settings

                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "DU ScreenFx " + pulse;
                profile.hideFlags = HideFlags.DontSave;
                HdrpVolumeUtility.BuildScreenFxLook(profile, look);

                var child = new GameObject("Pulse " + pulse);
                child.layer = gameObject.layer;
                child.transform.SetParent(transform, false);

                var volume = child.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = settings.screenFxPriority + i; // above every scene volume, stable order between pulses
                volume.weight = 0f;
                volume.sharedProfile = profile;

                channel.Volume = volume;
                channel.Profile = profile;
                channel.AppliedWeight = 0f;
                s_OwnedVolumes.Add(volume);
            }

            enabled = false; // nothing to animate until the first pulse
        }

        /// <inheritdoc />
        public void Pulse(ScreenPulse pulse, float intensity, float duration)
        {
            Channel channel = GetChannel(pulse);
            if (channel == null || channel.Volume == null) return;

            float strength = Mathf.Clamp01(intensity) * (settings != null ? settings.pulseStrength : 1f);
            if (strength <= 0f) return;

            float now = Time.unscaledTime;
            float current = EvaluateEnvelope(channel, now);

            channel.From = current;
            channel.Peak = Mathf.Max(strength, current);
            channel.StartTime = now;
            channel.Duration = Mathf.Max(0.01f, duration);
            channel.PulseActive = true;
            enabled = true;
        }

        /// <inheritdoc />
        public void SetSustained(ScreenPulse pulse, float amount)
        {
            Channel channel = GetChannel(pulse);
            if (channel == null || channel.Volume == null) return;

            float strength = Mathf.Clamp01(amount) * (settings != null ? settings.pulseStrength : 1f);
            if (Mathf.Approximately(channel.Sustained, strength)) return;
            channel.Sustained = strength;
            enabled = true;
        }

        /// <summary>Current weight (0..1) of a pulse channel, for debugging/UI.</summary>
        public float GetWeight(ScreenPulse pulse)
        {
            Channel channel = GetChannel(pulse);
            return channel != null && channel.Volume != null ? channel.Volume.weight : 0f;
        }

        /// <summary>Stops every pulse and sustained effect immediately (e.g. on returning to the menu).</summary>
        public void ClearAll()
        {
            for (int i = 0; i < m_Channels.Length; i++)
            {
                Channel channel = m_Channels[i];
                if (channel == null) continue;
                channel.PulseActive = false;
                channel.Sustained = 0f;
                ApplyWeight(channel, 0f);
            }
            enabled = false;
        }

        private Channel GetChannel(ScreenPulse pulse)
        {
            int index = (int)pulse;
            return index >= 0 && index < m_Channels.Length ? m_Channels[index] : null;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            bool busy = false;

            for (int i = 0; i < m_Channels.Length; i++)
            {
                Channel channel = m_Channels[i];
                if (channel == null || channel.Volume == null) continue;

                float weight = Mathf.Max(channel.Sustained, EvaluateEnvelope(channel, now));
                ApplyWeight(channel, weight);
                busy |= channel.PulseActive || channel.Sustained > 0f;
            }

            // Idle: every weight has just been written as 0 (or its sustained value is 0) - stop ticking.
            if (!busy) enabled = false;
        }

        private float EvaluateEnvelope(Channel channel, float now)
        {
            if (!channel.PulseActive) return 0f;

            float elapsed = now - channel.StartTime;
            if (elapsed >= channel.Duration || elapsed < 0f)
            {
                channel.PulseActive = false;
                return 0f;
            }

            float attackLimit = settings != null ? settings.pulseAttackTime : 0.025f;
            float attack = Mathf.Min(attackLimit, channel.Duration * 0.25f);
            if (elapsed < attack)
            {
                float a = elapsed / attack;
                a = a * a * (3f - 2f * a); // smoothstep rise
                return Mathf.Lerp(channel.From, channel.Peak, a);
            }

            float k = (elapsed - attack) / Mathf.Max(1e-4f, channel.Duration - attack);
            float remaining = 1f - Mathf.Clamp01(k);
            return channel.Peak * remaining * remaining; // ease-out decay: fast drop, gentle tail
        }

        private static void ApplyWeight(Channel channel, float weight)
        {
            if (Mathf.Abs(weight - channel.AppliedWeight) < 1e-4f) return;
            channel.AppliedWeight = weight;
            if (channel.Volume != null) channel.Volume.weight = weight;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(ScreenFx.Driver, this)) ScreenFx.Driver = null;

            for (int i = 0; i < m_Channels.Length; i++)
            {
                Channel channel = m_Channels[i];
                if (channel == null) continue;
                if (channel.Volume != null) s_OwnedVolumes.Remove(channel.Volume);
                HdrpVolumeUtility.DestroyProfile(channel.Profile);
                channel.Profile = null;
                channel.Volume = null;
            }
            s_OwnedVolumes.RemoveWhere(v => v == null);
        }
    }
}
