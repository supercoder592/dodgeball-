using System;
using System.Collections.Generic;
using System.Threading;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.Audio;

namespace DodgeballUltra.Audio
{
    public enum SfxId
    {
        Throw = 0,
        ThrowHeavy,
        BallHitPlayer,
        BallHitHeavy,
        BallBounceFloor,
        BallBounceWall,
        Catch,
        PerfectCatch,
        CatchWhiff,
        Pickup,
        Pass,
        Footstep,
        Jump,
        Land,
        Slide,
        Whistle,
        Countdown,
        RoundStart,
        Elimination,
        Revive,
        AbilityCast,
        UltimateCast,
        UltimateReady,
        Shockwave,
        Beam,
        Freeze,
        Teleport,
        Glue,
        Turret,
        Shield,
        Magnet,
        Stasis,
        Rewind,
        Cloak,
        Clone,
        Earthquake,
        CrowdCheer,
        CrowdGasp,
        CrowdAmbience,
        UiClick,
        UiConfirm,
    }

    /// <summary>
    /// CONTRACT (kernel) - 3D/2D sound playback with pooled AudioSources. Reacts to game events on its own (hits, catches,
    /// throws, bounces, round flow). Uses clips from an AudioLibrary asset when present, otherwise synthesises realistic-ish
    /// placeholder clips at startup (thumps, whooshes, slaps, whistle, crowd noise) so the game is never silent.
    /// <para>
    /// Clips: an <see cref="AudioLibrary"/> entry wins; every other id uses <see cref="ProceduralSfx"/>. The placeholders
    /// are synthesised on a background thread at start-up in the order the first seconds of a match need them (floor
    /// bounces, thumps, catches, throws, footsteps, whistle...) and turned into AudioClips a few per frame; a sound
    /// requested before its turn is synthesised on the spot. WebGL (no threads) synthesises one clip per frame instead.
    /// </para>
    /// <para>
    /// Voices: a fixed pool of AudioSources (no per-play allocation). Each id has a mix profile (<see cref="SfxProfiles"/>):
    /// gain, volume/pitch jitter (no machine-gun repeats), a minimum interval (several systems reporting the same fact
    /// play it once), a voice cap per id and a priority used for voice stealing. 3D voices use a logarithmic roll-off
    /// (1/r like a real point source) between <see cref="minDistance"/> and <see cref="maxDistance"/>.
    /// </para>
    /// <para>
    /// Buses: Sfx / Crowd / Announcer / Ui volumes (optional AudioMixerGroups) under a master volume. Hitstop ducks every
    /// non-exempt voice for the freeze frame (the impact itself cuts through). Crowd ambience is a seamless loop whose level
    /// follows the match phase plus a decaying excitement pushed by eliminations, perfect catches and round ends.
    /// Footsteps (cadence from ground speed) and sneaker squeaks (hard cuts and braking) are polled from the players,
    /// skipping Gale's Silent Footsteps. Timing uses unscaled time; footstep cadence follows scaled movement.
    /// </para>
    /// <para>Owner module: Audio.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        // ------------------------------------------------------------------ tuning

        [Header("Clips")]
        [Tooltip("Recorded clips (optional). Ids without an entry use the procedural placeholders.")]
        public AudioLibrary library;

        [Tooltip("Resources path of a library loaded when none is assigned.")]
        public string libraryResourcePath = "DodgeballUltra/AudioLibrary";

        [Tooltip("Synthesise the placeholder clips on a worker thread at start-up (WebGL: incrementally on the main thread).")]
        public bool synthesizeInBackground = true;

        [Tooltip("Placeholder clips converted into AudioClips per frame while the start-up synthesis runs.")]
        [Range(1, 16)] public int clipsPerFrame = 4;

