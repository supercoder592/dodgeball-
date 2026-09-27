using System;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// Physical and flight tuning shared by every <see cref="DodgeBall"/>. One instance lives on the
    /// <see cref="BallManager"/> (Inspector-editable); balls read it through <see cref="BallManager.Tuning"/> and fall back to
    /// <see cref="Default"/> when no manager exists (tests, partial scenes).
    /// <para>
    /// Defaults describe a regulation 8.25" (0.21 m) foam-core, rubber-skinned dodgeball of ~0.35 kg on a lacquered hardwood
    /// court. Gameplay-only deviations from real physics (the reduced in-flight air drag, the 2-4 m/s hit knockback) are
    /// called out in the tooltips.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class BallPhysicsTuning
    {
        // ------------------------------------------------------------------ free physics body

        [Header("Physical ball (regulation foam dodgeball)")]
        [Tooltip("Mass (kg). A regulation 8.25\" foam dodgeball weighs roughly 0.3-0.4 kg.")]
        [Min(0.05f)] public float mass = 0.35f;

        [Tooltip("Bounciness of the ball's physics material (combined with 'Maximum', so the ball always bounces like foam " +
                 "even on dead surfaces).")]
        [Range(0f, 1f)] public float bounciness = 0.55f;

        [Tooltip("Sliding friction of the rubber skin against the court.")]
        [Range(0f, 1.5f)] public float dynamicFriction = 0.6f;

        [Tooltip("Static friction of the rubber skin against the court.")]
        [Range(0f, 1.5f)] public float staticFriction = 0.65f;

        [Tooltip("Linear damping (air drag) while the ball is a free physics body. Foam balls are light and draggy.")]
        [Min(0f)] public float linearDamping = 0.05f;

        [Tooltip("Angular damping while free: emulates the rolling resistance of a soft ball on hardwood.")]
        [Min(0f)] public float angularDamping = 0.4f;

        [Tooltip("Maximum angular velocity (rad/s). A 0.105 m ball rolling at 6 m/s spins at ~57 rad/s, far above Unity's " +
                 "default limit of 7 rad/s.")]
        [Min(7f)] public float maxAngularVelocity = 150f;

        // ------------------------------------------------------------------ live flight

        [Header("Live flight (custom integrator + sphere sweep)")]
        [Tooltip("Quadratic air drag k (1/m) while live: dv/dt = -k |v| v. A real foam ball is ~0.03; the gameplay default is " +
                 "much lower so the Rally Boost speeds (up to 220 km/h) survive the flight and the ballistic solver stays exact.")]
        [Range(0f, 0.05f)] public float liveQuadraticDrag = 0.0005f;

        [Tooltip("Seconds after launch during which the ball ignores its own thrower (the hand starts inside the body capsule).")]
        [Range(0f, 0.5f)] public float throwerGraceTime = 0.15f;

        [Tooltip("A contact whose normal has at least this Y component counts as the floor (resets the rally).")]
        [Range(0f, 1f)] public float floorNormalThreshold = 0.6f;

        [Tooltip("Normal restitution of a live ball bouncing on the floor (foam on lacquered wood).")]
        [Range(0f, 1f)] public float floorRestitution = 0.55f;

        [Tooltip("Normal restitution of a live ball bouncing off walls, padding and props.")]
        [Range(0f, 1f)] public float wallRestitution = 0.45f;

        [Tooltip("Fraction of the tangential velocity lost to friction on a surface bounce.")]
        [Range(0f, 1f)] public float surfaceTangentialLoss = 0.15f;

        [Tooltip("Normal restitution when a live ball glances off a player's body (soft tissue absorbs most of the energy).")]
        [Range(0f, 1f)] public float bodyRestitution = 0.28f;

        [Tooltip("Fraction of the tangential velocity kept when deflecting off a body.")]
        [Range(0f, 1f)] public float bodyTangentialRetention = 0.45f;

        [Tooltip("Upward pop (m/s) added when a ball deflects off a body, so it loops up and drops near the victim.")]
        [Range(0f, 5f)] public float bodyDeflectLift = 1.4f;

        [Tooltip("Normal restitution when a hittable (shield, turret body) blocks the ball.")]
        [Range(0f, 1f)] public float blockRestitution = 0.35f;

        [Tooltip("Fraction of the velocity kept when a hittable absorbs the ball (clones popping): it drops almost dead.")]
        [Range(0f, 0.5f)] public float absorbRetention = 0.08f;

        [Tooltip("Classic dodgeball rule: a live ball that strikes the ball an enemy is holding is blocked (deflects, no hit).")]
        public bool heldBallBlocks = true;

        [Tooltip("Normal restitution of a ball deflecting off a held (blocking) ball.")]
        [Range(0f, 1f)] public float heldBallBlockRestitution = 0.5f;

        [Tooltip("Planar shove (m/s) the blocker receives when their held ball stops a throw.")]
        [Range(0f, 3f)] public float heldBallBlockShove = 0.6f;

        [Tooltip("Visual spin (rad/s) given to thrown balls. A hand-thrown dodgeball turns ~2-4 revolutions per second.")]
        [Range(0f, 60f)] public float launchSpinRate = 16f;

        [Tooltip("Random side-spin tilt (deg) of the spin axis, so no two throws rotate identically.")]
        [Range(0f, 90f)] public float launchSpinTilt = 25f;

        // ------------------------------------------------------------------ hits

        [Header("Hit knockback (gameplay stagger)")]
        [Tooltip("Knockback (m/s velocity change) of a slow hit. Real momentum transfer of a 0.35 kg ball is ~0.2 m/s; the " +
                 "game uses a readable stagger instead.")]
        [Range(0f, 10f)] public float knockbackMin = 2f;

        [Tooltip("Knockback (m/s) of a 220 km/h hit.")]
        [Range(0f, 10f)] public float knockbackMax = 4f;

        [Tooltip("Ball speed (km/h) at which the knockback starts to rise above the minimum.")]
        [Min(0f)] public float knockbackMinSpeedKmh = 40f;

        // ------------------------------------------------------------------ stasis

        [Header("Stasis (Chrono)")]
        [Tooltip("Vertical hover amplitude (m) of a ball frozen in stasis.")]
        [Range(0f, 0.2f)] public float stasisBobAmplitude = 0.02f;

        [Tooltip("Hover frequency (Hz).")]
        [Range(0f, 4f)] public float stasisBobFrequency = 0.8f;

        [Tooltip("Slow drift rotation (deg/s) while in stasis.")]
        [Range(0f, 180f)] public float stasisSpinDegreesPerSecond = 20f;

        // ------------------------------------------------------------------ ability balls

        [Header("Ability projectiles")]
        [Tooltip("Seconds an ability ball takes to dissolve after it stops being live, before it returns to the pool.")]
        [Range(0.05f, 2f)] public float abilityBallFadeTime = 0.35f;

        [Tooltip("An ability ball that was spawned but never launched is recycled after this many seconds (safety net).")]
        [Range(0.1f, 5f)] public float abilityBallLaunchTimeout = 1f;

        // ------------------------------------------------------------------ free ball events

        [Header("Free-ball contact events")]
        [Tooltip("Minimum normal impact speed (m/s) of a free ball for a BallBouncedEvent (audio/VFX). Rolling contacts below " +
                 "this are silent.")]
        [Min(0f)] public float minBounceEventSpeed = 0.8f;

        [Tooltip("Minimum seconds between two bounce events of the same free ball.")]
        [Min(0f)] public float minBounceEventInterval = 0.06f;

        [Tooltip("Normal impact speed (m/s) that produces a full squash on a bounce.")]
        [Min(0.1f)] public float fullSquashImpactSpeed = 24f;

        [Tooltip("Largest squash (0..1) a bounce may produce (the JuiceManager drives the stronger hit squash).")]
        [Range(0f, 0.9f)] public float maxBounceSquash = 0.45f;

        // ------------------------------------------------------------------ runtime helpers

        [NonSerialized] private UnityEngine.Object _physicsMaterial;
        [NonSerialized] private float _materialBounciness = -1f;
        [NonSerialized] private float _materialDynamicFriction = -1f;
        [NonSerialized] private float _materialStaticFriction = -1f;

        private static BallPhysicsTuning s_default;

        /// <summary>Spec defaults, used when no <see cref="BallManager"/> exists.</summary>
        public static BallPhysicsTuning Default => s_default ??= new BallPhysicsTuning();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_default = null;

        /// <summary>
        /// Physics material for the ball collider (created through <see cref="PhysicsCompat"/>, bounce combine = Maximum).
        /// Cached; re-created only when the Inspector values changed.
        /// </summary>
        public UnityEngine.Object GetPhysicsMaterial()
        {
            bool stale = _physicsMaterial == null ||
                         !Mathf.Approximately(_materialBounciness, bounciness) ||
                         !Mathf.Approximately(_materialDynamicFriction, dynamicFriction) ||
                         !Mathf.Approximately(_materialStaticFriction, staticFriction);
            if (!stale) return _physicsMaterial;

            _physicsMaterial = PhysicsCompat.CreatePhysicsMaterial("DU_FoamDodgeball", bounciness, dynamicFriction, staticFriction, true);
            _materialBounciness = bounciness;
            _materialDynamicFriction = dynamicFriction;
            _materialStaticFriction = staticFriction;
            return _physicsMaterial;
        }

        /// <summary>Knockback velocity change (m/s) for a ball travelling at <paramref name="speedMs"/>.</summary>
        public float KnockbackForSpeed(float speedMs)
        {
            float t = Mathf.InverseLerp(knockbackMinSpeedKmh * Core.GameConstants.KmhToMs, Core.GameConstants.MaxBallSpeedMs, speedMs);
            return Mathf.Lerp(knockbackMin, knockbackMax, t);
        }
    }
}
