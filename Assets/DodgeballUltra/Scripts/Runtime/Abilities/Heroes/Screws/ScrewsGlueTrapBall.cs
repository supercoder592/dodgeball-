using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Screws - SKILL [Glue Trap Ball] (黏膠陷阱球): throws a viscous ball that leaves a 4 s slowing puddle (60 % slow) where
    /// it lands or hits (CD 10 s).
    /// <para>
    /// OnCast conjures an ability ball through <see cref="AbilityUtil.ThrowAbilityBall"/> (style Glue, 0.9x speed of a full
    /// charge, gravity 0.8 - a heavy, slightly loopy throw) carrying a <see cref="ScrewsGluePayload"/>. On a player hit the
    /// puddle appears at the victim's feet (the hit still counts normally); on any surface contact it is projected to the
    /// floor below. The <see cref="ScrewsGluePuddle"/> slows ENEMIES standing in it (refreshing, removed on exit), bogs
    /// down rolling balls and plays GlueSplat VFX/SFX. All puddles of one Screws share a single slow source, so overlapping
    /// puddles never stack beyond 60 %.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ScrewsGlueTrapBall : AbilityBase
    {
        [Header("Glue ball")]
        [Tooltip("Speed multiplier on top of a full-charge throw. Spec: x0.9 (a heavy, viscous ball).")]
        [Range(0.3f, 1.5f)] public float speedMultiplier = 0.9f;

        [Tooltip("Gravity multiplier of the glue ball. Spec: 0.8.")]
        [Range(0f, 2f)] public float gravityScale = 0.8f;

        [Header("Puddle")]
        [Tooltip("Puddle radius (m). Spec: 2 m.")]
        [Range(0.5f, 5f)] public float puddleRadius = 2f;

        [Tooltip("Puddle lifetime (s). Spec: 4 s.")]
        [Range(0.5f, 12f)] public float puddleDuration = 4f;

        [Tooltip("Slow applied to enemies inside (0.6 = 60 % slower). Spec: 60 %.")]
        [Range(0f, 0.95f)] public float slowMagnitude = 0.6f;

        [Tooltip("Duration (s) of each slow refresh; it lingers this long after stepping out only if the removal is missed.")]
        [Range(0.05f, 1f)] public float slowRefresh = 0.3f;

        [Tooltip("Players whose feet are higher than this (m) above the glue (jumping over it) are not stuck.")]
        [Range(0.05f, 1.5f)] public float footHeight = 0.35f;

        [Tooltip("Viscous drag (1/s) on loose balls rolling through the glue (0 = off).")]
        [Range(0f, 15f)] public float ballDrag = 3.5f;

        [Tooltip("Also splat when the glue ball is caught (the catcher ends up standing in it).")]
        public bool puddleOnCatch = true;

        [Tooltip("Maximum puddles one Screws can have at once (the oldest dries up instantly).")]
        [Range(1, 6)] public int maxActivePuddles = 3;

        [Header("Look")]
        [Tooltip("Glue albedo: dark, slightly translucent-looking green.")]
        public Color glueColor = new Color(0.07f, 0.17f, 0.05f, 1f);

        [Tooltip("Wet smoothness (0..1). Glossy.")]
        [Range(0f, 1f)] public float smoothness = 0.93f;

        [Tooltip("Surface wobble amplitude.")]
        [Range(0f, 0.2f)] public float wobble = 0.035f;

        [Tooltip("Seconds the splat takes to spread.")]
        [Range(0.01f, 1f)] public float spreadTime = 0.2f;

        [Tooltip("Seconds at the end during which the glue dries out and vanishes.")]
        [Range(0.05f, 2f)] public float dryTime = 0.6f;

        [Tooltip("Satellite droplets around the splat.")]
        [Range(0, 16)] public int droplets = 6;

        [Header("AI")]
        [Tooltip("Bots throw the glue ball at enemies closer than this (m).")]
        [Range(3f, 30f)] public float aiMaxRange = 16f;

        [NonSerialized] private List<ScrewsGluePuddle> _puddles;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public ScrewsGlueTrapBall() { }

        /// <summary>The ball thrown by the last cast (may already be recycled).</summary>
        public DodgeBall LastBall { get; private set; }

        protected override void OnInitialize() => _puddles = new List<ScrewsGluePuddle>(4);

        protected override void OnEquip()
        {
            Listen<RoundEndedEvent>(_ => ClearPuddles());
            Listen<RoundStartedEvent>(_ => ClearPuddles());
        }

        protected override void OnUnequip() => ClearPuddles();

        protected override void OnRoundReset() => ClearPuddles();

        protected override void OnCast()
        {
            var settings = new ScrewsGluePuddleSettings
            {
                Radius = puddleRadius,
                Duration = puddleDuration,
                SlowMagnitude = slowMagnitude,
                SlowRefresh = slowRefresh,
                FootMargin = 0.15f,
                FootHeight = footHeight,
                BallDrag = ballDrag,
                SpreadTime = spreadTime,
                DryTime = dryTime,
                Color = glueColor,
                Smoothness = smoothness,
                Wobble = wobble,
                Droplets = droplets,
            };

            var options = AbilityThrowOptions.Default(BallStyle.Glue);
            options.SpeedMultiplier = speedMultiplier;
            options.GravityScale = gravityScale;
            options.Payload = new ScrewsGluePayload(Owner, settings, this, puddleOnCatch, OnPuddleSpawned);

            LastBall = AbilityUtil.ThrowAbilityBall(Owner, options);
            if (LastBall != null) AudioManager.PlayAt(SfxId.Glue, Owner.ChestPosition, 0.45f, 1.25f); // wet slap on release
        }

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || ctx.NearestEnemy == null) return 0f;
            if (ctx.NearestEnemyDistance > aiMaxRange) return 0f;
            float w = Data.aiWeight;
            // Better against enemies who are not already slowed, and when we have no ball of our own to throw.
            var status = ctx.NearestEnemy.Status;
            float slowed = status != null && status.Has(StatusEffectType.Slow) ? 0.5f : 1f;
            float ballBonus = ctx.HoldingBall ? 0.75f : 1f;
            return Mathf.Clamp01(w * slowed * ballBonus * (1f - 0.3f * ctx.NearestEnemyDistance / aiMaxRange));
        }

        private void OnPuddleSpawned(ScrewsGluePuddle puddle)
        {
            if (puddle == null) return;
            for (int i = _puddles.Count - 1; i >= 0; i--)
                if (_puddles[i] == null) _puddles.RemoveAt(i);
            _puddles.Add(puddle);
            while (_puddles.Count > Mathf.Max(1, maxActivePuddles))
            {
                var oldest = _puddles[0];
                _puddles.RemoveAt(0);
                if (oldest != null) oldest.Dismiss();
            }
        }

        private void ClearPuddles()
        {
            if (_puddles != null)
            {
                for (int i = 0; i < _puddles.Count; i++)
                    if (_puddles[i] != null) _puddles[i].Dismiss();
                _puddles.Clear();
            }
            ScrewsGluePuddle.DismissAll(this);
        }
    }
}
