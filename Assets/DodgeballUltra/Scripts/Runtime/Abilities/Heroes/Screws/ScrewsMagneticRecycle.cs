using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Screws - PASSIVE [Magnetic Recycle] (磁力回收): balls dropped on a death slowly roll back toward Screws' territory.
    /// <para>
    /// On every <see cref="PlayerEliminatedEvent"/> a recycle zone opens for <see cref="window"/> seconds (spec: 4 s) at the
    /// elimination point. Loose balls within <see cref="captureRadius"/> (3 m) of it - including balls that land there during
    /// the window, like the one that made the hit - and the ball the eliminated player was holding are tagged. Tagged balls
    /// that are rolling on the floor receive a gentle planar acceleration (~1.5 m/s^2, capped at a slow roll) toward the
    /// centre of Screws' half until they are safely inside it (then friction takes over) or the window ends.
    /// </para>
    /// <para>
    /// The push is registered as a <see cref="BallManager"/> field effect for the window (applied in the physics step when the
    /// ball system feeds Free balls to field effects); tagged balls not serviced by the physics step this frame are pushed
    /// from the passive's own tick instead, so the behaviour never depends on how the ball module schedules field effects
    /// and a ball is never pushed twice in one step.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ScrewsMagneticRecycle : AbilityBase, IBallFieldEffect
    {
        private struct Zone
        {
            public Vector3 Point;
            public float Until;
        }

        private struct Tag
        {
            public DodgeBall Ball;
            public DodgeballPlayer DroppedBy; // eliminated holder (null for balls already loose)
            public float Until;
            public float LastPhysicsPush; // Time.fixedTime of the last push applied from the physics step
        }

        [Header("Magnetic Recycle")]
        [Tooltip("Seconds the recycle stays active after an elimination. Spec: 4 s.")]
        [Range(0.5f, 10f)] public float window = 4f;

        [Tooltip("Loose balls within this radius (m) of the elimination point are recycled. Spec: 3 m.")]
        [Range(0.5f, 8f)] public float captureRadius = 3f;

        [Tooltip("Planar acceleration (m/s^2) toward Screws' half. Spec: ~1.5 m/s^2 (a gentle roll).")]
        [Range(0.1f, 6f)] public float acceleration = 1.5f;

        [Tooltip("Speed (m/s) the push never exceeds along its direction (keeps it a slow roll).")]
        [Range(0.3f, 6f)] public float maxRollSpeed = 2.2f;

        [Tooltip("Only balls within this height (m) above the floor (rolling, not flying) are pushed.")]
        [Range(0.05f, 1f)] public float maxHeightAboveFloor = 0.2f;

        [Tooltip("A ball is released once it is this far (m) inside Screws' half.")]
        [Range(0f, 3f)] public float releaseDepth = 0.6f;

        [Tooltip("Also recycle eliminations of Screws' own teammates (and himself), not only of enemies.")]
        public bool includeFriendlyEliminations = true;

        [Header("Presentation")]
        [Tooltip("Play a faint magnetic hum at the elimination point when the recycle engages.")]
        public bool playHum = true;

        [Range(0f, 1f)] public float humVolume = 0.3f;

        [NonSerialized] private List<Zone> _zones;
        [NonSerialized] private List<Tag> _tags;
        [NonSerialized] private bool _registered;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public ScrewsMagneticRecycle() { }

        /// <summary>Balls currently being rolled home.</summary>
        public int ActiveTagCount => _tags != null ? _tags.Count : 0;

        protected override void OnInitialize()
        {
            _zones = new List<Zone>(4);
            _tags = new List<Tag>(6);
        }

        protected override void OnEquip()
        {
            Listen<PlayerEliminatedEvent>(OnPlayerEliminated);
            Listen<RoundEndedEvent>(_ => ClearAll());
            Listen<RoundStartedEvent>(_ => ClearAll());
        }

        protected override void OnUnequip() => ClearAll();

        /// <summary>Passives are never cast.</summary>
        protected override void OnCast() { }

        protected override void OnRoundReset() => ClearAll();

        /// <summary>A passive: the AI never "uses" it.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ events

        private void OnPlayerEliminated(PlayerEliminatedEvent e)
        {
            if (Owner == null || !Owner.Team.IsValid() || e.Player == null) return;
            if (!includeFriendlyEliminations && !IsEnemy(e.Player)) return;
            var manager = BallManager.Instance;
            if (manager == null || Court.Instance == null) return;

            Vector3 point = e.Player.Position; // feet: balls roll on the floor
            float until = Now + window;
            _zones.Add(new Zone { Point = point, Until = until });

            // The ball the victim was holding (it will be tagged once it is dropped/free).
            var held = e.Player.Combat != null ? e.Player.Combat.HeldBall : null;
            if (held != null && !held.IsAbilityBall) TagBall(held, until, e.Player);
            CaptureAround(point, until);

            EnsureRegistered();
            if (playHum) AudioManager.PlayAt(SfxId.Magnet, point, humVolume, 1.35f);
        }

        // ------------------------------------------------------------------ per frame

        protected override void OnTick(float deltaTime)
        {
            if (_tags.Count == 0 && _zones.Count == 0)
            {
                Unregister();
                return;
            }

            float now = Now;
            // Zones keep tagging balls that settle near the elimination point during the window.
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (now > _zones[i].Until) { _zones.RemoveAt(i); continue; }
                CaptureAround(_zones[i].Point, _zones[i].Until);
            }

            // Fallback push for tagged balls the physics step did not service (Free balls may not receive field effects).
            float fixedNow = Time.fixedTime;
            float stepTolerance = Time.fixedDeltaTime * 1.5f;
            for (int i = _tags.Count - 1; i >= 0; i--)
            {
                var tag = _tags[i];
                if (tag.Ball == null || now > tag.Until || tag.Ball.IsAbilityBall) { _tags.RemoveAt(i); continue; }
                // Somebody picked it up (or threw it): it is back in play, stop recycling it.
                if (tag.Ball.IsLive || (tag.Ball.State == BallState.Held && tag.Ball.Holder != tag.DroppedBy))
                {
                    _tags.RemoveAt(i);
                    continue;
                }
                if (!tag.Ball.IsFree) continue; // still in the eliminated player's hands: wait for the drop
                if (fixedNow - tag.LastPhysicsPush <= stepTolerance) continue;
                if (!Push(tag.Ball, deltaTime)) _tags.RemoveAt(i);
            }
        }

        /// <summary>Field effect: pushes tagged Free balls inside the physics step (when the ball module calls it for them).</summary>
        public void ApplyToBall(DodgeBall ball, float fixedDeltaTime)
        {
            if (ball == null || !ball.IsFree || _tags == null) return;
            int index = IndexOf(ball);
            if (index < 0) return;
            var tag = _tags[index];
            if (Now > tag.Until) return;
            tag.LastPhysicsPush = Time.fixedTime;
            _tags[index] = tag;
            if (!Push(ball, fixedDeltaTime)) _tags.RemoveAt(index);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Accelerates <paramref name="ball"/> toward Screws' half. Returns false once it is home (untag).</summary>
        private bool Push(DodgeBall ball, float dt)
        {
            var court = Court.Instance;
            if (court == null || Owner == null || !Owner.Team.IsValid()) return false;

            Vector3 pos = ball.transform.position;
            Vector3 home = -court.AttackDirection(Owner.Team);
            float depth = Vector3.Dot(pos - court.Center, home);
            if (depth >= releaseDepth && court.IsInInfield(Owner.Team, pos)) return false; // safely home

            var body = ball.Body;
            if (body == null || body.isKinematic) return true;
            if (pos.y - court.FloorY > ball.Radius + maxHeightAboveFloor) return true; // bouncing: wait for it to roll

            Vector3 target = court.GetInfieldBounds(Owner.Team).center;
            Vector3 dir = ScrewsGadgetKit.Planar(target - pos, home);
            Vector3 v = body.GetVelocity();
            float along = v.x * dir.x + v.z * dir.z;
            if (along < maxRollSpeed)
            {
                float dv = Mathf.Min(acceleration * dt, maxRollSpeed - along);
                body.SetVelocity(v + dir * dv);
            }
            return true;
        }

        private void CaptureAround(Vector3 point, float until)
        {
            var manager = BallManager.Instance;
            if (manager == null) return;
            var balls = manager.MatchBalls;
            float r2 = captureRadius * captureRadius;
            for (int i = 0; i < balls.Count; i++)
            {
                var b = balls[i];
                if (b == null || !b.IsFree || b.IsAbilityBall) continue;
                Vector3 d = b.transform.position - point;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                TagBall(b, until, null);
            }
        }

        private void TagBall(DodgeBall ball, float until, DodgeballPlayer droppedBy)
        {
            int index = IndexOf(ball);
            if (index >= 0)
            {
                var tag = _tags[index];
                if (until > tag.Until) tag.Until = until;
                _tags[index] = tag;
                return;
            }
            _tags.Add(new Tag { Ball = ball, DroppedBy = droppedBy, Until = until, LastPhysicsPush = -1f });
        }

        private int IndexOf(DodgeBall ball)
        {
            for (int i = 0; i < _tags.Count; i++)
                if (_tags[i].Ball == ball) return i;
            return -1;
        }

        private void EnsureRegistered()
        {
            if (_registered || BallManager.Instance == null) return;
            BallManager.Instance.RegisterFieldEffect(this);
            _registered = true;
        }

        private void Unregister()
        {
            if (!_registered) return;
            _registered = false;
            if (BallManager.Instance != null) BallManager.Instance.UnregisterFieldEffect(this);
        }

        private void ClearAll()
        {
            _zones?.Clear();
            _tags?.Clear();
            Unregister();
        }
    }
}
