using DodgeballUltra.Abilities;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// Data-driven hero definition (ScriptableObject): identity, the realistic human model to use, stats and the three
    /// abilities. Generated for all ten heroes by <c>Dodgeball Ultra ▸ Setup Wizard</c>; can be created by hand too.
    /// </summary>
    [CreateAssetMenu(fileName = "Hero_", menuName = "Dodgeball Ultra/Character Data", order = 0)]
    public sealed class CharacterData : ScriptableObject
    {
        [Header("Identity")]
        public HeroId heroId;
        public string displayName = "Hero";
        [Tooltip("Short archetype, e.g. 'Speedball'.")]
        public string title = "Archetype";
        public HeroRole role;
        [TextArea(2, 6)] public string description;
        public Sprite portrait;
        [Tooltip("Accent colour for UI and ability VFX.")]
        public Color themeColor = new Color(1f, 0.55f, 0.1f);

        [Header("Realistic human model")]
        [Tooltip("A rigged HUMANOID model prefab (Animator + Humanoid Avatar). Built by the Setup Wizard from the Microsoft " +
                 "Rocketbox library, but any Humanoid (Mixamo, Character Creator, MetaHuman export...) works.")]
        public GameObject modelPrefab;
        [Tooltip("Rocketbox avatar folder name used by the Setup Wizard, e.g. 'Sports_Male_02'.")]
        public string rocketboxAvatar = "Sports_Male_02";
        public BodyType bodyType = BodyType.Male;
        [Tooltip("Animator controller for the model (the wizard generates male/female controllers from motion-capture clips).")]
        public RuntimeAnimatorController animatorController;
        [Tooltip("Uniform scale applied to the model instance.")]
        [Min(0.1f)] public float modelScale = 1f;
        [Tooltip("Physical height used for the collision capsule (m).")]
        [Range(1.4f, 2.2f)] public float height = 1.8f;
        [Tooltip("Collision capsule radius (m).")]
        [Range(0.2f, 0.6f)] public float radius = 0.32f;

        [Header("Stats")]
        [Tooltip("Spec: 100 for most heroes, 200 for Gouki (Thick Hide).")]
        [Min(1)] public int maxHp = Core.GameConstants.DefaultMaxHp;
        public MotorProfile movement = new MotorProfile();
        public CombatProfile combat = new CombatProfile();

        [Header("Abilities")]
        public AbilityData passive;
        public AbilityData skill;
        public AbilityData ultimate;

        [Header("Audio")]
        [Tooltip("Pitch multiplier for vocal efforts (grunts on throws / hits).")]
        [Range(0.6f, 1.6f)] public float voicePitch = 1f;
    }
}
