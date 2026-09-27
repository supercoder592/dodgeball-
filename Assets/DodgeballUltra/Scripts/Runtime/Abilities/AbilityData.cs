using UnityEngine;

namespace DodgeballUltra.Abilities
{
    /// <summary>
    /// Data-driven ability definition (ScriptableObject). Holds the designer-facing metadata shared by every ability plus
    /// a polymorphic <see cref="logic"/> template (<c>[SerializeReference]</c>) whose serialized fields are the ability's
    /// own tuning values (e.g. shockwave radius). At runtime each player gets its own clone of the template via
    /// <see cref="CreateRuntimeInstance"/>, so assets are never mutated during play.
    /// </summary>
    [CreateAssetMenu(fileName = "Ability_", menuName = "Dodgeball Ultra/Ability Data", order = 10)]
    public sealed class AbilityData : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id, e.g. 'rayne.supersonic_meteor'.")]
        public string abilityId = "new.ability";
        public string displayName = "New Ability";
        [TextArea(2, 5)] public string description;
        public Sprite icon;
        public AbilitySlot slot = AbilitySlot.Skill;

        [Header("Timing")]
        [Tooltip("Cooldown in seconds after the ability ends (Skills). Ultimates normally use 0 and gate on the meter.")]
        [Min(0f)] public float cooldown = 10f;
        [Tooltip("Channel time before OnCast fires (0 = instant).")]
        [Min(0f)] public float castTime;
        [Tooltip("How long the ability stays Active after OnCast (0 = instant effect).")]
        [Min(0f)] public float duration;

        [Header("Rules")]
        [Tooltip("Fraction of the ultimate meter consumed (Ultimates only).")]
        [Range(0f, 1f)] public float ultimateCost = 1f;
        [Tooltip("The ability can only be activated while holding a ball.")]
        public bool requiresBall;
        [Tooltip("The ability can be used from the outfield.")]
        public bool usableFromOutfield;
        [Tooltip("Stun / freeze / elimination interrupt the channel or active effect.")]
        public bool interruptible = true;

        [Header("AI")]
        public AbilityAIHint aiHint = AbilityAIHint.Anytime;
        [Tooltip("Relative desire to use this ability when the hint matches (0..1).")]
        [Range(0f, 1f)] public float aiWeight = 0.5f;

        [Header("Logic")]
        [Tooltip("The runtime behaviour class and its tuning values.")]
        [SerializeReference] public AbilityBase logic;

        /// <summary>A fresh runtime instance for one player (clone of <see cref="logic"/>), or null if no logic is assigned.</summary>
        public AbilityBase CreateRuntimeInstance() => logic != null ? logic.CloneTemplate() : null;

        /// <summary>Convenience factory used by the roster factory and editor tools.</summary>
        public static AbilityData Create(string id, string displayName, AbilitySlot slot, AbilityBase logic, float cooldown,
            float castTime = 0f, float duration = 0f, string description = null)
        {
            var data = CreateInstance<AbilityData>();
            data.name = id;
            data.abilityId = id;
            data.displayName = displayName;
            data.slot = slot;
            data.logic = logic;
            data.cooldown = cooldown;
            data.castTime = castTime;
            data.duration = duration;
            data.description = description;
            return data;
        }
    }
}
