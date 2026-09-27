using System;
using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Specter - PASSIVE [Danger Sense] (危險感知): the screen edges flash red when a high-speed ball is locked on to Specter.
    /// <para>
    /// Every enemy throw (not a pass) that is soft-locked on Specter - or, optionally, predicted to hit Specter even
    /// though it was aimed at someone else - and leaves the hand at or above <see cref="minThreatSpeedKmh"/> becomes a
    /// tracked threat. For each threat a <see cref="DangerSenseEvent"/> with <c>Active = true</c>, the predicted time to
    /// impact (<see cref="TrajectoryPredictor.PredictImpact"/>) and the release speed is published; the HUD draws the red
    /// screen-edge flash for the local player and bots use it as a dodge cue. When the ball stops being live (caught,
    /// hit, bounced, frozen in stasis...), flies past Specter, or Specter leaves the infield, a matching event with
    /// <c>Active = false</c> is published.
    /// </para>
    /// Threats are tracked per ball, so several simultaneous fastballs each get their own start/stop pair.
    /// </summary>
    [Serializable]
    public sealed class SpecterDangerSense : AbilityBase
    {
        [Header("Detection")]
        [Tooltip("Minimum release speed (km/h) of an enemy throw for it to count as a high-speed threat.")]
        [Min(0f)] public float minThreatSpeedKmh = 90f;

        [Tooltip("Also warn about fast enemy balls predicted to hit Specter although they were soft-locked on someone else " +
                 "(or on nobody). The spec's 'locked on' case is always covered.")]
        public bool warnOnPredictedImpact = true;

        [Tooltip("Prediction horizon (s) used to estimate the time to impact.")]
        [Range(0.5f, 5f)] public float predictionHorizon = 3f;

        [Tooltip("Safety net: seconds after which a threat is dropped even if the ball is still flagged live.")]
        [Range(0.5f, 10f)] public float maxTrackSeconds = 4f;

        [Tooltip("Distance (m) a ball must travel beyond Specter's chest, along its flight direction, to count as passed.")]
        [Range(0f, 3f)] public float passedMargin = 0.6f;

        [Tooltip("Maximum number of simultaneously tracked threats (oldest is dropped first).")]
        [Range(1, 12)] public int maxTrackedThreats = 6;

        [Header("Local feedback")]
        [Tooltip("Adds a red post-process vignette pulse on top of the HUD edge flash when the local player is Specter.")]
        public bool postProcessPulse = true;

        [Tooltip("Intensity of the red post-process pulse.")]
        [Range(0f, 1f)] public float pulseIntensity = 0.55f;

        [Tooltip("Shortest pulse length (s). The pulse lasts until the predicted impact, clamped to [min, max].")]
        [Range(0.05f, 1f)] public float minPulseDuration = 0.2f;

        [Tooltip("Longest pulse length (s). The pulse lasts until the predicted impact, clamped to [min, max].")]
        [Range(0.1f, 2f)] public float maxPulseDuration = 0.8f;

        /// <summary>One ball that is currently considered locked on to Specter.</summary>
        private struct Threat
        {
            public DodgeBall Ball;
            public float LaunchTime;   // DodgeBall.LaunchTime at detection: detects re-throws of the same ball
            public float TrackedAt;    // Time.time at detection
            public float SpeedKmh;
        }

        // Runtime state. Reference types are created per clone in OnInitialize (MemberwiseClone would share them).
        [NonSerialized] private List<Threat> _threats;

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public SpecterDangerSense() { }

        /// <summary>Number of balls currently locked on to Specter.</summary>
        public int ActiveThreatCount => _threats != null ? _threats.Count : 0;

        /// <summary>True while at least one high-speed ball is locked on.</summary>
        public bool IsDangerActive => ActiveThreatCount > 0;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnInitialize()
        {
            _threats = new List<Threat>(Mathf.Max(1, maxTrackedThreats));
        }

        protected override void OnEquip()
        {
            Listen<BallThrownEvent>(OnBallThrown);
            Listen<RoundEndedEvent>(OnRoundEnded);
        }

        protected override void OnUnequip() => ClearAll();

        protected override void OnRoundReset() => ClearAll();

        /// <summary>Passives never cast; Danger Sense works purely from events and <see cref="OnTick"/>.</summary>
        protected override void OnCast() { }

        /// <summary>Every frame: drops threats that ended (no longer live, passed, re-thrown, timed out).</summary>
        protected override void OnTick(float deltaTime)
        {
            if (_threats == null || _threats.Count == 0) return;

            // Specter can no longer be hit (eliminated, in the outfield): every warning is moot.
            if (Owner == null || !Owner.IsTargetable)
            {
                ClearAll();
                return;
            }

            Vector3 chest = Owner.ChestPosition;
            float now = Now;
            for (int i = _threats.Count - 1; i >= 0; i--)
            {
                var threat = _threats[i];
                if (IsThreatOver(threat, chest, now)) EndThreat(i);
            }
        }

        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ event handlers

        private void OnBallThrown(BallThrownEvent e)
        {
            if (Owner == null || _threats == null) return;
            if (e.Ball == null || e.IsPass || e.Thrower == null || !IsEnemy(e.Thrower)) return;
            if (e.SpeedKmh < minThreatSpeedKmh) return;
            if (!Owner.IsTargetable) return;

            bool lockedOn = e.Target == Owner;
            bool predicted = TrajectoryPredictor.PredictImpact(e.Ball, Owner, predictionHorizon, out float timeToImpact, out _);
            if (!lockedOn && !(warnOnPredictedImpact && predicted)) return;

            if (!predicted)
            {
                // Locked on but the predictor thinks it will miss (e.g. Specter is already moving): straight-line estimate.
                float distance = Vector3.Distance(e.Origin, Owner.ChestPosition);
                timeToImpact = distance / Mathf.Max(1f, e.SpeedKmh * Core.GameConstants.KmhToMs);
            }

            // Same ball re-thrown: replace the entry silently (the new Active=true event supersedes the old one).
            int existing = IndexOf(e.Ball);
            var threat = new Threat { Ball = e.Ball, LaunchTime = e.Ball.LaunchTime, TrackedAt = Now, SpeedKmh = e.SpeedKmh };
            if (existing >= 0)
            {
                _threats[existing] = threat;
            }
            else
            {
                if (_threats.Count >= Mathf.Max(1, maxTrackedThreats)) EndThreat(0); // drop the oldest
                _threats.Add(threat);
            }

            GameEvents.Publish(new DangerSenseEvent
            {
                Player = Owner,
                Ball = e.Ball,
                TimeToImpact = timeToImpact,
                SpeedKmh = e.SpeedKmh,
                Active = true,
            });

            if (postProcessPulse && Owner.IsLocalPlayer && pulseIntensity > 0f)
            {
                float duration = Mathf.Clamp(timeToImpact, minPulseDuration, Mathf.Max(minPulseDuration, maxPulseDuration));
                ScreenFx.Pulse(ScreenPulse.DangerSense, pulseIntensity, duration);
            }
        }

        private void OnRoundEnded(RoundEndedEvent e) => ClearAll();

        // ------------------------------------------------------------------ helpers

        private bool IsThreatOver(in Threat threat, Vector3 chest, float now)
        {
            var ball = threat.Ball;
            if (ball == null || !ball.IsLive) return true;                        // caught, hit, bounced, stasis, despawned
            if (!Mathf.Approximately(ball.LaunchTime, threat.LaunchTime)) return true; // it is a different throw now
            if (now - threat.TrackedAt > maxTrackSeconds) return true;

            // Passed: the ball is beyond Specter along its own flight direction.
            Vector3 velocity = ball.Velocity;
            float speed = velocity.magnitude;
            if (speed > 0.5f)
            {
                float along = Vector3.Dot(ball.transform.position - chest, velocity / speed);
                if (along > passedMargin) return true;
            }
            return false;
        }

        private int IndexOf(DodgeBall ball)
        {
            for (int i = 0; i < _threats.Count; i++)
                if (_threats[i].Ball == ball) return i;
            return -1;
        }

        /// <summary>Removes threat <paramref name="index"/> and publishes the matching Active=false event.</summary>
        private void EndThreat(int index)
        {
            var threat = _threats[index];
            _threats.RemoveAt(index);
            if (Owner == null) return;

            GameEvents.Publish(new DangerSenseEvent
            {
                Player = Owner,
                Ball = threat.Ball,
                TimeToImpact = 0f,
                SpeedKmh = threat.SpeedKmh,
                Active = false,
            });
        }

        private void ClearAll()
        {
            if (_threats == null) return;
            for (int i = _threats.Count - 1; i >= 0; i--) EndThreat(i);
        }
    }
}
