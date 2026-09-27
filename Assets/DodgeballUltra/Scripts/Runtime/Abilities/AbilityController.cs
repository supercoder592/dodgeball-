using System.Collections.Generic;
using DodgeballUltra.Characters;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities
{
    /// <summary>
    /// Owns a player's three ability instances (passive, skill, ultimate) and the Ultimate meter.
    /// Ticked by <see cref="DodgeballPlayer"/> after the state machine and combat controller.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AbilityController : MonoBehaviour
    {
        [Tooltip("Ultimate meter gained per second while in the infield during a live round (0..1 per second).")]
        [SerializeField] private float passiveUltGainPerSecond = 0.008f;

        private readonly List<AbilityBase> _all = new List<AbilityBase>(3);
        private ResourceMeter _ultimateMeter = new ResourceMeter(1f);

        public DodgeballPlayer Owner { get; private set; }
        public AbilityBase Passive { get; private set; }
        public AbilityBase Skill { get; private set; }
        public AbilityBase Ultimate { get; private set; }

        public IReadOnlyList<AbilityBase> All => _all;

        /// <summary>0..1</summary>
        public float UltimateCharge => _ultimateMeter.Normalized;
        public bool IsUltimateFull => _ultimateMeter.IsFull;

        public float PassiveUltGainPerSecond
        {
            get => passiveUltGainPerSecond;
            set => passiveUltGainPerSecond = Mathf.Max(0f, value);
        }

        /// <summary>Creates runtime instances from the hero's AbilityData assets.</summary>
        public void Setup(DodgeballPlayer owner, CharacterData character)
        {
            Owner = owner;
            Teardown();
            if (character == null) return;

            Passive = Create(character.passive);
            Skill = Create(character.skill);
            Ultimate = Create(character.ultimate);
        }

        /// <summary>Unequips everything (hero swap / destroy).</summary>
        public void Teardown()
        {
            for (int i = 0; i < _all.Count; i++) _all[i].Unequip();
            _all.Clear();
            Passive = Skill = Ultimate = null;
        }

        public AbilityBase Get(AbilitySlot slot)
        {
            switch (slot)
            {
                case AbilitySlot.Passive: return Passive;
                case AbilitySlot.Skill: return Skill;
                case AbilitySlot.Ultimate: return Ultimate;
                default: return null;
            }
        }

        /// <summary>Returns the first equipped ability of type <typeparamref name="T"/> (e.g. to query a passive).</summary>
        public T Find<T>() where T : AbilityBase
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i] is T t) return t;
            return null;
        }

        public bool TryUseSkill() => Skill != null && Skill.TryActivate(out _);

        public bool TryUseUltimate() => Ultimate != null && Ultimate.TryActivate(out _);

        public bool TryUse(AbilitySlot slot, out AbilityFailReason reason)
        {
            reason = AbilityFailReason.Custom;
            var a = Get(slot);
            return a != null && a.TryActivate(out reason);
        }

        public bool IsUltimateReady(float cost) => _ultimateMeter.Value + 1e-4f >= Mathf.Clamp01(cost);

        /// <summary>Called by AbilityBase when an ultimate is activated.</summary>
        public void ConsumeUltimate(float cost)
        {
            float before = _ultimateMeter.Normalized;
            _ultimateMeter.Add(-Mathf.Clamp01(cost));
            PublishUlt(before);
        }

        /// <summary>Adds ultimate charge (0..1 scale). Spec: a Perfect Catch adds 0.15.</summary>
        public void AddUltimateCharge(float amount, UltGainReason reason)
        {
            if (amount == 0f) return;
            float before = _ultimateMeter.Normalized;
            _ultimateMeter.Add(amount);
            PublishUlt(before);
        }

        public void SetUltimateCharge(float normalized)
        {
            float before = _ultimateMeter.Normalized;
            _ultimateMeter.Set(Mathf.Clamp01(normalized));
            PublishUlt(before);
        }

        /// <summary>Interrupts every busy ability (stun, freeze, elimination, round end).</summary>
        public void InterruptAll(InterruptReason reason)
        {
            for (int i = 0; i < _all.Count; i++) _all[i].Interrupt(reason);
        }

        /// <summary>Round reset. The ultimate meter carries over between rounds unless <paramref name="clearUltimate"/>.</summary>
        public void ResetForRound(bool clearUltimate = false)
        {
            for (int i = 0; i < _all.Count; i++) _all[i].ResetForRound();
            if (clearUltimate) SetUltimateCharge(0f);
        }

        public void Tick(float deltaTime)
        {
            if (Owner == null) return;

            var match = MatchManager.Instance;
            bool live = match == null || match.IsPlaying;
            if (live && Owner.IsInfield && passiveUltGainPerSecond > 0f && !_ultimateMeter.IsFull)
            {
                AddUltimateCharge(passiveUltGainPerSecond * deltaTime, UltGainReason.Passive);
            }

            for (int i = 0; i < _all.Count; i++) _all[i].Tick(deltaTime);
        }

        private AbilityBase Create(AbilityData data)
        {
            if (data == null) return null;
            var runtime = data.CreateRuntimeInstance();
            if (runtime == null)
            {
                Debug.LogWarning($"[Dodgeball Ultra] AbilityData '{data.name}' has no logic assigned.", data);
                return null;
            }
            runtime.Initialize(Owner, data);
            _all.Add(runtime);
            return runtime;
        }

        private void PublishUlt(float before)
        {
            float now = _ultimateMeter.Normalized;
            if (Mathf.Approximately(before, now)) return;
            bool becameReady = before < 1f - 1e-4f && _ultimateMeter.IsFull;
            GameEvents.Publish(new UltimateChargeChangedEvent { Player = Owner, Normalized = now, BecameReady = becameReady });
        }

        private void OnDestroy() => Teardown();
    }
}
