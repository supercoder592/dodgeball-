using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Bear - ULTIMATE [Aegis Barrier] (神盾屏障): raises a large energy shield at centre court that blocks every incoming
    /// opponent throw for 6 s.
    /// <para>
    /// OnCast spawns a <see cref="BearAegisBarrierWall"/> just behind the centre line on Bear's side, spanning the whole
    /// court width (plus a small overhang) and ~3.2 m tall, facing the enemy half. The wall is an
    /// <see cref="Combat.IBallHittable"/> on the Hittable layer: enemy balls are Blocked (the ball reflects and publishes
    /// <see cref="BallBlockedEvent"/>), Bear's team's throws pass through, unblockable balls are left to the ball.
    /// </para>
    /// <para>
    /// The wall is a deployed structure with its own clock: when the ability ends normally the wall flickers out; a stun
    /// or freeze on Bear does not collapse it, and by default neither does Bear's elimination
    /// (<see cref="persistsIfBearEliminated"/>). Round end/reset, hero swap and cancellation remove it immediately.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class BearAegisBarrier : AbilityBase
    {
        [Header("Placement")]
        [Tooltip("Distance (m) behind the centre line, on Bear's side, where the wall stands.")]
        [Range(0f, 4f)] public float offsetFromCentreLine = 0.6f;

        [Tooltip("Extra width (m) past each sideline so sideline throws cannot sneak around it.")]
        [Range(0f, 3f)] public float sidelineOverhang = 0.5f;

        [Tooltip("Wall width (m) when no Court exists (falls back to a wall in front of Bear).")]
        [Range(2f, 20f)] public float fallbackWidth = 10f;

        [Tooltip("Distance (m) in front of Bear used when no Court exists.")]
        [Range(0.5f, 8f)] public float fallbackDistance = 3f;

        [Header("Barrier")]
        [Tooltip("Wall height (m). Spec: large shield, ~3.2 m.")]
        [Range(1f, 6f)] public float height = 3.2f;

        [Tooltip("Thickness (m) of the blocking volume (thicker = fewer grazing misses).")]
        [Range(0.05f, 1.5f)] public float thickness = 0.35f;

        [Tooltip("Barrier duration used only when the AbilityData has no duration (spec: 6 s).")]
        [Min(0.5f)] public float fallbackDuration = 6f;

        [Tooltip("Keep the barrier up for its full duration even if Bear is eliminated while it stands.")]
        public bool persistsIfBearEliminated = true;

        [Header("Look")]
        [Tooltip("Energy colour (rgb) and base opacity (a). Pale cyan-blue reads as projected energy, not cartoon.")]
        public Color energyTint = new Color(0.42f, 0.78f, 1f, 0.26f);

        [Tooltip("Brightness multiplier of the energy sheet.")]
        [Range(0.1f, 4f)] public float intensity = 1.2f;

        [Tooltip("Hexagon size (m, circumradius) of the energy lattice.")]
        [Range(0.05f, 1f)] public float hexSize = 0.22f;

        [Tooltip("Upward drift of the lattice (m/s).")]
        [Range(0f, 1f)] public float latticeScrollSpeed = 0.12f;

        [Tooltip("Seconds the sheet takes to rise from the emitter rail.")]
        [Range(0.05f, 1.5f)] public float growTime = 0.35f;

        [Tooltip("Seconds the sheet takes to flicker out.")]
        [Range(0.05f, 1.5f)] public float fadeTime = 0.45f;

        [Tooltip("Lifetime (s) of an impact ripple.")]
        [Range(0.1f, 2f)] public float rippleDuration = 0.55f;

        [Tooltip("Radius (m) an impact ripple spreads to.")]
        [Range(0.2f, 4f)] public float rippleRadius = 1.6f;

        [Tooltip("Out-of-plane bulge (m) of an impact ripple.")]
        [Range(0f, 0.2f)] public float rippleAmplitude = 0.05f;

        [Tooltip("Extra brightness on impact (flicker).")]
        [Range(0f, 3f)] public float hitFlashBoost = 0.9f;

        [Tooltip("HDR colour of the emitter light strips.")]
        [ColorUsage(false, true)] public Color emitterTint = new Color(0.5f, 0.85f, 1f, 1f) * 3.5f;

        [Header("Deploy feedback")]
        [Tooltip("Camera trauma added when the wall slams up (0..1).")]
        [Range(0f, 1f)] public float deployTrauma = 0.2f;

        [NonSerialized] private BearAegisBarrierWall _wall;
        [NonSerialized] private float _manualRemaining = -1f;
        [NonSerialized] private InterruptReason _interruptReason;
        [NonSerialized] private bool _hasInterruptReason;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public BearAegisBarrier() { }

        /// <summary>The standing wall, or null.</summary>
        public BearAegisBarrierWall ActiveWall => _wall;

        protected override void OnEquip()
        {
            Listen<RoundEndedEvent>(_ => DismissWall(true));
            Listen<RoundStartedEvent>(_ => DismissWall(true));
        }

        protected override void OnUnequip() => DismissWall(true);

        protected override void OnCast()
        {
            DismissWall(true);
            _hasInterruptReason = false;

            float lifetime = Duration > 0f ? Duration : fallbackDuration;
            if (Duration <= 0f)
            {
                HoldActive();
                _manualRemaining = fallbackDuration;
            }

            ComputePlacement(out var floorCentre, out var rotation, out float width);

            var settings = BearAegisBarrierSettings.Default;
            settings.Width = width;
            settings.Height = height;
            settings.Thickness = thickness;
            settings.Lifetime = lifetime + 0.05f; // the ability's end dismisses it; this is the fallback clock
            settings.GrowTime = growTime;
            settings.FadeTime = fadeTime;
            settings.EnergyTint = energyTint;
            settings.Intensity = intensity;
            settings.HexSize = hexSize;
            settings.ScrollSpeed = latticeScrollSpeed;
            settings.RippleDuration = rippleDuration;
            settings.RippleMaxRadius = rippleRadius;
            settings.RippleAmplitude = rippleAmplitude;
            settings.HitFlashBoost = hitFlashBoost;
            settings.EmitterTint = emitterTint;
            _wall = BearAegisBarrierWall.Create(Owner, floorCentre, rotation, settings);

            // Deploy feedback: energy crackles along the rail, a deep shield hum, a small camera kick.
            Vector3 right = rotation * Vector3.right;
            Vector3 up = Vector3.up * 0.3f;
            var faceEnemy = rotation;
            VfxManager.Spawn(VfxId.ShieldImpact, floorCentre + up, faceEnemy, 1.1f, energyTint);
            VfxManager.Spawn(VfxId.ShieldImpact, floorCentre + up + right * (width * 0.33f), faceEnemy, 0.8f, energyTint);
            VfxManager.Spawn(VfxId.ShieldImpact, floorCentre + up - right * (width * 0.33f), faceEnemy, 0.8f, energyTint);
            AudioManager.PlayAt(SfxId.Shield, floorCentre + Vector3.up * (height * 0.5f), 1f, 0.8f);
            if (JuiceManager.Instance != null && deployTrauma > 0f) JuiceManager.Instance.AddTrauma(deployTrauma, floorCentre);
        }

        protected override void OnTick(float deltaTime)
        {
            if (_wall == null || !_wall.IsBlocking)
            {
                EndAbility();
                return;
            }
            if (_manualRemaining > 0f)
            {
                _manualRemaining -= deltaTime;
                if (_manualRemaining <= 0f) EndAbility();
            }
        }

        protected override void OnInterrupt(InterruptReason reason)
        {
            _interruptReason = reason;
            _hasInterruptReason = true;
        }

        protected override void OnEnd(bool interrupted)
        {
            _manualRemaining = -1f;
            if (!interrupted || !_hasInterruptReason)
            {
                DismissWall(false);
                return;
            }

            switch (_interruptReason)
            {
                case InterruptReason.RoundEnded:
                case InterruptReason.Replaced:
                case InterruptReason.Cancelled:
                    DismissWall(true);
                    break;
                case InterruptReason.Eliminated:
                    if (!persistsIfBearEliminated) DismissWall(false);
                    else _wall = null; // the wall keeps standing on its own clock
                    break;
                default:
                    // Stunned / Frozen: a deployed structure does not collapse because its owner got rattled.
                    _wall = null;
                    break;
            }
            _hasInterruptReason = false;
        }

        protected override void OnRoundReset() => DismissWall(true);

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null) return 0f;
            float w = Data.aiWeight;

            // Count loaded enemy infielders: the more balls the enemy holds, the more a wall is worth.
            int armedEnemies = 0;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p) || !p.IsInfield || p.Combat == null) continue;
                if (p.Combat.HasBall) armedEnemies++;
            }

            float score = 0f;
            if (armedEnemies >= 2) score = w;
            else if (armedEnemies == 1 && ctx.AlliesInfield <= ctx.EnemiesInfield) score = w * 0.6f;
            if (ctx.AlliesInfield < ctx.EnemiesInfield) score = Mathf.Max(score, w * 0.8f);
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.5f) score = Mathf.Max(score, w * 0.5f);
            return Mathf.Clamp01(score);
        }

        // ------------------------------------------------------------------ helpers

        private void ComputePlacement(out Vector3 floorCentre, out Quaternion rotation, out float width)
        {
            var court = Court.Instance;
            if (court != null && Owner.Team.IsValid())
            {
                Vector3 attack = court.AttackDirection(Owner.Team);
                attack.y = 0f;
                if (attack.sqrMagnitude < 1e-4f) attack = Owner.Forward;
                attack.Normalize();
                floorCentre = court.Center - attack * offsetFromCentreLine;
                floorCentre.y = court.FloorY;
                rotation = Quaternion.LookRotation(attack, Vector3.up);
                width = court.width + 2f * sidelineOverhang;
                return;
            }

            Vector3 forward = Owner.Forward;
            floorCentre = AbilityUtil.GroundPoint(Owner.Position + forward * fallbackDistance);
            rotation = Quaternion.LookRotation(forward, Vector3.up);
            width = fallbackWidth;
        }

        private void DismissWall(bool immediate)
        {
            _manualRemaining = -1f;
            if (_wall != null) _wall.Dismiss(immediate);
            _wall = null;
        }
    }
}
