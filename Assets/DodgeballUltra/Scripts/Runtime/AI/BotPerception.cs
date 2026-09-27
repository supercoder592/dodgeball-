using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Human-like perception of incoming balls. Every live enemy ball is tracked from its launch, but the bot only
    /// becomes aware of it after a reaction delay sampled from the difficulty profile (0.45 s Easy to 0.12 s Pro, with
    /// jitter). Balls thrown from outside the field of view (outfield throws at the back) are noticed later - or not at
    /// all until they enter the peripheral radius. Specter's Danger Sense shortens the delay.
    /// <para>
    /// Only noticed balls are run through <see cref="TrajectoryPredictor.PredictImpact"/>; the most urgent predicted hit is
    /// exposed as <see cref="MostUrgent"/>. Fixed-size storage, no allocations per frame.
    /// </para>
    /// </summary>
    public sealed class BotPerception
    {
        /// <summary>Maximum number of simultaneously tracked enemy balls.</summary>
        public const int Capacity = 12;

        private struct Entry
        {
            public DodgeBall Ball;
            public float LaunchTime;
            public float NoticeAt;
            public float Reaction;
            public bool Blind;
            public bool FromBehind;
            public bool Seen;
            public bool WillHit;
            public float TimeToImpact;
            public float CatchZoneTime;
            public Vector3 ImpactPoint;
        }

        private readonly Entry[] _entries = new Entry[Capacity];
        private readonly List<DodgeBall> _incoming = new List<DodgeBall>(Capacity);
        private int _count;
        private BotThreat _mostUrgent;

        /// <summary>A noticed ball is predicted to hit the bot.</summary>
        public bool HasThreat => _mostUrgent.Ball != null;

        /// <summary>The noticed ball with the smallest time to impact (invalid when <see cref="HasThreat"/> is false).</summary>
        public BotThreat MostUrgent => _mostUrgent;

        /// <summary>Number of noticed balls predicted to hit this update.</summary>
        public int NoticedThreatCount { get; private set; }

        /// <summary>Number of enemy live balls being tracked (noticed or not).</summary>
        public int TrackedCount => _count;

        /// <summary>Forgets everything (round reset, zone change).</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++) _entries[i] = default;
            _count = 0;
            _mostUrgent = default;
            NoticedThreatCount = 0;
        }

        /// <summary>
        /// Refreshes the tracked set and predictions. Call once per sampled frame.
        /// </summary>
        /// <param name="self">The perceiving player.</param>
        /// <param name="profile">Difficulty profile (reaction times, field of view).</param>
        /// <param name="rng">Per-bot random source.</param>
        /// <param name="now">Time.time (scaled).</param>
        /// <param name="lookahead">Maximum prediction horizon (s).</param>
        /// <param name="peripheralRadius">Distance (m) at which an unnoticed rear ball is finally sensed.</param>
        /// <param name="catchRadius">Radius (m) of the catch zone around the chest (CombatProfile.catchRadius).</param>
        public void Update(DodgeballPlayer self, BotDifficultyProfile profile, BotRandom rng, float now, float lookahead,
            float peripheralRadius, float catchRadius)
        {
            _mostUrgent = default;
            NoticedThreatCount = 0;

            var manager = BallManager.Instance;
            if (manager == null || self == null || !self.IsInfield)
            {
                Clear();
                return;
            }

            for (int i = 0; i < _count; i++) _entries[i].Seen = false;

            _incoming.Clear();
            manager.GetIncomingLiveBalls(self, _incoming);
            for (int b = 0; b < _incoming.Count; b++)
            {
                var ball = _incoming[b];
                if (ball == null || !ball.IsLive || ball.IsPass) continue;

                int idx = Find(ball, ball.LaunchTime);
                if (idx < 0) idx = Add(ball, self, profile, rng, now);
                if (idx < 0) continue; // storage full: ignore the least important newcomer

                ref var e = ref _entries[idx];
                e.Seen = true;

                if (e.Blind)
                {
                    // Unnoticed rear ball: sensed only when it is about to arrive (sound / peripheral vision).
                    if (BotWorld.PlanarDistance(ball.transform.position, self.Position) > peripheralRadius) continue;
                    e.Blind = false;
                    e.NoticeAt = now + e.Reaction * 0.5f;
                }

                e.WillHit = false;
                if (now < e.NoticeAt) continue;

                e.WillHit = TrajectoryPredictor.PredictImpact(ball, self, lookahead, out e.TimeToImpact, out e.ImpactPoint);
                if (!e.WillHit) continue;

                // The catch rule judges the moment the ball enters the catch zone, slightly before body contact.
                if (TrajectoryPredictor.TimeToReach(ball, self.ChestPosition, catchRadius, lookahead, out float zoneTime, out _))
                    e.CatchZoneTime = Mathf.Min(zoneTime, e.TimeToImpact);
                else
                    e.CatchZoneTime = e.TimeToImpact;

                NoticedThreatCount++;
                if (_mostUrgent.Ball == null || e.TimeToImpact < _mostUrgent.TimeToImpact)
                {
                    _mostUrgent = new BotThreat
                    {
                        Ball = ball,
                        LaunchTime = e.LaunchTime,
                        TimeToImpact = e.TimeToImpact,
                        ImpactPoint = e.ImpactPoint,
                        CatchZoneTime = e.CatchZoneTime,
                        FromBehind = e.FromBehind,
                        SpeedKmh = ball.SpeedKmh,
                        NoticedAt = e.NoticeAt,
                    };
                }
            }

            Compact();
        }

        /// <summary>
        /// Specter's Danger Sense: a fast ball is locked on to the bot. The bot becomes aware of it much faster, even when
        /// it was thrown from behind.
        /// </summary>
        public void NotifyDangerSense(DodgeBall ball, DodgeballPlayer self, BotDifficultyProfile profile, BotRandom rng, float now)
        {
            if (ball == null || self == null) return;
            int idx = Find(ball, ball.LaunchTime);
            if (idx < 0) idx = Add(ball, self, profile, rng, now);
            if (idx < 0) return;

            ref var e = ref _entries[idx];
            float fast = rng.Jitter(profile.reactionTime, profile.reactionJitter) * profile.dangerSenseReactionMultiplier;
            e.Blind = false;
            e.NoticeAt = Mathf.Min(e.NoticeAt, now + fast);
        }

        /// <summary>
        /// Latest prediction for a specific noticed ball (from the last <see cref="Update"/>). False when the ball is not
        /// tracked, not yet noticed, or no longer predicted to hit.
        /// </summary>
        public bool TryGetThreat(DodgeBall ball, float launchTime, out BotThreat threat)
        {
            threat = default;
            if (ball == null) return false;
            int idx = Find(ball, launchTime);
            if (idx < 0) return false;
            ref var e = ref _entries[idx];
            if (e.Blind || !e.WillHit || float.IsPositiveInfinity(e.NoticeAt)) return false;
            threat = new BotThreat
            {
                Ball = ball,
                LaunchTime = e.LaunchTime,
                TimeToImpact = e.TimeToImpact,
                ImpactPoint = e.ImpactPoint,
                CatchZoneTime = e.CatchZoneTime,
                FromBehind = e.FromBehind,
                SpeedKmh = ball.SpeedKmh,
                NoticedAt = e.NoticeAt,
            };
            return true;
        }

        /// <summary>True if <paramref name="ball"/> (current launch) has been noticed.</summary>
        public bool IsNoticed(DodgeBall ball, float now)
        {
            if (ball == null) return false;
            int idx = Find(ball, ball.LaunchTime);
            return idx >= 0 && !_entries[idx].Blind && now >= _entries[idx].NoticeAt;
        }

        // ------------------------------------------------------------------ internals

        private int Find(DodgeBall ball, float launchTime)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Ball == ball && Mathf.Abs(_entries[i].LaunchTime - launchTime) < 1e-4f) return i;
            }
            return -1;
        }

        private int Add(DodgeBall ball, DodgeballPlayer self, BotDifficultyProfile profile, BotRandom rng, float now)
        {
            // A re-launch of a tracked ball (caught and thrown back) replaces its old entry.
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Ball == ball)
                {
                    RemoveAt(i);
                    break;
                }
            }
            if (_count >= Capacity) return -1;

            float reaction = Mathf.Max(0.02f, rng.Jitter(profile.reactionTime, profile.reactionJitter));

            // Where did the throw come from, relative to where the bot is looking?
            var origin = ball.LaunchOrigin;
            if ((origin - ball.transform.position).sqrMagnitude < 1e-6f) origin = ball.transform.position;
            var toOrigin = BotWorld.PlanarDirection(self.Position, origin, self.Forward);
            float angle = Vector3.Angle(self.Forward, toOrigin);
            bool fromBehind = angle > profile.fieldOfView;

            bool blind = false;
            if (fromBehind)
            {
                reaction *= profile.rearReactionMultiplier;
                blind = rng.Chance(profile.rearBlindChance);
            }

            // Count the reaction from the launch when the bot saw the throw happen; from now when it only just started
            // watching (e.g. input was locked during the launch).
            float seenFrom = now - ball.LaunchTime <= 0.15f ? ball.LaunchTime : now;

            int idx = _count++;
            _entries[idx] = new Entry
            {
                Ball = ball,
                LaunchTime = ball.LaunchTime,
                Reaction = reaction,
                NoticeAt = blind ? float.PositiveInfinity : seenFrom + reaction,
                Blind = blind,
                FromBehind = fromBehind,
                TimeToImpact = float.PositiveInfinity,
                CatchZoneTime = float.PositiveInfinity,
            };
            return idx;
        }

        private void Compact()
        {
            int write = 0;
            for (int read = 0; read < _count; read++)
            {
                if (!_entries[read].Seen || _entries[read].Ball == null) continue;
                if (write != read) _entries[write] = _entries[read];
                write++;
            }
            for (int i = write; i < _count; i++) _entries[i] = default;
            _count = write;
        }

        private void RemoveAt(int index)
        {
            _count--;
            for (int i = index; i < _count; i++) _entries[i] = _entries[i + 1];
            _entries[_count] = default;
        }
    }
}
