using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Shadow's passive <b>[Decoy Dash]</b>: "sprinting leaves a 1 s fading illusion clone".
    /// <para>
    /// While Shadow is in the Sprinting state (and actually moving at speed) an illusion is left behind every
    /// <see cref="spawnInterval"/> seconds: <see cref="Characters.CharacterVisual.SpawnAfterimage"/> bakes the current mocap pose into a
    /// translucent, fading afterimage, and <see cref="ShadowClone.SpawnDecoy"/> gives it a short-lived hittable body so
    /// that for its 1 s life it can swallow an enemy ball (pops with CloneDissolve) and fool bots
    /// (<see cref="ShadowClone.ActiveClones"/>). The decoy only arms once Shadow has run clear of it, so it never acts as a
    /// shield for the real body.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ShadowDecoyDash : AbilityBase
    {
        [Header("Illusions")]
        [Tooltip("Seconds between two illusions while sprinting.")]
        [Min(0.2f)] public float spawnInterval = 0.9f;

        [Tooltip("Delay (s) after the sprint starts before the first illusion peels off.")]
        [Min(0f)] public float firstSpawnDelay = 0.15f;

        [Tooltip("Life of each illusion (s): the afterimage fades over this time and the decoy body lives as long. Spec: 1 s.")]
        [Range(0.2f, 3f)] public float illusionLifetime = 1f;

        [Tooltip("Minimum planar speed (m/s) for sprinting to leave illusions (standing sprint input does nothing).")]
        [Min(0f)] public float minimumSpeed = 3.5f;

        [Tooltip("The decoy body becomes hittable once Shadow is this far (m) from it, so it never shields the real body.")]
        [Min(0f)] public float armDistance = 0.8f;

        [Header("Look")]
        [Tooltip("Tint of the afterimage: a dark, smoky translucent silhouette (alpha = starting opacity).")]
        public Color afterimageTint = new Color(0.16f, 0.17f, 0.22f, 0.6f);

        [Tooltip("Volume of the soft whoosh when an illusion peels off (0 = silent).")]
        [Range(0f, 1f)] public float spawnVolume = 0.25f;

        [NonSerialized] private float _timer;
        [NonSerialized] private bool _wasSprinting;

        public ShadowDecoyDash() { }

        protected override void OnCast() { }

        protected override void OnTick(float deltaTime)
        {
            if (!IsSprintingFast())
            {
                _wasSprinting = false;
                return;
            }

            if (!_wasSprinting)
            {
                // Sprint just started: the first illusion peels off almost immediately.
                _wasSprinting = true;
                _timer = firstSpawnDelay;
            }

            _timer -= deltaTime;
            if (_timer > 0f) return;
            _timer = spawnInterval;
            SpawnIllusion();
        }

        protected override void OnRoundReset() => _wasSprinting = false;

        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        private bool IsSprintingFast()
        {
            var owner = Owner;
            if (owner == null || !owner.IsInfield || owner.StateMachine == null) return false;
            if (!owner.StateMachine.IsIn(PlayerStateId.Sprinting)) return false;
            var match = MatchManager.Instance;
            if (match != null && !match.IsPlaying) return false;
            return owner.Motor == null || owner.Motor.PlanarSpeed >= minimumSpeed;
        }

        private void SpawnIllusion()
        {
            var visual = Owner.Visual;
            if (visual == null) return;

            GameObject afterimage = visual.SpawnAfterimage(illusionLifetime, afterimageTint);
            if (afterimage == null) return;

            ShadowClone.SpawnDecoy(Owner, afterimage, illusionLifetime, armDistance);
            if (spawnVolume > 0f) AudioManager.PlayAt(SfxId.Clone, Owner.Position, spawnVolume, UnityEngine.Random.Range(1.2f, 1.35f));
        }
    }
}
