using System.Collections.Generic;
using DodgeballUltra.Abilities;
using DodgeballUltra.CameraSystem;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>Everything the juice pipeline needs to know about a hit.</summary>
    public struct HitJuiceRequest
    {
        public Vector3 Point;
        public Vector3 Normal;          // collision normal (for squash &amp; stretch orientation)
        public float SpeedKmh;          // drives intensity
        public DodgeballPlayer Victim;  // flashed
        public DodgeBall Ball;          // squashed
        public bool Eliminated;
        public bool LocalPlayerInvolved;
    }

    /// <summary>Everything the juice pipeline needs to know about a catch.</summary>
    public struct CatchJuiceRequest
    {
        public Vector3 Point;
        public Vector3 Normal;
        public float SpeedKmh;
        public DodgeballPlayer Catcher;
        public DodgeBall Ball;
        public bool Perfect;
        public bool LocalPlayerInvolved;
    }

    /// <summary>
    /// CONTRACT (kernel, spec file) - Hit-Feel &amp; Game Juice framework.
    /// Subscribes to BallHitPlayerEvent and BallCaughtEvent: every hit and every perfect catch runs the pipeline
    /// <c>Hitstop (0.03-0.1 s, unscaled) -> Perlin camera shake -> ball squash &amp; stretch along the normal -> 0.05 s white hit-flash
    /// -> screen pulse</c>, all tuned by a JuiceProfile ScriptableObject.
    /// <para>
    /// <b>Why an observer.</b> Combat never calls the juice code. It publishes facts on the <see cref="GameEvents"/> bus
    /// (<see cref="BallHitPlayerEvent"/>, <see cref="BallCaughtEvent"/>, <see cref="PlayerEliminatedEvent"/> ...); this
    /// manager turns them into feel. Removing the manager removes the feel, never the gameplay.
    /// </para>
    /// <para>
    /// <b>Intensity.</b> Every stage is scaled by one number: <c>intensity = Profile.IntensityForSpeed(SpeedKmh)</c>
    /// (0..1, 40 km/h .. 220 km/h through a curve). A lob barely nudges the camera; a 220 km/h rally ball freezes the
    /// frame for 0.1 s, shakes hard and flattens the ball by more than half.
    /// </para>
    /// <para>
    /// <b>Pipeline for a hit</b> (<see cref="PlayHit"/>):
    /// <code>
    /// 1 Hitstop   lerp(hitstopMin, hitstopMax, intensity) × (eliminated ? eliminationHitstopMultiplier : 1)
    ///             clamped to 0.03..0.1 s, Time.timeScale → hitstopTimeScale, unscaled countdown, then restored
    /// 2 Shake     lerp(shakeAmplitudeMin, shakeAmplitudeMax, intensity) on every CameraShaker; linear distance falloff
    ///             (shakeFalloffDistance) unless the local player is involved (× localPlayerShakeMultiplier instead)
    /// 3 Squash    ball.VisualRoot squashed along the collision normal by lerp(squashMin, squashMax, intensity),
    ///             volume preserving, damped spring back (SquashStretch)
    /// 4 Flash     victim materials flash hitFlashColor for hitFlashDuration (0.05 s) via MaterialPropertyBlocks (HitFlash)
    /// 5 Pulse     ScreenFx.Pulse(Hit | HeavyHit) - post-processing punch through the pipeline adapter
    /// </code>
    /// A perfect catch (<see cref="PlayCatch"/>) runs the same stages with the perfect-catch values
    /// (perfectCatchHitstop, strong shake, perfectCatchFlashColor on the catcher, ScreenFx PerfectCatch); a normal catch
    /// only shakes lightly and squashes the ball.
    /// </para>
    /// <para>
    /// <b>Time.</b> Juice runs on unscaled time: the hitstop freezes gameplay (scaled time) while the shake, squash
    /// spring and flash keep animating, which is exactly what sells the impact. <c>Time.fixedDeltaTime</c> is left
    /// untouched: physics simply advances less while the time scale is low.
    /// </para>
    /// <para>
    /// <b>Robustness.</b> Every consumer is optional: no camera shaker, no HitFlash, no screen-fx driver, no camera rig -
    /// each stage silently skips what is missing. Overlapping hitstops extend to the latest end time (never stack or
    /// shrink); a paused game (time scale 0) is never touched; if another system changes the time scale during a
    /// hitstop, its value wins; the saved time scale is restored on disable/destroy.
    /// </para>
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-900)]
    public sealed class JuiceManager : MonoBehaviour
    {
        public static JuiceManager Instance { get; private set; }

        [Tooltip("Tuning asset. When empty, a default profile with the spec values is created at runtime.")]
        [SerializeField] private JuiceProfile profile;

        /// <summary>Tuning asset. Assigned by GameBootstrap; a default is created when null.</summary>
        public JuiceProfile Profile
        {
            get
            {
                if (profile == null)
                {
                    profile = JuiceProfile.CreateDefault();
                    _ownsProfile = true;
                }
                return profile;
            }
            set
            {
                if (_ownsProfile && profile != null && profile != value) Destroy(profile);
                profile = value;
                _ownsProfile = false;
            }
        }

        /// <summary>True while a hitstop is freezing time.</summary>
        public bool IsHitstopActive { get; private set; }

        /// <summary>Unscaled seconds left in the current hitstop (0 when none).</summary>
        public float HitstopRemaining => IsHitstopActive ? Mathf.Max(0f, _hitstopRemaining) : 0f;

        /// <summary>Every enabled <see cref="CameraShaker"/> (static so registration order does not matter).</summary>
        public static IReadOnlyList<CameraShaker> Shakers => s_shakers;

        // ------------------------------------------------------------------ internal state

        private static readonly List<CameraShaker> s_shakers = new List<CameraShaker>(2);

        /// <summary>
        /// Lowest time scale a hitstop ever writes. A true 0 would be indistinguishable from the pause menu's 0, so a
        /// "full freeze" uses this instead (visually identical: 0.01 % speed).
        /// </summary>
        private const float MinHitstopTimeScale = 0.0001f;

        private bool _ownsProfile;

        // hitstop
        private float _hitstopRemaining;      // unscaled seconds
        private int _hitstopStartFrame = -1;  // the countdown skips this frame (its delta elapsed before the request)
        private float _savedTimeScale = 1f;   // time scale before the first overlapping request
        private float _appliedTimeScale = 1f; // time scale we wrote

        // elimination merging: an elimination that belongs to a hit we just juiced only adds the heavy accent
        private struct JuicedVictim
        {
            public DodgeballPlayer Player;
            public float UnscaledTime;
        }

        private readonly JuicedVictim[] _recentVictims = new JuicedVictim[8];
        private int _recentVictimCursor;

        // Eliminations are handled at LateUpdate so the matching BallHitPlayerEvent (published before OR after it, in
        // the same physics step) has been seen and the two are merged instead of doubled.
        private readonly List<PlayerEliminatedEvent> _pendingEliminations = new List<PlayerEliminatedEvent>(6);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_shakers.Clear();
            Instance = null;
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Juice] A JuiceManager already exists on '{Instance.name}'. Removing the duplicate on '{name}'.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            if (Instance != this) return; // duplicate waiting for destruction: never double-juice

            GameEvents.Subscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Subscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Subscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Subscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Subscribe<BallBouncedEvent>(OnBallBounced);
            GameEvents.Subscribe<BallBlockedEvent>(OnBallBlocked);
            GameEvents.Subscribe<AbilityCastEvent>(OnAbilityCast);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Unsubscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Unsubscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Unsubscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Unsubscribe<BallBouncedEvent>(OnBallBounced);
            GameEvents.Unsubscribe<BallBlockedEvent>(OnBallBlocked);
            GameEvents.Unsubscribe<AbilityCastEvent>(OnAbilityCast);

            // Without Update nobody would end the freeze: restore time now.
            EndHitstop(restoreTimeScale: true);
            _pendingEliminations.Clear();
        }

        private void OnDestroy()
        {
            EndHitstop(restoreTimeScale: true);
            if (Instance == this) Instance = null;
            if (_ownsProfile && profile != null) Destroy(profile);
        }

        private void Update() => TickHitstop();

        private void LateUpdate()
        {
            if (_pendingEliminations.Count > 0) ProcessPendingEliminations();
        }

        // ================================================================== PIPELINES

        /// <summary>Runs the full hit pipeline.</summary>
        public void PlayHit(in HitJuiceRequest request)
        {
            var p = Profile;
            float intensity = p.IntensityForSpeed(request.SpeedKmh);
            bool local = request.LocalPlayerInvolved;

            // 1) Hitstop - the freeze frame. Dynamic 0.03..0.1 s, longer for eliminations (clamped by Hitstop()).
            float hitstop = Mathf.Lerp(p.hitstopMin, p.hitstopMax, intensity);
            if (request.Eliminated) hitstop *= p.eliminationHitstopMultiplier;
            Hitstop(hitstop, p.hitstopTimeScale);

            // 2) Camera shake - Perlin, trauma model. Hits involving the local player are felt fully (and a bit more);
            //    hits elsewhere on the court fade with distance from the camera.
            float amplitude = Mathf.Lerp(p.shakeAmplitudeMin, p.shakeAmplitudeMax, intensity);
            if (local) Shake(amplitude * p.localPlayerShakeMultiplier, p.shakeFrequency, p.shakeDuration);
            else Shake(amplitude, p.shakeFrequency, p.shakeDuration, request.Point);
            if (request.Eliminated) AddTrauma(p.eliminationExtraTrauma * (local ? p.localPlayerShakeMultiplier : 1f), local ? (Vector3?)null : request.Point);

            // 3) Squash & stretch - flatten the ball along the collision normal (volume preserving, springs back).
            if (request.Ball != null && request.Ball.VisualRoot != null)
            {
                Vector3 normal = ResolveNormal(request.Normal, request.Ball, request.Point, request.Victim);
                SquashAndStretch(request.Ball.VisualRoot, normal, Mathf.Lerp(p.squashMin, p.squashMax, intensity), p.squashDuration);
            }

            // 4) Hit flash - 0.05 s white on the victim's materials (unscaled, visible during the freeze).
            if (request.Victim != null) Flash(request.Victim, p.hitFlashColor, p.hitFlashDuration);

            // 5) Screen pulse - chromatic/vignette punch; heavy variant for eliminations.
            float remote = local ? 1f : p.remoteScreenPulseScale;
            if (request.Eliminated)
                ScreenFx.Pulse(ScreenPulse.HeavyHit, p.eliminationScreenPulse * remote, p.screenPulseDuration * 1.4f);
            else
                ScreenFx.Pulse(ScreenPulse.Hit, p.hitScreenPulse * Mathf.Lerp(0.6f, 1f, intensity) * remote, p.screenPulseDuration);

            // 6) Local accent - the victim's own camera "gets hit" (FOV kick outward).
            if (request.Victim != null && request.Victim.IsLocalPlayer) KickFov(p.localHitFovKick * Mathf.Lerp(0.6f, 1f, intensity), p.fovKickDuration);

            RememberVictim(request.Victim);
        }

        /// <summary>Runs the catch pipeline (full pipeline for perfect catches, a light version for normal ones).</summary>
        public void PlayCatch(in CatchJuiceRequest request)
        {
            var p = Profile;
            float intensity = p.IntensityForSpeed(request.SpeedKmh);
            bool local = request.LocalPlayerInvolved;
            Vector3 normal = ResolveNormal(request.Normal, request.Ball, request.Point, request.Catcher);

            if (request.Perfect)
            {
                // 1) The perfect-catch freeze is fixed (and at the top of the range): the moment must read every time.
                Hitstop(p.perfectCatchHitstop, p.hitstopTimeScale);

                // 2) Strong shake.
                if (local) Shake(p.perfectCatchShakeAmplitude * p.localPlayerShakeMultiplier, p.shakeFrequency, p.shakeDuration);
                else Shake(p.perfectCatchShakeAmplitude, p.shakeFrequency, p.shakeDuration, request.Point);

                // 3) The ball smacks into the palms: strong squash even for slower balls.
                if (request.Ball != null && request.Ball.VisualRoot != null)
                    SquashAndStretch(request.Ball.VisualRoot, normal, Mathf.Lerp(p.squashMin, p.squashMax, Mathf.Max(intensity, 0.6f)), p.squashDuration);

                // 4) Golden flash on the catcher.
                if (request.Catcher != null) Flash(request.Catcher, p.perfectCatchFlashColor, p.hitFlashDuration);

                // 5) Bloom/exposure pulse.
                ScreenFx.Pulse(ScreenPulse.PerfectCatch, p.perfectCatchScreenPulse * (local ? 1f : p.remoteScreenPulseScale), p.screenPulseDuration);

                // 6) Local accent: punch-in.
                if (request.Catcher != null && request.Catcher.IsLocalPlayer) KickFov(p.perfectCatchFovKick, p.fovKickDuration);
                return;
            }

            // Normal catch: a light shake and the ball squash only (no freeze, flash or pulse).
            float amplitude = p.normalCatchShakeAmplitude * Mathf.Lerp(0.5f, 1f, intensity);
            if (local) Shake(amplitude * p.localPlayerShakeMultiplier, p.shakeFrequency, p.shakeDuration * 0.7f);
            else Shake(amplitude, p.shakeFrequency, p.shakeDuration * 0.7f, request.Point);

            if (request.Ball != null && request.Ball.VisualRoot != null)
                SquashAndStretch(request.Ball.VisualRoot, normal, Mathf.Lerp(p.squashMin, p.squashMax, intensity) * p.normalCatchSquashScale, p.squashDuration);
        }

        // ================================================================== STAGES

        /// <summary>
        /// Frame freeze: sets Time.timeScale to <paramref name="timeScale"/> for <paramref name="duration"/> unscaled seconds
        /// (clamped to 0.03-0.1 s unless <paramref name="allowLong"/>), then restores the previous time scale. Overlapping
        /// requests extend rather than stack.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>The time scale in force before the first of several overlapping requests is remembered and restored.</item>
        /// <item>An overlapping request extends the freeze to whichever end time is later (never adds, never shortens);
        /// the deeper (lower) time scale wins.</item>
        /// <item>If the game is paused (time scale already 0) nothing happens.</item>
        /// <item>The countdown is unscaled (Update + Time.unscaledDeltaTime), starting on the frame after the request.</item>
        /// <item>If the pause menu sets 0 during a hitstop, the countdown holds until unpaused; if any other system sets
        /// a different time scale, that system wins and nothing is restored.</item>
        /// <item><c>Time.fixedDeltaTime</c> is not scaled: physics just advances less while frozen.</item>
        /// <item>Publishes <see cref="HitstopEvent"/> when a freeze starts or is extended.</item>
        /// </list>
        /// </remarks>
        public void Hitstop(float duration, float timeScale = 0f, bool allowLong = false)
        {
            if (!isActiveAndEnabled) return; // we could not guarantee the restore without Update
            if (float.IsNaN(duration) || duration <= 0f) return;

            duration = allowLong ? duration : Mathf.Clamp(duration, GameConstants.HitstopMin, GameConstants.HitstopMax);
            float scale = Mathf.Clamp(float.IsNaN(timeScale) ? 0f : timeScale, MinHitstopTimeScale, 1f);

            if (!IsHitstopActive)
            {
                float current = Time.timeScale;
                if (current <= 0f) return; // paused: never fight the pause menu

                _savedTimeScale = current;
                _appliedTimeScale = Mathf.Min(scale, current); // never speed up an existing slow motion
                Time.timeScale = _appliedTimeScale;
                _hitstopRemaining = duration;
                _hitstopStartFrame = Time.frameCount;
                IsHitstopActive = true;
            }
            else
            {
                bool extended = false;
                if (duration > _hitstopRemaining)
                {
                    _hitstopRemaining = duration;       // extend to the later end time
                    _hitstopStartFrame = Time.frameCount;
                    extended = true;
                }
                if (scale < _appliedTimeScale && IsOurTimeScale())
                {
                    _appliedTimeScale = scale;          // the deeper freeze wins
                    Time.timeScale = _appliedTimeScale;
                    extended = true;
                }
                if (!extended) return;
            }

            GameEvents.Publish(new HitstopEvent { Duration = _hitstopRemaining, TimeScale = _appliedTimeScale });
        }

        /// <summary>Ends a running hitstop now and restores the time scale.</summary>
        public void CancelHitstop() => EndHitstop(restoreTimeScale: true);

        /// <summary>
        /// Procedural Perlin-noise camera shake on every registered <see cref="CameraShaker"/>. Amplitude falls off with
        /// distance from <paramref name="worldSource"/> when given.
        /// </summary>
        /// <param name="amplitude">Trauma-equivalent strength (0..1, may exceed 1 for local emphasis; the shaker clamps).</param>
        /// <param name="frequency">Noise frequency (Hz); &lt;= 0 keeps each shaker's own frequency.</param>
        /// <param name="duration">Unscaled seconds over which the shake fades out.</param>
        /// <param name="worldSource">Where it happened. Null = felt at full strength (local player involved).</param>
        public void Shake(float amplitude, float frequency, float duration, Vector3? worldSource = null)
        {
            if (amplitude <= 0f || duration <= 0f) return;
            for (int i = s_shakers.Count - 1; i >= 0; i--)
            {
                var shaker = s_shakers[i];
                if (shaker == null)
                {
                    s_shakers.RemoveAt(i);
                    continue;
                }
                float attenuation = Attenuation(shaker, worldSource);
                if (attenuation <= 0.001f) continue;
                shaker.Shake(amplitude * attenuation, frequency, duration);
            }
        }

        /// <summary>Adds trauma (0..1) to the shakers (trauma-squared shake model).</summary>
        public void AddTrauma(float trauma, Vector3? worldSource = null)
        {
            if (trauma == 0f || float.IsNaN(trauma)) return;
            for (int i = s_shakers.Count - 1; i >= 0; i--)
            {
                var shaker = s_shakers[i];
                if (shaker == null)
                {
                    s_shakers.RemoveAt(i);
                    continue;
                }
                float attenuation = Attenuation(shaker, worldSource);
                if (attenuation <= 0.001f) continue;
                shaker.AddTrauma(trauma * attenuation);
            }
        }

        /// <summary>Squash along <paramref name="normal"/> and stretch perpendicular (volume-preserving), springing back.</summary>
        /// <param name="target">A purely visual transform (e.g. <see cref="DodgeBall.VisualRoot"/>), never a physics body.</param>
        /// <param name="normal">World-space collision normal.</param>
        /// <param name="intensity">Squash fraction (0.3 = 30 % flatter along the normal).</param>
        /// <param name="duration">Unscaled seconds of the spring back.</param>
        public void SquashAndStretch(Transform target, Vector3 normal, float intensity, float duration = 0.18f)
        {
            if (target == null || intensity <= 0f) return;
            var squash = target.GetComponent<SquashStretch>();
            if (squash == null) squash = target.gameObject.AddComponent<SquashStretch>();
            squash.Impact(normal, intensity, duration);
        }

        /// <summary>0.05 s (default) white flash on the player's materials.</summary>
        public void Flash(DodgeballPlayer player, Color? color = null, float duration = Core.GameConstants.HitFlashDuration)
        {
            if (player == null) return;
            var flash = ResolveHitFlash(player);
            if (flash != null) flash.Flash(color ?? Profile.hitFlashColor, duration);
        }

        public void RegisterShaker(CameraShaker shaker) => AddShaker(shaker);
        public void UnregisterShaker(CameraShaker shaker) => RemoveShaker(shaker);

        /// <summary>Static registration used by <see cref="CameraShaker"/> (works before the manager exists).</summary>
        public static void AddShaker(CameraShaker shaker)
        {
            if (shaker != null && !s_shakers.Contains(shaker)) s_shakers.Add(shaker);
        }

        /// <summary>Static unregistration used by <see cref="CameraShaker"/>.</summary>
        public static void RemoveShaker(CameraShaker shaker) => s_shakers.Remove(shaker);

        // ================================================================== EVENT OBSERVERS

        private void OnBallHitPlayer(BallHitPlayerEvent e)
        {
            // Ignored = the ball could not hit this player at all (teammate, pass...): nothing happened.
            if (e.Outcome == HitOutcome.Ignored) return;

            var request = new HitJuiceRequest
            {
                Point = e.Point,
                Normal = e.Normal,
                SpeedKmh = e.SpeedKmh > 0f ? e.SpeedKmh : e.BallVelocity.magnitude * GameConstants.MsToKmh,
                Victim = e.Victim,
                Ball = e.Ball,
                Eliminated = e.Outcome == HitOutcome.Eliminated,
                LocalPlayerInvolved = e.IsLocalPlayerInvolved || IsLocal(e.Victim) || IsLocal(e.Attacker),
            };

            if (e.Outcome == HitOutcome.Negated)
            {
                // Invulnerability / evasion / personal shield: the ball glances off. Light feedback only - a freeze or a
                // flash would falsely tell the player they were hit.
                PlayNegated(in request);
                return;
            }

            // Damaged, Eliminated, EliminationDelayed (Chrono) and EliminationPrevented (Specter) all "connect".
            PlayHit(in request);
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (e.Quality == CatchQuality.Miss) return;

            var request = new CatchJuiceRequest
            {
                Point = e.Point,
                Normal = Vector3.zero, // resolved from the ball's travel direction in PlayCatch
                SpeedKmh = e.SpeedKmh,
                Catcher = e.Catcher,
                Ball = e.Ball,
                Perfect = e.Quality == CatchQuality.Perfect,
                LocalPlayerInvolved = e.IsLocalPlayerInvolved || IsLocal(e.Catcher) || IsLocal(e.Thrower),
            };
            PlayCatch(in request);
        }

        private void OnPlayerEliminated(PlayerEliminatedEvent e)
        {
            if (e.Player == null || e.Cause == EliminationCause.Forfeit) return;
            for (int i = 0; i < _pendingEliminations.Count; i++)
                if (_pendingEliminations[i].Player == e.Player) return; // already queued this frame
            _pendingEliminations.Add(e);
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            if (e.Ball == null) return;

            // Make sure every thrown ball can stretch along its velocity while flying, even if the Combat module did not
            // add the component itself (GetComponent is allocation-free; AddComponent happens once per ball).
            var visual = e.Ball.VisualRoot;
            if (visual != null && visual.GetComponent<SquashStretch>() == null) visual.gameObject.AddComponent<SquashStretch>();

            if (e.IsPass || !IsLocal(e.Thrower)) return;

            // Local accent: a hard throw kicks the camera slightly (release recoil).
            var p = Profile;
            float intensity = p.IntensityForSpeed(e.SpeedKmh);
            AddTrauma(p.localThrowTrauma * intensity);
            KickFov(p.localThrowFovKick * intensity, p.fovKickDuration);
        }

        private void OnBallBounced(BallBouncedEvent e)
        {
            // Spec: squash & stretch on ball impacts along collision normals - court bounces included (no freeze/shake).
            if (e.Ball == null || e.Ball.VisualRoot == null) return;
            var p = Profile;
            float speed = e.ImpactSpeed; // m/s
            if (speed < p.minBounceSpeed) return;

            float t = Mathf.InverseLerp(p.minBounceSpeed, GameConstants.MaxBallSpeedMs, speed);
            float squash = Mathf.Lerp(0f, p.squashMax, t) * p.bounceSquashScale;
            SquashAndStretch(e.Ball.VisualRoot, e.Normal, squash, p.squashDuration);
        }

        private void OnBallBlocked(BallBlockedEvent e)
        {
            // Shields, clones, turrets: the ball smacks into something solid.
            if (e.Ball == null) return;
            var p = Profile;
            float intensity = p.IntensityForSpeed(e.Ball.SpeedKmh);

            if (e.Ball.VisualRoot != null)
            {
                Vector3 normal = ResolveNormal(e.Normal, e.Ball, e.Point, null);
                SquashAndStretch(e.Ball.VisualRoot, normal, Mathf.Lerp(p.squashMin, p.squashMax, intensity), p.squashDuration);
            }

            bool local = IsLocal(e.Ball.LastThrower);
            float amplitude = p.blockedShakeAmplitude * Mathf.Lerp(0.5f, 1f, intensity);
            if (local) Shake(amplitude, p.shakeFrequency, p.shakeDuration * 0.8f);
            else Shake(amplitude, p.shakeFrequency, p.shakeDuration * 0.8f, e.Point);
        }

        private void OnAbilityCast(AbilityCastEvent e)
        {
            // Ultimates are the biggest moments of a match: give the caster's own screen a punch.
            if (e.Slot != AbilitySlot.Ultimate || !IsLocal(e.Player)) return;
            var p = Profile;
            ScreenFx.Pulse(ScreenPulse.UltimateCast, p.ultimateCastScreenPulse, p.screenPulseDuration * 1.8f);
            AddTrauma(p.ultimateCastTrauma);
        }

        // ================================================================== helpers

        /// <summary>Negated hit: small shake and squash, scaled by <see cref="JuiceProfile.negatedHitScale"/>.</summary>
        private void PlayNegated(in HitJuiceRequest request)
        {
            var p = Profile;
            if (p.negatedHitScale <= 0f) return;
            float intensity = p.IntensityForSpeed(request.SpeedKmh);
            float scale = p.negatedHitScale;

            float amplitude = Mathf.Lerp(p.shakeAmplitudeMin, p.shakeAmplitudeMax, intensity) * scale;
            if (request.LocalPlayerInvolved) Shake(amplitude * p.localPlayerShakeMultiplier, p.shakeFrequency, p.shakeDuration * 0.7f);
            else Shake(amplitude, p.shakeFrequency, p.shakeDuration * 0.7f, request.Point);

            if (request.Ball != null && request.Ball.VisualRoot != null)
            {
                Vector3 normal = ResolveNormal(request.Normal, request.Ball, request.Point, request.Victim);
                SquashAndStretch(request.Ball.VisualRoot, normal, Mathf.Lerp(p.squashMin, p.squashMax, intensity) * scale, p.squashDuration);
            }
        }

        /// <summary>
        /// Handles queued eliminations. If the victim was juiced by a ball hit moments ago, only the extra heavy accent
        /// is added (heavy pulse + trauma); otherwise the elimination came from elsewhere (Chrono's delayed impact
        /// resolving, Gouki's tackle, a frozen shatter, an ability) and gets its own heavy pipeline.
        /// </summary>
        private void ProcessPendingEliminations()
        {
            var p = Profile;
            for (int i = 0; i < _pendingEliminations.Count; i++)
            {
                var e = _pendingEliminations[i];
                if (e.Player == null) continue;

                bool local = IsLocal(e.Player) || IsLocal(e.Attacker);
                Vector3 point = e.Point != Vector3.zero ? e.Point : e.Player.ChestPosition;
                float remote = local ? 1f : p.remoteScreenPulseScale;

                if (WasRecentlyJuiced(e.Player, p.eliminationMergeWindow))
                {
                    // Extra heavy pulse on top of the hit pipeline that already ran for this elimination.
                    ScreenFx.Pulse(ScreenPulse.HeavyHit, p.eliminationScreenPulse * remote, p.screenPulseDuration * 1.6f);
                    continue;
                }

                // Stand-alone elimination.
                if (e.Cause != EliminationCause.OutOfBounds)
                    Hitstop(p.hitstopMax * p.eliminationHitstopMultiplier, p.hitstopTimeScale);

                if (local) Shake(p.eliminationShakeAmplitude * p.localPlayerShakeMultiplier, p.shakeFrequency, p.shakeDuration);
                else Shake(p.eliminationShakeAmplitude, p.shakeFrequency, p.shakeDuration, point);

                Flash(e.Player, p.hitFlashColor, p.hitFlashDuration);
                ScreenFx.Pulse(ScreenPulse.HeavyHit, p.eliminationScreenPulse * remote, p.screenPulseDuration * 1.6f);
                if (e.Player.IsLocalPlayer) KickFov(p.localHitFovKick, p.fovKickDuration);

                RememberVictim(e.Player);
            }
            _pendingEliminations.Clear();
        }

        private void TickHitstop()
        {
            if (!IsHitstopActive) return;

            if (!IsOurTimeScale())
            {
                // Paused by the pause menu: hold the remaining freeze until the game is unpaused (the menu restores our
                // value, the countdown resumes).
                if (Time.timeScale <= 0f) return;

                // Another system took control of time (slow-motion ability, match end...): its value wins.
                EndHitstop(restoreTimeScale: false);
                return;
            }

            if (Time.frameCount == _hitstopStartFrame) return; // this frame's delta elapsed before the request

            _hitstopRemaining -= Time.unscaledDeltaTime;
            if (_hitstopRemaining <= 0f) EndHitstop(restoreTimeScale: true);
        }

        private void EndHitstop(bool restoreTimeScale)
        {
            if (!IsHitstopActive) return;
            IsHitstopActive = false;
            _hitstopRemaining = 0f;
            // Only restore if the time scale is still the one we wrote (never override a pause or another system).
            if (restoreTimeScale && IsOurTimeScale()) Time.timeScale = _savedTimeScale;
        }

        private bool IsOurTimeScale() => Mathf.Abs(Time.timeScale - _appliedTimeScale) <= 1e-6f;

        /// <summary>1 without a source; otherwise linear falloff from the shaker (camera) to the source.</summary>
        private float Attenuation(CameraShaker shaker, Vector3? worldSource)
        {
            if (!worldSource.HasValue) return 1f;
            float falloff = Profile.shakeFalloffDistance;
            if (falloff <= 0f) return 1f;
            float distance = Vector3.Distance(shaker.transform.position, worldSource.Value);
            return 1f - Mathf.Clamp01(distance / falloff);
        }

        /// <summary>
        /// A usable squash axis: the given normal, else the ball's travel direction, else the direction from the player's
        /// chest to the contact point, else up.
        /// </summary>
        private static Vector3 ResolveNormal(Vector3 normal, DodgeBall ball, Vector3 point, DodgeballPlayer player)
        {
            if (normal.sqrMagnitude > 1e-6f) return normal.normalized;
            if (ball != null)
            {
                Vector3 v = ball.Velocity;
                if (v.sqrMagnitude > 0.01f) return -v.normalized;
                if (ball.LastThrower != null)
                {
                    Vector3 fromThrower = point - ball.LastThrower.ChestPosition;
                    if (fromThrower.sqrMagnitude > 1e-4f) return -fromThrower.normalized;
                }
            }
            if (player != null)
            {
                Vector3 fromChest = point - player.ChestPosition;
                if (fromChest.sqrMagnitude > 1e-4f) return fromChest.normalized;
            }
            return Vector3.up;
        }

        /// <summary>
        /// The player's HitFlash: the one built by CharacterVisual, else any in the hierarchy, else one added on the
        /// visual (initialised with its renderers) so the flash always happens.
        /// </summary>
        private static HitFlash ResolveHitFlash(DodgeballPlayer player)
        {
            var visual = player.Visual;
            if (visual != null && visual.HitFlash != null) return visual.HitFlash;

            var existing = player.GetComponentInChildren<HitFlash>(true);
            if (existing != null) return existing;

            var host = visual != null ? visual.gameObject : player.gameObject;
            var created = host.AddComponent<HitFlash>();
            if (visual != null && visual.Renderers != null && visual.Renderers.Length > 0) created.Initialize(visual.Renderers);
            return created; // otherwise it gathers its renderers lazily on the first flash
        }

        private static void KickFov(float degrees, float duration)
        {
            if (Mathf.Abs(degrees) < 0.01f) return;
            var rig = ThirdPersonCameraRig.Instance;
            if (rig != null) rig.AddFovKick(degrees, duration);
        }

        private static bool IsLocal(DodgeballPlayer player) => player != null && player.IsLocalPlayer;

        private void RememberVictim(DodgeballPlayer player)
        {
            if (player == null) return;
            _recentVictims[_recentVictimCursor] = new JuicedVictim { Player = player, UnscaledTime = Time.unscaledTime };
            _recentVictimCursor = (_recentVictimCursor + 1) % _recentVictims.Length;
        }

        private bool WasRecentlyJuiced(DodgeballPlayer player, float window)
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < _recentVictims.Length; i++)
            {
                var v = _recentVictims[i];
                if (v.Player == player && now - v.UnscaledTime <= window) return true;
            }
            return false;
        }
    }
}
