using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Chrono - ULTIMATE [Temporal Reset] (時間重置): rewinds a targeted 5 × 5 m zone's ball trajectories and player
    /// positions back 3 s.
    /// <para>
    /// The zone is centred on Chrono's aim point (bots: on the most dangerous enemy ball heading at their team, when
    /// <see cref="botsAutoTarget"/>), limited to <see cref="maxCastRange"/> from Chrono and clamped to the playing area
    /// (both halves and both outfield strips), aligned with the court lines. Every player and ball whose CURRENT position
    /// lies inside the zone is restored from its <see cref="TimeRewindRecorder"/> snapshot of
    /// <see cref="rewindSeconds"/> ago (entities without history are skipped):
    /// </para>
    /// <list type="bullet">
    /// <item>Players teleport to their recorded position (clamped to their current zone, so nobody crosses the centre line
    /// or leaves the outfield) and orientation, optionally with their recorded momentum.</item>
    /// <item>Live balls get their recorded position and velocity. When the recorded moment predates the current throw, the
    /// throw is undone: a match ball returns to where it was as a free ball, an ability projectile is erased.</item>
    /// <item>Free / stasis balls teleport to their recorded position.</item>
    /// </list>
    /// Everything is gathered first and applied afterwards, so rewinds cannot cascade (a teleported entity is never
    /// re-evaluated) and pooled balls recycled during the rewind do not disturb the iteration. Presentation: TemporalZone
    /// VFX over the area, RewindTrail at each rewound entity's old and new position, a TimeRewind screen pulse and the
    /// rewind SFX.
    /// </summary>
    [Serializable]
    public sealed class ChronoTemporalReset : AbilityBase
    {
        [Header("Zone")]
        [Tooltip("Side length (m) of the square zone. Spec: 5 m.")]
        [Range(1f, 15f)] public float zoneSize = 5f;

        [Tooltip("Height (m) of the zone above the floor (catches high lobs).")]
        [Range(1f, 12f)] public float zoneHeight = 6f;

        [Tooltip("Maximum distance (m) from Chrono to the zone centre.")]
        [Range(3f, 40f)] public float maxCastRange = 25f;

        [Tooltip("How far back in time (s) the zone is rewound. Spec: 3 s.")]
        [Range(0.5f, 6f)] public float rewindSeconds = 3f;

        [Header("What is rewound")]
        [Tooltip("Rewind enemy players inside the zone.")]
        public bool affectEnemies = true;

        [Tooltip("Rewind allied players inside the zone.")]
        public bool affectAllies = true;

        [Tooltip("Rewind Chrono if standing inside the zone.")]
        public bool affectSelf = true;

        [Tooltip("Rewind balls inside the zone.")]
        public bool affectBalls = true;

        [Tooltip("Restore the recorded planar velocity of rewound players (momentum continuity).")]
        public bool restorePlayerVelocity = true;

        [Header("Targeting")]
        [Tooltip("Bots centre the zone on the most dangerous enemy ball heading at their team instead of their aim point.")]
        public bool botsAutoTarget = true;

        [Tooltip("Look-ahead (s) used to decide whether an enemy ball is heading toward a teammate.")]
        [Range(0.2f, 3f)] public float threatLookAhead = 1.5f;

        [Tooltip("Distance (m) from a teammate's body within which a predicted flight counts as a threat.")]
        [Range(0.3f, 4f)] public float threatRadius = 1.4f;

        [Header("Presentation")]
        [Tooltip("Tint of the zone and rewind trails (clockwork gold).")]
        public Color zoneTint = new Color(0.95f, 0.78f, 0.38f, 1f);

        [Tooltip("TimeRewind screen pulse intensity (the whole arena sees time snap back).")]
        [Range(0f, 1f)] public float pulseIntensity = 0.85f;

        [Tooltip("Screen pulse duration (s, unscaled).")]
        [Range(0.1f, 2f)] public float pulseDuration = 0.7f;

        [Tooltip("Maximum number of per-entity RewindTrail bursts (keeps a crowded zone readable).")]
        [Range(0, 32)] public int maxTrailBursts = 16;

        [Header("AI")]
        [Tooltip("Bots only rewind balls that will reach a teammate in at least this many seconds.")]
        [Range(0f, 1f)] public float aiMinTimeToThreat = 0.15f;

        // ------------------------------------------------------------------ runtime state (created per clone)
        [NonSerialized] private List<DodgeballPlayer> _players;
        [NonSerialized] private List<DodgeBall> _balls;
        [NonSerialized] private Vector3 _lastCenter;
        [NonSerialized] private int _lastRewoundCount;

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public ChronoTemporalReset() { }

        /// <summary>Centre of the most recent zone (HUD / debug gizmos).</summary>
        public Vector3 LastZoneCenter => _lastCenter;

        /// <summary>Players + balls rewound by the most recent cast.</summary>
        public int LastRewoundCount => _lastRewoundCount;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnInitialize()
        {
            _players = new List<DodgeballPlayer>(6);
            _balls = new List<DodgeBall>(8);
        }

        protected override void OnCast()
        {
            Vector3 center = ResolveZoneCenter();
            Quaternion zoneRotation = ChronoTimeUtil.CourtYaw();
            _lastCenter = center;

            GatherTargets(center, zoneRotation);

            // Zone presentation first so the flash reads as the cause of the snap-back.
            VfxManager.Spawn(VfxId.TemporalZone, center, zoneRotation, zoneSize / 5f, zoneTint);
            AudioManager.PlayAt(SfxId.Rewind, center, 1f, 0.9f);
            if (pulseIntensity > 0f) ScreenFx.Pulse(ScreenPulse.TimeRewind, pulseIntensity, pulseDuration);

            int bursts = 0;
            int rewound = 0;

            for (int i = 0; i < _players.Count; i++)
            {
                var result = ChronoTimeUtil.TryRewindPlayer(_players[i], rewindSeconds, restorePlayerVelocity, out Vector3 from, out Vector3 to);
                if (result == ChronoRewindResult.Skipped) continue;
                rewound++;
                SpawnTrail(from + Vector3.up * 0.9f, to + Vector3.up * 0.9f, ref bursts);
            }

            for (int i = 0; i < _balls.Count; i++)
            {
                var result = ChronoTimeUtil.TryRewindBall(_balls[i], rewindSeconds, out Vector3 from, out Vector3 to);
                if (result == ChronoRewindResult.Skipped) continue;
                rewound++;
                SpawnTrail(from, to, ref bursts);
            }

            _lastRewoundCount = rewound;
            _players.Clear();
            _balls.Clear();
        }

        protected override void OnUnequip()
        {
            _players?.Clear();
            _balls?.Clear();
        }

        // ------------------------------------------------------------------ AI

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || Owner == null) return 0f;

            // Defensive use: an enemy ball is about to reach a teammate - rewinding it undoes the throw.
            var ball = ChronoTimeUtil.FindMostThreateningEnemyBall(Owner, Owner.ChestPosition, maxCastRange, threatLookAhead,
                threatRadius, aiMinTimeToThreat, out float timeToThreat);
            if (ball != null)
            {
                float urgency = Mathf.Clamp01(1f - timeToThreat / Mathf.Max(0.1f, threatLookAhead));
                // Slightly lower than the Stasis Field so the skill is preferred when both are available.
                return Mathf.Clamp01(Data.aiWeight * (0.55f + 0.35f * urgency));
            }
            return 0f;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Aim point (or bot auto-target) limited to the cast range and clamped to the playing area, on the floor.</summary>
        private Vector3 ResolveZoneCenter()
        {
            Vector3 origin = Owner.Position;
            Vector3 center;

            DodgeBall autoTarget = null;
            if (botsAutoTarget && !Owner.IsHumanControlled)
            {
                autoTarget = ChronoTimeUtil.FindMostThreateningEnemyBall(Owner, Owner.ChestPosition, maxCastRange, threatLookAhead,
                    threatRadius, 0f, out _);
            }

            if (autoTarget != null)
            {
                center = autoTarget.transform.position;
            }
            else
            {
                var intent = Owner.Intent;
                center = intent.AimPoint;
                bool invalid = float.IsNaN(center.x) || float.IsInfinity(center.x) || (center - origin).sqrMagnitude < 1e-4f;
                if (invalid)
                {
                    Vector3 aim = intent.AimDirection.sqrMagnitude > 1e-4f ? intent.AimDirection : Owner.Forward;
                    aim.y = 0f;
                    center = origin + (aim.sqrMagnitude > 1e-4f ? aim.normalized : Owner.Forward) * 8f;
                }
            }

            // Limit the planar distance from Chrono.
            Vector3 planar = center - origin;
            planar.y = 0f;
            if (planar.magnitude > maxCastRange) planar = planar.normalized * maxCastRange;
            center = origin + planar;

            // Clamp to the playing area (both halves + outfield strips).
            Bounds area = ChronoTimeUtil.GetPlayAreaBounds();
            center.x = Mathf.Clamp(center.x, area.min.x, area.max.x);
            center.z = Mathf.Clamp(center.z, area.min.z, area.max.z);

            return AbilityUtil.GroundPoint(center);
        }

        private void GatherTargets(Vector3 center, Quaternion zoneRotation)
        {
            _players.Clear();
            _balls.Clear();

            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsInitialized || p.Rewind == null) continue;
                bool self = p == Owner;
                if (self && !affectSelf) continue;
                if (!self && IsAlly(p) && !affectAllies) continue;
                if (IsEnemy(p) && !affectEnemies) continue;
                if (!ChronoTimeUtil.IsInsideZone(p.Position, center, zoneRotation, zoneSize, zoneHeight)) continue;
                _players.Add(p);
            }

            if (!affectBalls) return;
            var manager = BallManager.Instance;
            var balls = manager != null ? manager.ActiveBalls : null;
            if (balls == null) return;

            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || ball.Rewind == null) continue;
                var state = ball.State;
                if (state == BallState.Held || state == BallState.Despawned) continue; // held balls travel with their holder
                if (!ChronoTimeUtil.IsInsideZone(ball.transform.position, center, zoneRotation, zoneSize, zoneHeight)) continue;
                _balls.Add(ball);
            }
        }

        private void SpawnTrail(Vector3 from, Vector3 to, ref int bursts)
        {
            if (bursts >= maxTrailBursts) return;
            VfxManager.Spawn(VfxId.RewindTrail, from, Quaternion.identity, 0.7f, zoneTint);
            bursts++;
            if (bursts >= maxTrailBursts || (to - from).sqrMagnitude < 0.01f) return;
            VfxManager.Spawn(VfxId.RewindTrail, to, Quaternion.identity, 0.9f, zoneTint);
            bursts++;
        }
    }
}