        [Header("Mixing")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float sfxVolume = 1f;
        [Range(0f, 1f)] public float crowdVolume = 0.75f;
        [Range(0f, 1f)] public float announcerVolume = 0.9f;
        [Range(0f, 1f)] public float uiVolume = 0.9f;
        public bool muted;
        public AudioMixerGroup sfxGroup;
        public AudioMixerGroup crowdGroup;
        public AudioMixerGroup announcerGroup;
        public AudioMixerGroup uiGroup;

        [Header("Voices")]
        [Tooltip("Pooled AudioSources.")]
        [Range(8, 64)] public int voiceCount = 32;
        [Tooltip("Distance (m) up to which a 3D sound plays at full volume.")]
        [Min(0.1f)] public float minDistance = 2f;
        [Tooltip("Distance (m) beyond which a 3D sound is inaudible (and not started at all).")]
        [Min(1f)] public float maxDistance = 60f;
        [Tooltip("Doppler (0 = off: the camera flies around fast, the pitch must not wobble).")]
        [Range(0f, 5f)] public float dopplerLevel = 0f;
        [Tooltip("Send to reverb zones placed in the arena.")]
        [Range(0f, 1.1f)] public float reverbZoneMix = 1f;

        [Header("Game events")]
        [Tooltip("Play the generic combat / match sounds from game events.")]
        public bool reactToGameEvents = true;
        [Tooltip("Throws at or above this speed (km/h) use the heavy whoosh.")]
        [Min(0f)] public float heavyThrowSpeedKmh = 115f;
        [Tooltip("Hits at or above this speed (km/h), or eliminating hits, use the heavy thump.")]
        [Min(0f)] public float heavyHitSpeedKmh = 110f;
        [Tooltip("Bounces slower than this (m/s) are silent.")]
        [Min(0f)] public float minBounceSpeed = 0.8f;
        [Tooltip("Bounce speed (m/s) of the loudest bounce.")]
        [Min(0.1f)] public float fullBounceSpeed = 12f;

        [Header("Footsteps")]
        public bool footsteps = true;
        [Tooltip("Planar speed (m/s) below which the feet are silent.")]
        [Min(0f)] public float minFootstepSpeed = 0.6f;
        [Tooltip("Steps per second = base + perSpeed x speed (clamped).")]
        [Min(0f)] public float cadenceBase = 1.7f;
        [Min(0f)] public float cadencePerSpeed = 0.32f;
        [Min(0.1f)] public float cadenceMin = 1.6f;
        [Min(0.1f)] public float cadenceMax = 4.3f;
        [Range(0f, 1f)] public float footstepVolume = 0.55f;
        [Tooltip("Sneaker squeaks need at least this speed (m/s).")]
        [Min(0f)] public float squeakMinSpeed = 3.5f;
        [Tooltip("Yaw rate (deg/s) of a hard cut that squeaks.")]
        [Min(0f)] public float squeakYawRate = 480f;
        [Tooltip("Braking deceleration (m/s^2) that squeaks.")]
        [Min(0f)] public float squeakBraking = 16f;
        [Range(0f, 1f)] public float squeakChance = 0.55f;
        [Min(0f)] public float squeakCooldown = 0.5f;

        [Header("Crowd")]
        public bool crowdAmbience = true;
        [Range(0f, 1f)] public float crowdIdle = 0.16f;
        [Range(0f, 1f)] public float crowdPreRound = 0.26f;
        [Range(0f, 1f)] public float crowdCountdown = 0.3f;
        [Range(0f, 1f)] public float crowdPlaying = 0.3f;
        [Range(0f, 1f)] public float crowdRoundEnd = 0.42f;
        [Range(0f, 1f)] public float crowdMatchEnd = 0.5f;
        [Tooltip("Crowd level change rate (per second).")]
        [Min(0.01f)] public float crowdFadeRate = 0.5f;
        [Tooltip("Maximum extra crowd level from excitement (big plays).")]
        [Range(0f, 1f)] public float excitementGain = 0.35f;
        [Tooltip("Time constant (s) of the excitement decay.")]
        [Min(0.05f)] public float excitementDecay = 2.5f;

        [Header("Hitstop ducking")]
        [Range(0f, 1f)] public float duckAmount = 0.45f;
        [Tooltip("Seconds (unscaled) to come back from the duck.")]
        [Min(0.01f)] public float duckRelease = 0.12f;
        [Tooltip("Longest duck (s), whatever the hitstop length.")]
        [Min(0f)] public float maxDuckHold = 0.25f;

        // ------------------------------------------------------------------ voices

        private sealed class Voice
        {
            public AudioSource Source;
            public Transform Transform;
            public bool Active;
            public int HandleId;
            public SfxId Id;
            public SfxCategory Category;
            public int Priority;
            public float StartTime;   // unscaled
            public float Gain;        // profile x request x jitter (before buses / duck / fade)
            public bool DuckExempt;
            public Transform Follow;
            public bool HadFollow;
            public bool Loop;
            public float FadeDuration;
            public float FadeRemaining; // > 0 while fading out
        }

        private Voice[] _voices = Array.Empty<Voice>();
        private int _nextHandle;

        // ------------------------------------------------------------------ clips

        private const int LoopVariant = 15;
        private const int VariantBits = 16;

        private static readonly SfxId[] s_synthesisPriority =
        {
            SfxId.BallBounceFloor, SfxId.BallHitPlayer, SfxId.Catch, SfxId.Throw, SfxId.Footstep, SfxId.Whistle,
            SfxId.Countdown, SfxId.RoundStart, SfxId.UiClick, SfxId.UiConfirm, SfxId.CrowdAmbience, SfxId.BallHitHeavy,
            SfxId.PerfectCatch, SfxId.BallBounceWall, SfxId.Pickup, SfxId.Jump, SfxId.Land, SfxId.Slide, SfxId.CatchWhiff,
            SfxId.ThrowHeavy, SfxId.Pass, SfxId.CrowdCheer, SfxId.CrowdGasp, SfxId.Elimination, SfxId.Revive,
        };

        private int _idCount;
        private AudioClip[][] _clips = Array.Empty<AudioClip[]>();
        private AudioClip[] _loopClips = Array.Empty<AudioClip>();
        private float[][][] _pcm = Array.Empty<float[][]>();   // [id][variant] written by the worker
        private float[][] _loopPcm = Array.Empty<float[]>();   // [id]
        private int[] _jobs = Array.Empty<int>();
        private int _pumpCursor;
        private Thread _worker;
        private volatile bool _stopWorker;
        private bool _useWorker;
        private float[] _lastPlay = Array.Empty<float>();
        private readonly List<AudioClip> _ownedClips = new List<AudioClip>(96);

        // ------------------------------------------------------------------ mix state

        private float _duckGain = 1f;
        private float _duckUntil = -1f;
        private float _crowdLevel;
        private float _crowdTarget;
        private float _excitement;
        private int _crowdHandle;
        private AudioListener _listener;
        private float _nextListenerSearch;
        private int _lastPassBallId = -1;
        private int _lastPassFrame = -1;
        private bool _subscribed;

        private struct FootState
        {
            public float Phase;
            public float SqueakCooldown;
        }

        private readonly Dictionary<int, FootState> _feet = new Dictionary<int, FootState>(8);

        // Cached delegates (subscribe / unsubscribe without allocations).
        private Action<BallThrownEvent> _onThrown;
        private Action<BallHitPlayerEvent> _onHit;
        private Action<BallCaughtEvent> _onCaught;
        private Action<CatchWhiffEvent> _onWhiff;
        private Action<BallPickedUpEvent> _onPickup;
        private Action<BallPassedEvent> _onPassed;
        private Action<BallBouncedEvent> _onBounced;
        private Action<PlayerEliminatedEvent> _onEliminated;
        private Action<PlayerRevivedEvent> _onRevived;
        private Action<RoundCountdownEvent> _onCountdown;
        private Action<RoundStartedEvent> _onRoundStarted;
        private Action<RoundEndedEvent> _onRoundEnded;
        private Action<MatchEndedEvent> _onMatchEnded;
        private Action<MatchPhaseChangedEvent> _onPhase;
        private Action<AbilityCastEvent> _onAbilityCast;
        private Action<UltimateChargeChangedEvent> _onUltimate;
        private Action<HitstopEvent> _onHitstop;

        // ------------------------------------------------------------------ contract

        public void Play(SfxId id, Vector3 position, float volume = 1f, float pitch = 1f) =>
            PlayInternal(id, -1, true, position, null, volume, pitch, false);

        public void Play2D(SfxId id, float volume = 1f, float pitch = 1f) =>
            PlayInternal(id, -1, false, Vector3.zero, null, volume, pitch, false);

        /// <summary>Null-safe shortcut.</summary>
        public static void PlayAt(SfxId id, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (Instance != null) Instance.Play(id, position, volume, pitch);
        }

        /// <summary>Null-safe shortcut.</summary>
        public static void PlayUi(SfxId id, float volume = 1f, float pitch = 1f)
        {
            if (Instance != null) Instance.Play2D(id, volume, pitch);
        }

        // ------------------------------------------------------------------ additions

        /// <summary>True once every placeholder clip exists (the start-up synthesis finished).</summary>
        public bool IsReady => _pumpCursor >= _jobs.Length;

        /// <summary>Master volume 0..1.</summary>
        public float MasterVolume
        {
            get => masterVolume;
            set => masterVolume = Mathf.Clamp01(value);
        }

        /// <summary>Mutes everything (voices keep running silently).</summary>
        public bool Muted
        {
            get => muted;
            set => muted = value;
        }

        public float GetCategoryVolume(SfxCategory category)
        {
            switch (category)
            {
                case SfxCategory.Crowd: return crowdVolume;
                case SfxCategory.Announcer: return announcerVolume;
                case SfxCategory.Ui: return uiVolume;
                default: return sfxVolume;
            }
        }

        public void SetCategoryVolume(SfxCategory category, float volume)
        {
            volume = Mathf.Clamp01(volume);
            switch (category)
            {
                case SfxCategory.Crowd: crowdVolume = volume; break;
                case SfxCategory.Announcer: announcerVolume = volume; break;
                case SfxCategory.Ui: uiVolume = volume; break;
                default: sfxVolume = volume; break;
            }
        }

        /// <summary>Plays a specific variant (e.g. Footstep 4-5 = sneaker squeaks, Whistle 1 = double blast) in 3D.</summary>
        public void PlayVariant(SfxId id, int variant, Vector3 position, float volume = 1f, float pitch = 1f) =>
            PlayInternal(id, Mathf.Max(0, variant), true, position, null, volume, pitch, false);

        /// <summary>Plays a specific variant in 2D.</summary>
        public void PlayVariant2D(SfxId id, int variant, float volume = 1f, float pitch = 1f) =>
            PlayInternal(id, Mathf.Max(0, variant), false, Vector3.zero, null, volume, pitch, false);

        /// <summary>
        /// Plays <paramref name="id"/> following <paramref name="target"/> (3D). Loops (hums, crowd) run until
        /// <see cref="Stop"/>, or fade out when the target is destroyed. Returns an invalid handle when nothing played.
        /// </summary>
        public AudioHandle PlayAttached(SfxId id, Transform target, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Vector3 position = target != null ? target.position : Vector3.zero;
            int voice = PlayInternal(id, -1, target != null, position, target, volume, pitch, loop);
            return voice >= 0 ? new AudioHandle { Id = _voices[voice].HandleId, Voice = voice } : default;
        }

        /// <summary>Stops a sound started with <see cref="PlayAttached"/> (fading over <paramref name="fadeOut"/> s).</summary>
        public void Stop(AudioHandle handle, float fadeOut = 0.08f)
        {
            Voice v = Resolve(handle);
            if (v == null) return;
            if (fadeOut <= 0f) StopVoice(v);
            else if (v.FadeRemaining <= 0f)
            {
                v.FadeDuration = fadeOut;
                v.FadeRemaining = fadeOut;
            }
        }

        /// <summary>True while the sound behind <paramref name="handle"/> plays.</summary>
        public bool IsPlaying(AudioHandle handle) => Resolve(handle) != null;

        /// <summary>Changes the gain of a running sound (e.g. a hum that swells).</summary>
        public void SetVolume(AudioHandle handle, float volume)
        {
            Voice v = Resolve(handle);
            if (v != null) v.Gain = SfxProfiles.Get(v.Id).Volume * Mathf.Max(0f, volume);
        }

        /// <summary>Null-safe <see cref="PlayAttached"/>.</summary>
        public static AudioHandle Attach(SfxId id, Transform target, float volume = 1f, float pitch = 1f, bool loop = false) =>
            Instance != null ? Instance.PlayAttached(id, target, volume, pitch, loop) : default;

        /// <summary>Null-safe <see cref="Stop"/>.</summary>
        public static void StopSound(AudioHandle handle, float fadeOut = 0.08f)
        {
            if (Instance != null && handle.IsValid) Instance.Stop(handle, fadeOut);
        }

        /// <summary>Stops every voice (scene changes, returning to the menu).</summary>
        public void StopAll()
        {
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Active) StopVoice(_voices[i]);
            _crowdHandle = 0;
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Dodgeball Ultra] A second AudioManager was found and disabled.", this);
                enabled = false;
                return;
            }
            Instance = this;

            if (library == null && !string.IsNullOrEmpty(libraryResourcePath))
                library = Resources.Load<AudioLibrary>(libraryResourcePath);

            _idCount = Enum.GetValues(typeof(SfxId)).Length;
            _lastPlay = new float[_idCount];
            for (int i = 0; i < _lastPlay.Length; i++) _lastPlay[i] = -999f;

            CreateDelegates();
            BuildVoices();
            StartSynthesis();
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            if (Instance == this && reactToGameEvents) Subscribe();
        }

        private void OnDisable() => Unsubscribe();

        private void OnDestroy()
        {
            Unsubscribe();
            _stopWorker = true;
            if (Instance == this) Instance = null;
            for (int i = 0; i < _ownedClips.Count; i++)
                if (_ownedClips[i] != null) Destroy(_ownedClips[i]);
            _ownedClips.Clear();
        }

        private void Update()
        {
            if (Instance != this) return;
            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;

            PumpClips();
            UpdateListener(now);
            UpdateDuck(now, dt);
            UpdateCrowd(dt);

            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];
                if (!v.Active) continue;

                if (v.Follow != null) v.Transform.position = v.Follow.position;
                else if (v.HadFollow)
                {
                    // The emitter is gone: loops fade out, one-shots finish where it was.
                    v.HadFollow = false;
                    if (v.Loop && v.FadeRemaining <= 0f)
                    {
                        v.FadeDuration = 0.15f;
                        v.FadeRemaining = 0.15f;
                    }
                }

                if (v.FadeRemaining > 0f)
                {
                    v.FadeRemaining -= dt;
                    if (v.FadeRemaining <= 0f)
                    {
                        StopVoice(v);
                        continue;
                    }
                }

                if (!v.Loop && !v.Source.isPlaying && now - v.StartTime > 0.05f && !AudioListener.pause)
                {
                    v.Active = false; // finished
                    v.HandleId = 0;
                    v.Follow = null;
                    continue;
                }

                v.Source.volume = ComputeVolume(v);
            }

            if (footsteps) UpdateFootsteps(Time.deltaTime);
        }

        // ------------------------------------------------------------------ playback core

        private void BuildVoices()
        {
            int count = Mathf.Clamp(voiceCount, 8, 64);
            _voices = new Voice[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Voice " + i.ToString("00"));
                go.transform.SetParent(transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = minDistance;
                source.maxDistance = maxDistance;
                source.dopplerLevel = dopplerLevel;
                source.spread = 0f;
                source.reverbZoneMix = reverbZoneMix;
                _voices[i] = new Voice { Source = source, Transform = go.transform };
            }
        }

        /// <summary>Starts a voice. Returns its index, or -1 when nothing played.</summary>
        private int PlayInternal(SfxId id, int variant, bool spatial, Vector3 position, Transform follow, float volume, float pitch,
            bool loop)
        {
            int index = (int)id;
            if (index < 0 || index >= _idCount || _voices.Length == 0 || volume <= 0f) return -1;

            SfxProfile profile = SfxProfiles.Get(id);
            float now = Time.unscaledTime;
            if (!loop && profile.MinInterval > 0f && now - _lastPlay[index] < profile.MinInterval) return -1;
            if (spatial && !loop && _listener != null)
            {
                float cull = maxDistance * 1.1f;
                if ((position - _listener.transform.position).sqrMagnitude > cull * cull) return -1; // inaudible anyway
            }

            float libVolume = 1f, libPitch = 1f;
            AudioClip clip = loop ? GetLoopClip(id, out libVolume, out libPitch) : GetClip(id, variant, out libVolume, out libPitch);
            if (clip == null) return -1;

            int voiceIndex = AcquireVoice(in profile, id);
            if (voiceIndex < 0) return -1;
            _lastPlay[index] = now;

            Voice v = _voices[voiceIndex];
            float gain = profile.Volume * libVolume * volume * (1f + UnityEngine.Random.Range(-1f, 1f) * profile.VolumeJitter);
            float finalPitch = pitch * libPitch * (1f + UnityEngine.Random.Range(-1f, 1f) * profile.PitchJitter);

            v.Active = true;
            v.Id = id;
            v.Category = profile.Category;
            v.Priority = profile.Priority;
            v.StartTime = now;
            v.Gain = Mathf.Max(0f, gain);
            v.DuckExempt = profile.DuckExempt;
            v.Follow = follow;
            v.HadFollow = follow != null;
            v.Loop = loop;
            v.FadeRemaining = 0f;
            if (++_nextHandle == 0) ++_nextHandle;
            v.HandleId = _nextHandle;

            AudioSource s = v.Source;
            s.clip = clip;
            s.loop = loop;
            s.pitch = Mathf.Clamp(finalPitch, 0.05f, 3f);
            s.spatialBlend = spatial ? 1f : 0f;
            s.priority = Mathf.Clamp(profile.Priority, 0, 256);
            s.outputAudioMixerGroup = GroupFor(profile.Category);
            s.ignoreListenerPause = profile.Category == SfxCategory.Ui;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            v.Transform.position = position;
            s.volume = ComputeVolume(v);
            s.time = 0f;
            s.Play();
            return voiceIndex;
        }

        /// <summary>Free voice, else the oldest of the same id beyond its cap, else the least important non-loop voice.</summary>
        private int AcquireVoice(in SfxProfile profile, SfxId id)
        {
            int free = -1, oldestSame = -1, victim = -1, sameCount = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];
                if (!v.Active)
                {
                    if (free < 0) free = i;
                    continue;
                }
                if (v.Loop) continue; // loops are owned by someone: never stolen
                if (v.Id == id)
                {
                    sameCount++;
                    if (oldestSame < 0 || v.StartTime < _voices[oldestSame].StartTime) oldestSame = i;
                }
                if (victim < 0 || v.Priority > _voices[victim].Priority ||
                    (v.Priority == _voices[victim].Priority && v.StartTime < _voices[victim].StartTime))
                    victim = i;
            }

            if (sameCount >= Mathf.Max(1, profile.MaxVoices) && oldestSame >= 0)
            {
                StopVoice(_voices[oldestSame]);
                return oldestSame;
            }
            if (free >= 0) return free;
            if (victim >= 0 && _voices[victim].Priority >= profile.Priority)
            {
                StopVoice(_voices[victim]);
                return victim;
            }
            return -1;
        }

        private Voice Resolve(AudioHandle handle)
        {
            if (!handle.IsValid || handle.Voice < 0 || handle.Voice >= _voices.Length) return null;
            Voice v = _voices[handle.Voice];
            return v.Active && v.HandleId == handle.Id ? v : null;
        }

        private void StopVoice(Voice v)
        {
            if (v.Source != null) v.Source.Stop();
            v.Active = false;
            v.HandleId = 0;
            v.Follow = null;
            v.HadFollow = false;
            v.FadeRemaining = 0f;
        }

        private float ComputeVolume(Voice v)
        {
            if (muted) return 0f;
            float volume = v.Gain * masterVolume * GetCategoryVolume(v.Category);
            if (!v.DuckExempt) volume *= _duckGain;
            if (v.FadeRemaining > 0f && v.FadeDuration > 0f) volume *= Mathf.Clamp01(v.FadeRemaining / v.FadeDuration);
            return Mathf.Clamp01(volume);
        }

        private AudioMixerGroup GroupFor(SfxCategory category)
        {
            switch (category)
            {
                case SfxCategory.Crowd: return crowdGroup;
                case SfxCategory.Announcer: return announcerGroup;
                case SfxCategory.Ui: return uiGroup;
                default: return sfxGroup;
            }
        }

        private void UpdateListener(float now)
        {
            if (_listener != null && _listener.isActiveAndEnabled) return;
            _listener = null;
            if (now < _nextListenerSearch) return;
            _nextListenerSearch = now + 1f;
            _listener = FindAnyObjectByType<AudioListener>();
        }

        // ------------------------------------------------------------------ clips

        private void StartSynthesis()
        {
            _clips = new AudioClip[_idCount][];
            _pcm = new float[_idCount][][];
            _loopClips = new AudioClip[_idCount];
            _loopPcm = new float[_idCount][];
            for (int i = 0; i < _idCount; i++)
            {
                int variants = Mathf.Max(1, ProceduralSfx.VariantCount((SfxId)i));
                _clips[i] = new AudioClip[variants];
                _pcm[i] = new float[variants][];
            }

            // Job order: what the first seconds of a match need first, then everything else, then the dedicated loops.
            var jobs = new List<int>(128);
            var queued = new bool[_idCount];
            for (int i = 0; i < s_synthesisPriority.Length; i++) QueueId(jobs, queued, (int)s_synthesisPriority[i]);
            for (int i = 0; i < _idCount; i++) QueueId(jobs, queued, i);
            for (int i = 0; i < _idCount; i++)
            {
                var id = (SfxId)i;
                if (ProceduralSfx.SupportsLoop(id) && !ProceduralSfx.IsLoopByDefault(id)) jobs.Add(i * VariantBits + LoopVariant);
            }
            _jobs = jobs.ToArray();
            _pumpCursor = 0;

#if UNITY_WEBGL && !UNITY_EDITOR
            _useWorker = false; // no threads: PumpClips synthesises one clip per frame
#else
            _useWorker = synthesizeInBackground;
#endif
            if (!_useWorker) return;
            try
            {
                _stopWorker = false;
                _worker = new Thread(SynthesisWorker)
                {
                    IsBackground = true,
                    Name = "DodgeballUltra SFX synthesis",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                _worker.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Dodgeball Ultra] Background audio synthesis unavailable, synthesising on the main thread: " + e.Message, this);
                _useWorker = false;
            }
        }

        private void QueueId(List<int> jobs, bool[] queued, int id)
        {
            if (id < 0 || id >= _idCount || queued[id]) return;
            queued[id] = true;
            int variants = _clips[id].Length;
            for (int v = 0; v < variants; v++) jobs.Add(id * VariantBits + v);
        }

        /// <summary>Worker thread: pure C# synthesis (no Unity API) into the PCM slots, in job order.</summary>
        private void SynthesisWorker()
        {
            try
            {
                int[] jobs = _jobs;
                for (int j = 0; j < jobs.Length && !_stopWorker; j++)
                {
                    int id = jobs[j] / VariantBits, variant = jobs[j] % VariantBits;
                    if (variant == LoopVariant)
                    {
                        float[] loopData = ProceduralSfx.SynthesizeLoop((SfxId)id);
                        Volatile.Write(ref _loopPcm[id], loopData);
                    }
                    else
                    {
                        float[] data = ProceduralSfx.Synthesize((SfxId)id, variant);
                        Volatile.Write(ref _pcm[id][variant], data);
                    }
                }
            }
            catch (Exception e)
            {
                // Main-thread synthesis on demand still works.
                Debug.LogWarning("[Dodgeball Ultra] Audio synthesis worker stopped: " + e.Message);
            }
        }

        /// <summary>Turns finished PCM into AudioClips (main thread only), a few per frame, in job order.</summary>
        private void PumpClips()
        {
            if (_pumpCursor >= _jobs.Length) return;
            int budget = _useWorker ? Mathf.Max(1, clipsPerFrame) : 1;
            while (budget > 0 && _pumpCursor < _jobs.Length)
            {
                int job = _jobs[_pumpCursor];
                int id = job / VariantBits, variant = job % VariantBits;
                bool isLoop = variant == LoopVariant;

                if (isLoop ? _loopClips[id] != null : _clips[id][variant] != null)
                {
                    // Already created on demand.
                    if (isLoop) _loopPcm[id] = null;
                    else _pcm[id][variant] = null;
                    _pumpCursor++;
                    continue;
                }

                float[] data = isLoop ? Volatile.Read(ref _loopPcm[id]) : Volatile.Read(ref _pcm[id][variant]);
                if (data == null)
                {
                    if (_useWorker && _worker != null && _worker.IsAlive) break; // the worker has not got there yet
                    data = isLoop ? ProceduralSfx.SynthesizeLoop((SfxId)id) : ProceduralSfx.Synthesize((SfxId)id, variant);
                }

                if (isLoop)
                {
                    _loopClips[id] = CreateClip((SfxId)id, "loop", data);
                    _loopPcm[id] = null;
                }
                else
                {
                    _clips[id][variant] = CreateClip((SfxId)id, variant.ToString(), data);
                    _pcm[id][variant] = null;
                }
                _pumpCursor++;
                budget--;
            }
        }

        private AudioClip CreateClip(SfxId id, string suffix, float[] data)
        {
            AudioClip clip = ProceduralSfx.CreateClip("DU_" + id + "_" + suffix, data);
            if (clip != null) _ownedClips.Add(clip);
            return clip;
        }

        /// <summary>Library clip, else the procedural variant (synthesised now if the start-up pass has not reached it).</summary>
        private AudioClip GetClip(SfxId id, int variant, out float volume, out float pitch)
        {
            volume = 1f;
            pitch = 1f;
            if (library != null)
            {
                AudioClip recorded = library.PickClip(id, variant, out volume, out pitch);
                if (recorded != null) return recorded;
                volume = 1f;
                pitch = 1f;
            }

            int index = (int)id;
            AudioClip[] variants = _clips[index];
            int v = variant >= 0
                ? variant % variants.Length
                : UnityEngine.Random.Range(0, Mathf.Clamp(ProceduralSfx.RandomVariantCount(id), 1, variants.Length));

            if (variants[v] != null) return variants[v];
            float[] data = Volatile.Read(ref _pcm[index][v]);
            if (data == null) data = ProceduralSfx.Synthesize(id, v);
            variants[v] = CreateClip(id, v.ToString(), data);
            _pcm[index][v] = null;
            return variants[v];
        }

        /// <summary>Seamless loop of <paramref name="id"/> (dedicated loop, the regular clip when it is one, else variant 0).</summary>
        private AudioClip GetLoopClip(SfxId id, out float volume, out float pitch)
        {
            volume = 1f;
            pitch = 1f;
            if (library != null)
            {
                AudioClip recorded = library.GetLoop(id, out volume, out pitch);
                if (recorded != null) return recorded;
                volume = 1f;
                pitch = 1f;
            }

            int index = (int)id;
            if (!ProceduralSfx.SupportsLoop(id) || ProceduralSfx.IsLoopByDefault(id)) return GetClip(id, 0, out volume, out pitch);
            if (_loopClips[index] != null) return _loopClips[index];
            float[] data = Volatile.Read(ref _loopPcm[index]);
            if (data == null) data = ProceduralSfx.SynthesizeLoop(id);
            _loopClips[index] = CreateClip(id, "loop", data);
            _loopPcm[index] = null;
            return _loopClips[index];
        }

        /// <summary>True when the clip of (id, variant 0) can be played without synthesising on the main thread.</summary>
        private bool IsClipAvailable(SfxId id)
        {
            if (library != null && library.TryGet(id, out _)) return true;
            int index = (int)id;
            return _clips[index][0] != null || Volatile.Read(ref _pcm[index][0]) != null;
        }

        // ------------------------------------------------------------------ mix

        private void UpdateDuck(float now, float dt)
        {
            if (now < _duckUntil) _duckGain = 1f - duckAmount;
            else _duckGain = Mathf.MoveTowards(_duckGain, 1f, dt / Mathf.Max(0.01f, duckRelease) * duckAmount);
        }

        private void UpdateCrowd(float dt)
        {
            if (!crowdAmbience)
            {
                if (_crowdHandle != 0)
                {
                    Stop(new AudioHandle { Id = _crowdHandle, Voice = FindVoice(_crowdHandle) }, 0.5f);
                    _crowdHandle = 0;
                }
                return;
            }

            _excitement *= Mathf.Exp(-dt / Mathf.Max(0.05f, excitementDecay));
            if (_crowdTarget <= 0f) _crowdTarget = crowdIdle;
            _crowdLevel = Mathf.MoveTowards(_crowdLevel, _crowdTarget, crowdFadeRate * dt);

            int voice = _crowdHandle != 0 ? FindVoice(_crowdHandle) : -1;
            if (voice < 0)
            {
                _crowdHandle = 0;
                // Start the loop only once it exists (never synthesise 10 s of crowd on the main thread mid-frame).
                if (!IsClipAvailable(SfxId.CrowdAmbience)) return;
                int started = PlayInternal(SfxId.CrowdAmbience, 0, false, Vector3.zero, null, 1f, 1f, true);
                if (started < 0) return;
                _crowdHandle = _voices[started].HandleId;
                voice = started;
            }

            Voice v = _voices[voice];
            if (v.FadeRemaining > 0f) return;
            v.Gain = Mathf.Clamp01(_crowdLevel + excitementGain * Mathf.Clamp01(_excitement));
        }

        private int FindVoice(int handleId)
        {
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Active && _voices[i].HandleId == handleId) return i;
            return -1;
        }

        private void Excite(float amount) => _excitement = Mathf.Min(1.5f, _excitement + amount);

        // ------------------------------------------------------------------ footsteps

        private void UpdateFootsteps(float dt)
        {
            if (dt <= 0f) return;
            IReadOnlyList<DodgeballPlayer> players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                DodgeballPlayer p = players[i];
                if (p == null || !p.isActiveAndEnabled) continue;
                PlayerMotor motor = p.Motor;
                if (motor == null) continue;

                _feet.TryGetValue(p.PlayerId, out FootState st);
                bool silent = p.Status != null && p.Status.Has(StatusEffectType.SilentFootsteps);
                PlayerStateMachine fsm = p.StateMachine;
                bool canStep = motor.IsGrounded && !motor.IsSliding && !motor.IsFrozen &&
                               (fsm == null || fsm.Current != PlayerStateId.Incapacitated);
                float speed = motor.PlanarSpeed;

                if (canStep && speed > minFootstepSpeed)
                {
                    float cadence = Mathf.Clamp(cadenceBase + cadencePerSpeed * speed, cadenceMin, cadenceMax);
                    st.Phase += cadence * dt;
                    if (st.Phase >= 1f)
                    {
                        st.Phase -= Mathf.Floor(st.Phase);
                        if (!silent)
                        {
                            float k = Mathf.InverseLerp(minFootstepSpeed, AnimatorSprint, speed);
                            Play(SfxId.Footstep, p.Position, footstepVolume * Mathf.Lerp(0.45f, 1f, k), Mathf.Lerp(1.04f, 0.96f, k));
                        }
                    }
                }
                else
                {
                    st.Phase = 0.6f; // the first step lands shortly after starting to move
                }

                st.SqueakCooldown -= dt;
                if (canStep && !silent && speed > squeakMinSpeed && st.SqueakCooldown <= 0f)
                {
                    Vector3 planarVelocity = motor.PlanarVelocity;
                    bool cut = Mathf.Abs(motor.YawRate) > squeakYawRate;
                    bool braking = planarVelocity.sqrMagnitude > 1e-4f &&
                                   Vector3.Dot(motor.PlanarAcceleration, planarVelocity.normalized) < -squeakBraking;
                    if (cut || braking)
                    {
                        if (UnityEngine.Random.value < squeakChance)
                        {
                            PlayVariant(SfxId.Footstep, 4 + UnityEngine.Random.Range(0, 2), p.Position,
                                Mathf.Lerp(0.5f, 0.8f, Mathf.InverseLerp(squeakMinSpeed, AnimatorSprint, speed)));
                            st.SqueakCooldown = squeakCooldown;
                        }
                        else
                        {
                            st.SqueakCooldown = 0.2f;
                        }
                    }
                }

                _feet[p.PlayerId] = st;
            }
        }

        private const float AnimatorSprint = 7.4f; // m/s of a full sprint (loudest steps)

        // ------------------------------------------------------------------ game events

        private void CreateDelegates()
        {
            _onThrown = OnBallThrown;
            _onHit = OnBallHitPlayer;
            _onCaught = OnBallCaught;
            _onWhiff = OnCatchWhiff;
            _onPickup = OnBallPickedUp;
            _onPassed = OnBallPassed;
            _onBounced = OnBallBounced;
            _onEliminated = OnPlayerEliminated;
            _onRevived = OnPlayerRevived;
            _onCountdown = OnCountdown;
            _onRoundStarted = OnRoundStarted;
            _onRoundEnded = OnRoundEnded;
            _onMatchEnded = OnMatchEnded;
            _onPhase = OnPhaseChanged;
            _onAbilityCast = OnAbilityCast;
            _onUltimate = OnUltimateCharge;
            _onHitstop = OnHitstop;
        }

        private void Subscribe()
        {
            if (_subscribed || _onThrown == null) return;
            GameEvents.Subscribe(_onThrown);
            GameEvents.Subscribe(_onHit);
            GameEvents.Subscribe(_onCaught);
            GameEvents.Subscribe(_onWhiff);
            GameEvents.Subscribe(_onPickup);
            GameEvents.Subscribe(_onPassed);
            GameEvents.Subscribe(_onBounced);
            GameEvents.Subscribe(_onEliminated);
            GameEvents.Subscribe(_onRevived);
            GameEvents.Subscribe(_onCountdown);
            GameEvents.Subscribe(_onRoundStarted);
            GameEvents.Subscribe(_onRoundEnded);
            GameEvents.Subscribe(_onMatchEnded);
            GameEvents.Subscribe(_onPhase);
            GameEvents.Subscribe(_onAbilityCast);
            GameEvents.Subscribe(_onUltimate);
            GameEvents.Subscribe(_onHitstop);
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            GameEvents.Unsubscribe(_onThrown);
            GameEvents.Unsubscribe(_onHit);
            GameEvents.Unsubscribe(_onCaught);
            GameEvents.Unsubscribe(_onWhiff);
            GameEvents.Unsubscribe(_onPickup);
            GameEvents.Unsubscribe(_onPassed);
            GameEvents.Unsubscribe(_onBounced);
            GameEvents.Unsubscribe(_onEliminated);
            GameEvents.Unsubscribe(_onRevived);
            GameEvents.Unsubscribe(_onCountdown);
            GameEvents.Unsubscribe(_onRoundStarted);
            GameEvents.Unsubscribe(_onRoundEnded);
            GameEvents.Unsubscribe(_onMatchEnded);
            GameEvents.Unsubscribe(_onPhase);
            GameEvents.Unsubscribe(_onAbilityCast);
            GameEvents.Unsubscribe(_onUltimate);
            GameEvents.Unsubscribe(_onHitstop);
            _subscribed = false;
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            if (e.IsPass)
            {
                Play(SfxId.Pass, e.Origin, 0.8f);
                if (e.Ball != null)
                {
                    _lastPassBallId = e.Ball.BallId;
                    _lastPassFrame = Time.frameCount;
                }
                return;
            }
            if (e.Ball != null && e.Ball.Style == BallStyle.Turret) return; // the turret plays its own pneumatic shot

            float k = Mathf.InverseLerp(40f, 160f, e.SpeedKmh);
            bool heavy = e.SpeedKmh >= heavyThrowSpeedKmh || e.IsCounterThrow;
            Play(heavy ? SfxId.ThrowHeavy : SfxId.Throw, e.Origin, Mathf.Lerp(0.55f, 1f, k), Mathf.Lerp(0.94f, 1.1f, k));
        }

        private void OnBallPassed(BallPassedEvent e)
        {
            if (e.Teleported) return; // Houdini's Hat Trick plays its own teleport
            if (e.Ball != null && e.Ball.BallId == _lastPassBallId && _lastPassFrame == Time.frameCount) return;
            Vector3 at = e.From != null ? e.From.ChestPosition : (e.Ball != null ? e.Ball.transform.position : Vector3.zero);
            Play(SfxId.Pass, at, 0.8f);
        }

        private void OnBallHitPlayer(BallHitPlayerEvent e)
        {
            if (e.Outcome == HitOutcome.Ignored) return;
            if (e.Outcome == HitOutcome.Negated)
            {
                Play(SfxId.BallBounceWall, e.Point, 0.6f, 1.1f); // deflected off a shield / evasion
                return;
            }
            float k = Mathf.InverseLerp(30f, 140f, e.SpeedKmh);
            bool heavy = e.SpeedKmh >= heavyHitSpeedKmh || e.Outcome == HitOutcome.Eliminated;
            Play(heavy ? SfxId.BallHitHeavy : SfxId.BallHitPlayer, e.Point, Mathf.Lerp(0.6f, 1f, k), Mathf.Lerp(1.05f, 0.92f, k));
            Excite(0.12f);
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (e.Quality == CatchQuality.Perfect)
            {
                Play(SfxId.PerfectCatch, e.Point);
                Play2D(SfxId.CrowdCheer, 0.45f);
                Excite(0.3f);
            }
            else
            {
                float k = Mathf.InverseLerp(30f, 140f, e.SpeedKmh);
                Play(SfxId.Catch, e.Point, Mathf.Lerp(0.65f, 1f, k), Mathf.Lerp(1.04f, 0.95f, k));
                Excite(0.1f);
            }
        }

        private void OnCatchWhiff(CatchWhiffEvent e)
        {
            if (e.Player != null) Play(SfxId.CatchWhiff, e.Player.ChestPosition, 0.6f);
        }

        private void OnBallPickedUp(BallPickedUpEvent e)
        {
            Vector3 at = e.Ball != null ? e.Ball.transform.position : (e.Player != null ? e.Player.Position : Vector3.zero);
            Play(SfxId.Pickup, at, 0.7f);
        }

        private void OnBallBounced(BallBouncedEvent e)
        {
            if (e.ImpactSpeed < minBounceSpeed) return;
            float k = Mathf.InverseLerp(minBounceSpeed, Mathf.Max(minBounceSpeed + 0.1f, fullBounceSpeed), e.ImpactSpeed);
            float volume = Mathf.Max(0.15f, Mathf.Pow(k, 0.7f));
            Play(e.HitFloor ? SfxId.BallBounceFloor : SfxId.BallBounceWall, e.Point, volume, Mathf.Lerp(1.06f, 0.96f, k));
        }

        private void OnPlayerEliminated(PlayerEliminatedEvent e)
        {
            if (e.Player != null) Play(SfxId.Elimination, e.Player.ChestPosition);
            Play2D(SfxId.CrowdCheer, 0.55f);
            Excite(0.45f);
        }

        private void OnPlayerRevived(PlayerRevivedEvent e)
        {
            if (e.Player != null) Play(SfxId.Revive, e.Player.Position + Vector3.up, 0.9f);
            Excite(0.2f);
        }

        private void OnCountdown(RoundCountdownEvent e)
        {
            if (e.SecondsLeft > 0) Play2D(SfxId.Countdown);
            _crowdTarget = crowdCountdown;
        }

        private void OnRoundStarted(RoundStartedEvent e)
        {
            Play2D(SfxId.RoundStart, 0.9f);
            PlayVariant2D(SfxId.Whistle, 0);
            _crowdTarget = crowdPlaying;
            Excite(0.25f);
        }

        private void OnRoundEnded(RoundEndedEvent e)
        {
            PlayVariant2D(SfxId.Whistle, 1); // double blast
            Play2D(SfxId.CrowdCheer, 0.8f);
            _crowdTarget = crowdRoundEnd;
            Excite(0.5f);
        }

        private void OnMatchEnded(MatchEndedEvent e)
        {
            PlayVariant2D(SfxId.CrowdCheer, 1, 1f);
            _crowdTarget = crowdMatchEnd;
            Excite(0.8f);
        }

        private void OnPhaseChanged(MatchPhaseChangedEvent e)
        {
            switch (e.Current)
            {
                case MatchPhase.PreRound: _crowdTarget = crowdPreRound; break;
                case MatchPhase.Countdown: _crowdTarget = crowdCountdown; break;
                case MatchPhase.Playing: _crowdTarget = crowdPlaying; break;
                case MatchPhase.RoundEnd: _crowdTarget = crowdRoundEnd; break;
                case MatchPhase.MatchEnd: _crowdTarget = crowdMatchEnd; break;
                default: _crowdTarget = crowdIdle; break;
            }
        }

        private void OnAbilityCast(AbilityCastEvent e)
        {
            if (e.Player == null) return;
            Vector3 chest = e.Player.ChestPosition;
            if (AbilitySfxTable.TryGetCastSound(e.Player.Hero, e.Slot, out SfxId sfx, out float volume)) Play(sfx, chest, volume);
            if (e.Slot == Abilities.AbilitySlot.Ultimate)
            {
                Play(SfxId.UltimateCast, chest, 0.9f);
                Excite(0.3f);
            }
        }

        private void OnUltimateCharge(UltimateChargeChangedEvent e)
        {
            if (e.BecameReady && e.Player != null && e.Player.IsLocalPlayer) Play2D(SfxId.UltimateReady, 0.8f);
        }

        private void OnHitstop(HitstopEvent e)
        {
            if (e.Duration <= 0f) return;
            _duckUntil = Mathf.Max(_duckUntil, Time.unscaledTime + Mathf.Min(e.Duration, maxDuckHold));
            _duckGain = 1f - duckAmount;
        }
    }
}
