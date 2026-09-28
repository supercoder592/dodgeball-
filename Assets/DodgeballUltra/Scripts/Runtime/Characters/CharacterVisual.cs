using System;
using System.Collections.Generic;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.Rendering;
using DodgeballUltra.VFX;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - the realistic human body of a player.
    /// <para>
    /// Hierarchy built by <see cref="Build"/>:
    /// <code>
    /// Player (DodgeballPlayer, Rigidbody, Capsule)
    ///   └─ Visual (CharacterVisual)
    ///        └─ LeanPivot (ProceduralLean rotates this)
    ///             └─ Model (instance of CharacterData.modelPrefab: Animator + Humanoid avatar + SkinnedMeshRenderers)
    ///                  + PlayerAnimatorDriver, CharacterIKController (OnAnimatorIK lives on the Animator's GameObject)
    ///                  + HitFlash, RagdollController
    /// </code>
    /// If <c>modelPrefab</c> is missing, a clearly-labelled placeholder is used and a warning explains how to run the
    /// Setup Wizard to download the realistic avatars. The game must never ship with the placeholder.
    /// </para>
    /// <para>
    /// Implementation notes:
    /// <list type="bullet">
    /// <item>The model instance is sanitised (colliders / rigidbodies / joints and stray Dodgeball Ultra components removed,
    ///       layer <see cref="GameLayers.Visual"/>, LODGroup for raw Rocketbox exports); gameplay collision is the player's
    ///       capsule and the ragdoll is rebuilt by <see cref="RagdollController"/>. The Animator never applies root motion
    ///       (the motor owns the position) and always animates (cheap, and IK / ragdoll velocities need a fresh pose).</item>
    /// <item>Hand sockets sit in the palm (computed from the finger knuckles, <see cref="HumanoidUtil.ComputePalmFrame"/>)
    ///       one ball radius off the palm, so a held ball rests against the hand on any Humanoid rig; forward = fingers,
    ///       up = palm normal. Models without a Humanoid avatar have no sockets (the ball uses the hip carry fallback).</item>
    /// <item>The flat team ring lies on the floor under the Visual (not the LeanPivot), so it never tilts with the lean.</item>
    /// <item>Freeze: an ice coat (a translucent glossy copy of every skinned mesh, part of the same LOD levels) plus faceted
    ///       ice crystals growing from the floor around the legs, animator paused. The cold tint and the frozen mist belong to
    ///       the StatusEffectController while the Frozen status drives the freeze; a direct call (no status) gets its own.</item>
    /// <item>Cloak: seen by an enemy of the local player the body is not rendered at all (renderers and shadows off) with a
    ///       refraction shimmer on engage / disengage; the owner and allies see a translucent ghost (shared ghost material,
    ///       no shadow) so they keep control.</item>
    /// <item>Fades run on unscaled time (juice): a freeze reads instantly even during the hit's hitstop.</item>
    /// </list>
    /// </para>
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(120)] // after the ragdoll (100): ring visibility reflects this frame's ragdoll state
    public sealed class CharacterVisual : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Team ring")]
        [Tooltip("Outer radius (m) of the team ring on the floor.")]
        [Range(0.2f, 1.2f)] public float ringRadius = 0.5f;

        [Tooltip("Height (m) of the ring above the floor (avoids z-fighting with the court).")]
        [Range(0f, 0.05f)] public float ringHeight = 0.012f;

        [Tooltip("Ring opacity for other players.")]
        [Range(0f, 1f)] public float ringOpacity = 0.5f;

        [Tooltip("Ring opacity for the local player (easier to find yourself in a scramble).")]
        [Range(0f, 1f)] public float localRingOpacity = 0.8f;

        [Header("Freeze (Elsa)")]
        [Tooltip("Colour (rgb) and opacity (a) of the ice coat over the body.")]
        public Color frostColor = new Color(0.8f, 0.92f, 1f, 0.42f);

        [Tooltip("Colour (rgb) and opacity (a) of the ice crystals growing around the legs.")]
        public Color iceCrystalColor = new Color(0.78f, 0.9f, 1f, 0.7f);

        [Tooltip("Number of ice crystals around the legs.")]
        [Range(0, 16)] public int iceCrystalCount = 9;

        [Tooltip("Seconds (unscaled) the ice takes to grow.")]
        [Range(0.01f, 1f)] public float iceGrowTime = 0.14f;

        [Tooltip("Seconds (unscaled) the ice takes to melt away.")]
        [Range(0.01f, 2f)] public float iceMeltTime = 0.3f;

        [Tooltip("Cold tint used when SetFrozen is called without the Frozen status (the status controller tints otherwise).")]
        public Color frozenTint = new Color(0.62f, 0.83f, 1f, 1f);

        [Tooltip("Strength of that tint.")]
        [Range(0f, 1f)] public float frozenTintAmount = 0.45f;

        [Header("Cloak (Gale)")]
        [Tooltip("Ghost colour (rgb) and opacity (a) the owner and allies see while cloaked.")]
        public Color cloakGhostTint = new Color(0.72f, 0.84f, 1f, 0.3f);

        // ------------------------------------------------------------------ contract state

        public DodgeballPlayer Owner { get; private set; }
        public Animator Animator { get; private set; }

        /// <summary>True when a Humanoid model with an Avatar is in use (IK, ragdoll and retargeted mocap available).</summary>
        public bool HasHumanoidModel { get; private set; }

        public Transform LeanPivot { get; private set; }
        public Transform ModelRoot { get; private set; }

        public PlayerAnimatorDriver AnimatorDriver { get; private set; }
        public CharacterIKController IK { get; private set; }
        public ProceduralLean Lean { get; private set; }
        public HitFlash HitFlash { get; private set; }
        public RagdollController Ragdoll { get; private set; }

        /// <summary>Socket in the right palm where a held ball sits.</summary>
        public Transform RightHandSocket { get; private set; }
        public Transform LeftHandSocket { get; private set; }

        public Renderer[] Renderers { get; private set; } = Array.Empty<Renderer>();

        // ------------------------------------------------------------------ additional state

        /// <summary>True while the ice-block look is on and the animation is paused (Elsa freeze).</summary>
        public bool IsFrozen { get; private set; }

        /// <summary>True while Gale's Optical Camouflage look is on.</summary>
        public bool IsCloaked { get; private set; }

        /// <summary>True while cloaked AND the local player is an enemy (the body is not rendered at all).</summary>
        public bool IsHiddenByCloak => _cloakHidden;

        /// <summary>False after <see cref="SetVisible"/>(false).</summary>
        public bool IsVisible { get; private set; } = true;

        /// <summary>True when the labelled "missing model" placeholder is shown instead of a realistic human.</summary>
        public bool IsPlaceholder { get; private set; }

        /// <summary>The hero definition the body was built from.</summary>
        public CharacterData Data { get; private set; }

        /// <summary>Current team accent colour.</summary>
        public Color TeamColor { get; private set; } = new Color(0.85f, 0.85f, 0.85f, 1f);

        /// <summary>Renderers of the highest level of detail (afterimages bake these).</summary>
        public Renderer[] HighDetailRenderers { get; private set; } = Array.Empty<Renderer>();

        /// <summary>The flat team ring on the floor (may be null).</summary>
        public Transform TeamRing => _ring;

        // ------------------------------------------------------------------ internals

        private static readonly int s_unlitColor = Shader.PropertyToID("_UnlitColor");
        private static readonly int s_baseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int s_color = Shader.PropertyToID("_Color");
        private static readonly int s_tintColor = Shader.PropertyToID("_TintColor");

        private static Mesh s_ringMesh;
        private static Texture2D s_ringTexture;
        private static readonly Dictionary<int, Material> s_afterimageMaterials = new Dictionary<int, Material>(8);
        private static readonly HashSet<string> s_missingModelWarnings = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<Renderer> s_rendererScratch = new List<Renderer>(16);
        private static readonly List<Mesh> s_meshScratch = new List<Mesh>(16);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_ringMesh = null;
            s_ringTexture = null;
            s_afterimageMaterials.Clear();
            s_missingModelWarnings.Clear();
        }

        // Ring.
        private Transform _ring;
        private MeshRenderer _ringRenderer;
        private Material _ringMaterial;
        private bool _ringShown = true;

        // Cloak.
        private bool _cloakHidden;
        private bool _ghostApplied;
        private Material[][] _originalMaterials;
        private Material[][] _ghostMaterials;
        private ShadowCastingMode[] _originalShadows;

        // Freeze.
        private bool _iceBuilt;
        private float _iceAmount;
        private float _iceTarget;
        private Transform _iceRoot;
        private Transform[] _crystals = Array.Empty<Transform>();
        private Vector3[] _crystalScales = Array.Empty<Vector3>();
        private float[] _crystalDelays = Array.Empty<float>();
        private SkinnedMeshRenderer[] _frostShells = Array.Empty<SkinnedMeshRenderer>();
        private MaterialPropertyBlock _iceBlock;
        private int _frostColorId = -1;
        private bool _ownsFrozenTint;
        private VfxHandle _ownMist;

        // Placeholder label.
        private Transform _label;
        private Canvas _labelCanvas;

        // Animator parameter mirroring (CopyAnimatorStateTo).
        private AnimatorControllerParameter[] _parameters = Array.Empty<AnimatorControllerParameter>();
        private RuntimeAnimatorController _parameterController;
        private bool[] _triggerWasSet = Array.Empty<bool>();
        private bool[] _triggerEdge = Array.Empty<bool>();
        private int _triggerFrame = -1;

        // ------------------------------------------------------------------ contract: build

        /// <summary>Instantiates the model and all visual sub-components for <paramref name="owner"/>.</summary>
        public void Build(DodgeballPlayer owner, CharacterData data)
        {
            Teardown();
            Owner = owner;
            Data = data;
            gameObject.layer = GameLayers.Visual;

            var pivot = new GameObject("LeanPivot") { layer = GameLayers.Visual };
            LeanPivot = pivot.transform;
            LeanPivot.SetParent(transform, false);
            LeanPivot.localPosition = Vector3.zero; // at the feet: the body tilts around the planted soles
            LeanPivot.localRotation = Quaternion.identity;

            GameObject model = null;
            if (data != null && data.modelPrefab != null)
            {
                try
                {
                    model = InstantiateModel(data);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                    if (model != null) HumanoidUtil.DestroySafe(model);
                    model = null;
                    Animator = null;
                    HasHumanoidModel = false;
                }
            }
            if (model == null) model = BuildPlaceholder(data);
            ModelRoot = model.transform;

            CollectRenderers(model);
            if (HasHumanoidModel)
            {
                RightHandSocket = CreateHandSocket(true);
                LeftHandSocket = CreateHandSocket(false);
            }

            // Sub-components live on the Animator's GameObject (OnAnimatorIK is only sent there).
            // Order matters: the ragdoll is built on the untouched bind pose, and the IK controller looks the ragdoll up on
            // the Animator's GameObject during its own Initialize (it must exist by then).
            GameObject host = Animator != null ? Animator.gameObject : model;
            HitFlash = HumanoidUtil.GetOrAdd<HitFlash>(host);
            HitFlash.Initialize(Renderers);
            Ragdoll = HumanoidUtil.GetOrAdd<RagdollController>(host);
            Ragdoll.Initialize(owner, Animator);
            AnimatorDriver = HumanoidUtil.GetOrAdd<PlayerAnimatorDriver>(host);
            AnimatorDriver.Initialize(owner, Animator);
            IK = HumanoidUtil.GetOrAdd<CharacterIKController>(host);
            IK.Initialize(owner, Animator);
            Lean = HumanoidUtil.GetOrAdd<ProceduralLean>(gameObject);
            Lean.Initialize(owner, LeanPivot);

            BuildTeamRing();
            IsVisible = true;
            ApplyRendererState();
            UpdateRingColor();
        }

        /// <summary>Humanoid bone transform (null when unavailable).</summary>
        public Transform GetBone(HumanBodyBones bone)
        {
            if (!HasHumanoidModel || Animator == null || bone < 0 || bone >= HumanBodyBones.LastBone) return null;
            return Animator.GetBoneTransform(bone);
        }

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            ApplyRendererState();
            UpdateRing();
        }

        /// <summary>
        /// Gale's Optical Camouflage. Enemies of the local player see an almost invisible refraction shimmer; allies and
        /// the owner see a translucent ghost so they keep control.
        /// </summary>
        public void SetCloaked(bool cloaked)
        {
            if (cloaked == IsCloaked)
            {
                // Re-evaluate the viewpoint (the local player may have changed since): cheap and idempotent.
                if (cloaked) ApplyCloakView(IsEnemyOfLocalView());
                return;
            }

            IsCloaked = cloaked;
            if (cloaked)
            {
                ApplyCloakView(IsEnemyOfLocalView());
            }
            else
            {
                if (_ghostApplied) ApplyGhostMaterials(false);
                _cloakHidden = false;
                ApplyRendererState();
            }
            UpdateRingColor();
            UpdateRing();

            // Engage / disengage shimmer over the body (spawned at the feet).
            if (Owner != null) VfxManager.Spawn(VfxId.CloakShimmer, Owner.Position, Quaternion.identity);
        }

        /// <summary>Ice encasing look + animator pause (Elsa freeze).</summary>
        public void SetFrozen(bool frozen)
        {
            if (IsFrozen == frozen) return;
            IsFrozen = frozen;

            if (AnimatorDriver != null) AnimatorDriver.SetPaused(frozen);
            else if (Animator != null) Animator.speed = frozen ? 0f : 1f;

            if (frozen)
            {
                if (!_iceBuilt) BuildIce();
                _iceTarget = 1f;
                if (_iceRoot != null) _iceRoot.gameObject.SetActive(IsRenderable);
            }
            else
            {
                _iceTarget = 0f;
            }

            // The StatusEffectController owns the cold tint and the mist while the Frozen status drives the freeze;
            // a direct call (tests, abilities, cutscenes) gets its own so the look is complete either way.
            bool statusOwned = Owner != null && Owner.Status != null && Owner.Status.Has(StatusEffectType.Frozen);
            if (frozen && !statusOwned)
            {
                if (HitFlash != null && HitFlash.TintAmount <= 0f)
                {
                    HitFlash.SetTint(frozenTint, frozenTintAmount);
                    _ownsFrozenTint = true;
                }
                if (!_ownMist.IsValid) _ownMist = VfxManager.SpawnAttached(VfxId.FrozenMist, transform, Vector3.up * 0.9f);
            }
            else if (!frozen)
            {
                ReleaseOwnFreezeEffects();
            }
        }

        /// <summary>Team colour indicator (ground ring / rim accent).</summary>
        public void SetTeamColor(Color color)
        {
            TeamColor = color;
            UpdateRingColor();
        }

        /// <summary>
        /// Shadow's Decoy Dash: bakes the current skinned pose into a static, fading translucent afterimage.
        /// Returns the spawned object (destroys itself after <paramref name="lifetime"/>).
        /// </summary>
        public GameObject SpawnAfterimage(float lifetime, Color tint)
        {
            if (ModelRoot == null || HighDetailRenderers.Length == 0) return null;

            var root = new GameObject(Owner != null ? "Afterimage_" + Owner.DisplayName : "Afterimage") { layer = GameLayers.Visual };
            Material ghost = GetAfterimageMaterial(tint);
            int colorId = HumanoidUtil.FindColorProperty(ghost, out Color baseColor);

            s_rendererScratch.Clear();
            s_meshScratch.Clear();
            for (int i = 0; i < HighDetailRenderers.Length; i++)
            {
                Renderer source = HighDetailRenderers[i];
                if (source == null || !source.gameObject.activeInHierarchy) continue;

                Mesh mesh = null;
                bool owned = false;
                if (source is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    mesh = new Mesh { name = skinned.sharedMesh.name + " (afterimage)" };
                    skinned.BakeMesh(mesh, true); // vertices in the renderer's local space incl. scale -> same TRS below
                    owned = true;
                }
                else
                {
                    var filter = source.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                if (mesh == null) continue;

                Transform src = source.transform;
                var part = new GameObject(source.name) { layer = GameLayers.Visual };
                part.transform.SetParent(root.transform, false);
                part.transform.SetPositionAndRotation(src.position, src.rotation);
                part.transform.localScale = src.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = FilledMaterialArray(ghost, Mathf.Max(1, mesh.subMeshCount));
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                s_rendererScratch.Add(renderer);
                if (owned) s_meshScratch.Add(mesh);
            }

            var fader = root.AddComponent<AfterimageFader>();
            fader.Initialize(lifetime, s_rendererScratch.ToArray(), s_meshScratch.ToArray(), colorId, baseColor);
            s_rendererScratch.Clear();
            s_meshScratch.Clear();
            return root;
        }

        /// <summary>
        /// Shadow's clones / Mirage Formation: a fully animated copy of the model (same controller) with no gameplay
        /// components. The caller drives its position and copies animator parameters via <see cref="CopyAnimatorStateTo"/>.
        /// </summary>
        public GameObject CreateAnimatedClone(string name)
        {
            if (ModelRoot == null) return null;

            GameObject clone;
            if (!IsPlaceholder && Data != null && Data.modelPrefab != null)
            {
                clone = Instantiate(Data.modelPrefab);
                clone.transform.localScale = Data.modelPrefab.transform.localScale * Mathf.Max(0.1f, Data.modelScale);
                if (!clone.activeSelf) clone.SetActive(true);
                SanitizeModel(clone);

                Animator animator = FindOrAddAnimator(clone);
                RuntimeAnimatorController controller = Animator != null && Animator.runtimeAnimatorController != null
                    ? Animator.runtimeAnimatorController
                    : Data.animatorController;
                if (controller != null) animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
            }
            else
            {
                // Placeholder body: copy what is shown (capsule + label).
                clone = Instantiate(ModelRoot.gameObject);
                HumanoidUtil.StripCharacterComponents(clone);
                HumanoidUtil.StripPhysics(clone);
                clone.transform.localScale = ModelRoot.lossyScale;
            }

            clone.name = string.IsNullOrEmpty(name) ? "CharacterClone" : name;
            clone.transform.SetPositionAndRotation(transform.position, transform.rotation);
            return clone;
        }

        /// <summary>Copies the owner's animator parameters onto a clone animator (mirror throw/catch animations).</summary>
        public void CopyAnimatorStateTo(Animator cloneAnimator)
        {
            Animator source = Animator;
            if (cloneAnimator == null || source == null || cloneAnimator == source) return;
            if (!source.isActiveAndEnabled || !cloneAnimator.isActiveAndEnabled) return;
            RuntimeAnimatorController controller = source.runtimeAnimatorController;
            if (controller == null || cloneAnimator.runtimeAnimatorController != controller) return; // parameters would not match
            if (!EnsureParameterCache(source)) return;

            // Trigger edges are computed once per frame so every clone sees the same throws / catches.
            int frame = Time.frameCount;
            bool computeEdges = _triggerFrame != frame;
            _triggerFrame = frame;

            for (int i = 0; i < _parameters.Length; i++)
            {
                AnimatorControllerParameter p = _parameters[i];
                int hash = p.nameHash;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        cloneAnimator.SetFloat(hash, source.GetFloat(hash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        cloneAnimator.SetInteger(hash, source.GetInteger(hash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        cloneAnimator.SetBool(hash, source.GetBool(hash));
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        if (computeEdges)
                        {
                            bool set = source.GetBool(hash);
                            _triggerEdge[i] = set && !_triggerWasSet[i];
                            _triggerWasSet[i] = set;
                        }
                        if (_triggerEdge[i]) cloneAnimator.SetTrigger(hash);
                        break;
                }
            }
            cloneAnimator.speed = source.speed; // frozen / slowed bodies pause their mirrors too
        }

        /// <summary>Called by DodgeballPlayer on round reset: un-ragdoll, un-freeze, visible, animator reset.</summary>
        public void ResetVisual()
        {
            if (Ragdoll != null) Ragdoll.ResetImmediate();

            // Freeze off immediately (no melt animation across a round reset).
            IsFrozen = false;
            _iceTarget = 0f;
            _iceAmount = 0f;
            ApplyIce();
            ReleaseOwnFreezeEffects();

            if (IsCloaked || _ghostApplied || _cloakHidden)
            {
                if (_ghostApplied) ApplyGhostMaterials(false);
                IsCloaked = false;
                _cloakHidden = false;
            }

            IsVisible = true;
            ApplyRendererState();
            if (HitFlash != null) HitFlash.ClearAll();
            if (Lean != null) Lean.ResetLean();
            if (IK != null) IK.ResetIK();
            if (AnimatorDriver != null) AnimatorDriver.ResetAnimator(); // unpauses, rebinds, evaluates the idle pose
            else if (Animator != null) Animator.speed = 1f;
            UpdateRingColor();
            UpdateRing();
        }

        // ------------------------------------------------------------------ lifecycle

        private void LateUpdate()
        {
            if (_iceAmount != _iceTarget)
            {
                float dt = Time.unscaledDeltaTime;
                float rate = _iceTarget > _iceAmount ? 1f / Mathf.Max(0.01f, iceGrowTime) : 1f / Mathf.Max(0.01f, iceMeltTime);
                _iceAmount = Mathf.MoveTowards(_iceAmount, _iceTarget, rate * dt);
                ApplyIce();
            }

            UpdateRing();

            if (_label != null && _labelCanvas != null && _labelCanvas.enabled)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 away = _label.position - cam.transform.position;
                    if (away.sqrMagnitude > 1e-6f) _label.rotation = Quaternion.LookRotation(away, Vector3.up);
                }
            }
        }

        private void OnDestroy()
        {
            ReleaseOwnFreezeEffects();
            if (_ringMaterial != null) HumanoidUtil.DestroySafe(_ringMaterial);
            _ringMaterial = null;
        }

        // ------------------------------------------------------------------ build helpers

        private void Teardown()
        {
            ReleaseOwnFreezeEffects();
            if (LeanPivot != null) HumanoidUtil.DestroySafe(LeanPivot.gameObject);
            if (_ring != null) HumanoidUtil.DestroySafe(_ring.gameObject);
            if (_iceRoot != null) HumanoidUtil.DestroySafe(_iceRoot.gameObject);
            if (_ringMaterial != null) HumanoidUtil.DestroySafe(_ringMaterial);

            LeanPivot = null;
            ModelRoot = null;
            Animator = null;
            HasHumanoidModel = false;
            IsPlaceholder = false;
            RightHandSocket = null;
            LeftHandSocket = null;
            Renderers = Array.Empty<Renderer>();
            HighDetailRenderers = Array.Empty<Renderer>();
            AnimatorDriver = null;
            IK = null;
            HitFlash = null;
            Ragdoll = null;

            _ring = null;
            _ringRenderer = null;
            _ringMaterial = null;
            _ringShown = true;
            _cloakHidden = false;
            _ghostApplied = false;
            _originalMaterials = null;
            _ghostMaterials = null;
            _originalShadows = null;
            _iceBuilt = false;
            _iceAmount = 0f;
            _iceTarget = 0f;
            _iceRoot = null;
            _crystals = Array.Empty<Transform>();
            _crystalScales = Array.Empty<Vector3>();
            _crystalDelays = Array.Empty<float>();
            _frostShells = Array.Empty<SkinnedMeshRenderer>();
            _label = null;
            _labelCanvas = null;
            _parameters = Array.Empty<AnimatorControllerParameter>();
            _parameterController = null;
            IsFrozen = false;
            IsCloaked = false;
        }

        private GameObject InstantiateModel(CharacterData data)
        {
            GameObject model = Instantiate(data.modelPrefab, LeanPivot, false);
            model.name = "Model";
            Transform t = model.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = data.modelPrefab.transform.localScale * Mathf.Max(0.1f, data.modelScale);
            if (!model.activeSelf) model.SetActive(true);
            SanitizeModel(model);

            Animator animator = FindOrAddAnimator(model);
            if (data.animatorController != null) animator.runtimeAnimatorController = data.animatorController;
            animator.applyRootMotion = false;                          // the motor owns the position
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;  // IK / ragdoll velocities need a fresh pose
            animator.updateMode = AnimatorUpdateMode.Normal;           // scaled time: hitstop freezes the body
            animator.enabled = true;
            Animator = animator;
            HasHumanoidModel = HumanoidUtil.IsValidHumanoid(animator);
            IsPlaceholder = false;

            if (!HasHumanoidModel)
            {
                Debug.LogWarning($"[Dodgeball Ultra] '{data.displayName}': model '{data.modelPrefab.name}' has no valid Humanoid " +
                                 "avatar (set Rig > Animation Type = Humanoid). IK, ragdoll and retargeted motion capture are " +
                                 "disabled for this hero.", data);
            }
            if (animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning($"[Dodgeball Ultra] '{data.displayName}' has no animator controller: the body will not animate. " +
                                 "Run Dodgeball Ultra > Setup Wizard to generate the motion-capture controllers.", data);
            }
            return model;
        }

        /// <summary>Physics, gameplay components, cameras and listeners out; visual layer; LODs for raw exports.</summary>
        private static void SanitizeModel(GameObject model)
        {
            HumanoidUtil.StripCharacterComponents(model);
            HumanoidUtil.StripPhysics(model);
            var cameras = model.GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++) Object.DestroyImmediate(cameras[i]);
            var listeners = model.GetComponentsInChildren<AudioListener>(true);
            for (int i = 0; i < listeners.Length; i++) Object.DestroyImmediate(listeners[i]);
            GameLayers.SetLayerRecursively(model, GameLayers.Visual);
            HumanoidUtil.EnsureLodGroup(model);
        }

        private static Animator FindOrAddAnimator(GameObject model)
        {
            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = model.AddComponent<Animator>();
            return animator;
        }

        private void CollectRenderers(GameObject model)
        {
            var list = new List<Renderer>(8);
            var all = model.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] is SkinnedMeshRenderer || all[i] is MeshRenderer) list.Add(all[i]);
            Renderers = list.ToArray();

            var high = new List<Renderer>(4);
            HumanoidUtil.CollectHighestDetailRenderers(model, high);
            HighDetailRenderers = high.ToArray();
        }

        private Transform CreateHandSocket(bool right)
        {
            Transform hand = GetBone(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (hand == null) return null;

            var go = new GameObject(right ? "RightHandSocket" : "LeftHandSocket") { layer = GameLayers.Visual };
            Transform socket = go.transform;
            socket.SetParent(hand, false);

            PalmFrame palm = HumanoidUtil.ComputePalmFrame(Animator, right);
            if (palm.Valid)
            {
                // Ball centre one radius off the palm (slightly less: the foam and the fingers give a little).
                float scale = HumanoidUtil.UniformScale(hand);
                socket.localPosition = palm.CenterLocal + palm.NormalLocal * (Core.GameConstants.BallRadius * 0.92f / scale);
                socket.localRotation = Quaternion.LookRotation(palm.FingerLocal, palm.NormalLocal);
            }
            else
            {
                socket.localPosition = Vector3.zero;
                socket.localRotation = Quaternion.identity;
            }
            return socket;
        }

        // ------------------------------------------------------------------ placeholder

        private GameObject BuildPlaceholder(CharacterData data)
        {
            IsPlaceholder = true;
            HasHumanoidModel = false;
            Animator = null;

            string heroName = data != null ? data.displayName : (Owner != null ? Owner.DisplayName : "Hero");
            if (s_missingModelWarnings.Add(heroName ?? string.Empty))
            {
                Debug.LogWarning($"[Dodgeball Ultra] Hero '{heroName}' has no realistic human model (CharacterData.modelPrefab is " +
                                 "empty). A labelled placeholder is shown instead. Run 'Dodgeball Ultra > Setup Wizard' to download " +
                                 "the Microsoft Rocketbox avatars and generate the hero models. Never ship with the placeholder.",
                    data != null ? (Object)data : this);
            }

            float height = data != null ? data.height : 1.8f;
            float radius = data != null ? data.radius : 0.32f;

            var root = new GameObject("Model (MISSING - placeholder)") { layer = GameLayers.Visual };
            root.transform.SetParent(LeanPivot, false);

            // Translucent magenta capsule: the universal "missing asset" colour, never mistakable for a character.
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var collider = capsule.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            capsule.name = "PlaceholderCapsule";
            capsule.layer = GameLayers.Visual;
            capsule.transform.SetParent(root.transform, false);
            capsule.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            capsule.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            var capsuleRenderer = capsule.GetComponent<MeshRenderer>();
            capsuleRenderer.sharedMaterial = MaterialFactory.GetOrCreate("DU_MissingModelPlaceholder",
                () => MaterialFactory.CreateGhost("DU_MissingModelPlaceholder", new Color(1f, 0.2f, 0.75f, 0.4f)));
            capsuleRenderer.shadowCastingMode = ShadowCastingMode.Off;

            BuildPlaceholderLabel(root.transform, height, heroName);
            return root;
        }

        private void BuildPlaceholderLabel(Transform parent, float height, string heroName)
        {
            try
            {
                var canvasGo = new GameObject("MissingModelLabel", typeof(RectTransform)) { layer = GameLayers.Visual };
                var rect = (RectTransform)canvasGo.transform;
                rect.SetParent(parent, false);
                rect.localPosition = new Vector3(0f, height + 0.35f, 0f);
                rect.localScale = Vector3.one * 0.0045f; // ~220 px per metre
                rect.sizeDelta = new Vector2(460f, 170f);
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.sortingOrder = 20;

                var textGo = new GameObject("Text", typeof(RectTransform)) { layer = GameLayers.Visual };
                var textRect = (RectTransform)textGo.transform;
                textRect.SetParent(rect, false);
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                var text = textGo.AddComponent<Text>();
                text.font = UI.UiFactory.DefaultFont;
                text.fontSize = 34;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                text.color = new Color(1f, 0.55f, 0.85f, 1f);
                text.text = "MISSING HERO MODEL\n" + (heroName ?? "Hero") + "\nRun Dodgeball Ultra > Setup Wizard";
                var shadow = textGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
                shadow.effectDistance = new Vector2(2f, -2f);

                _label = rect;
                _labelCanvas = canvas;
            }
            catch (Exception e)
            {
                Debug.LogException(e, this); // the capsule alone still makes the problem obvious
            }
        }

        // ------------------------------------------------------------------ renderer state

        private bool IsRenderable => IsVisible && !_cloakHidden;

        /// <summary>Applies visibility (SetVisible, enemy cloak) to the body, the ice and the placeholder label.</summary>
        private void ApplyRendererState()
        {
            bool show = IsRenderable;
            for (int i = 0; i < Renderers.Length; i++)
                if (Renderers[i] != null) Renderers[i].enabled = show;
            for (int i = 0; i < _frostShells.Length; i++)
                if (_frostShells[i] != null) _frostShells[i].enabled = show && _iceAmount > 0.001f;
            if (_iceRoot != null) _iceRoot.gameObject.SetActive(show && _iceAmount > 0.001f);
            if (_labelCanvas != null) _labelCanvas.enabled = show;
        }

        private bool IsEnemyOfLocalView()
        {
            DodgeballPlayer local = PlayerRegistry.LocalPlayer;
            return local != null && Owner != null && local != Owner && PlayerRegistry.AreEnemies(local, Owner);
        }

        private void ApplyCloakView(bool hiddenFromLocal)
        {
            if (hiddenFromLocal)
            {
                if (_ghostApplied) ApplyGhostMaterials(false);
                _cloakHidden = true;
            }
            else
            {
                _cloakHidden = false;
                if (!_ghostApplied) ApplyGhostMaterials(true);
            }
            ApplyRendererState();
            UpdateRing();
        }

        /// <summary>Swaps every body renderer to the shared translucent ghost material (and back). Arrays cached once.</summary>
        private void ApplyGhostMaterials(bool ghost)
        {
            if (ghost)
            {
                if (_originalMaterials == null || _originalMaterials.Length != Renderers.Length)
                {
                    Material ghostMaterial = MaterialFactory.GetOrCreate("DU_CloakGhost",
                        () => MaterialFactory.CreateGhost("DU_CloakGhost", cloakGhostTint));
                    _originalMaterials = new Material[Renderers.Length][];
                    _ghostMaterials = new Material[Renderers.Length][];
                    _originalShadows = new ShadowCastingMode[Renderers.Length];
                    for (int i = 0; i < Renderers.Length; i++)
                    {
                        Renderer r = Renderers[i];
                        _originalMaterials[i] = r != null ? r.sharedMaterials : Array.Empty<Material>();
                        _ghostMaterials[i] = FilledMaterialArray(ghostMaterial, Mathf.Max(1, _originalMaterials[i].Length));
                    }
                }
                for (int i = 0; i < Renderers.Length; i++)
                {
                    Renderer r = Renderers[i];
                    if (r == null) continue;
                    _originalShadows[i] = r.shadowCastingMode;
                    r.sharedMaterials = _ghostMaterials[i];
                    r.shadowCastingMode = ShadowCastingMode.Off; // a glassy ghost casts no solid shadow
                }
                _ghostApplied = true;
            }
            else
            {
                if (_originalMaterials != null)
                {
                    for (int i = 0; i < Renderers.Length && i < _originalMaterials.Length; i++)
                    {
                        Renderer r = Renderers[i];
                        if (r == null) continue;
                        r.sharedMaterials = _originalMaterials[i];
                        r.shadowCastingMode = _originalShadows[i];
                    }
                }
                _ghostApplied = false;
            }
        }

        private static Material[] FilledMaterialArray(Material material, int count)
        {
            var array = new Material[count];
            for (int i = 0; i < count; i++) array[i] = material;
            return array;
        }

        // ------------------------------------------------------------------ team ring

        private void BuildTeamRing()
        {
            var go = new GameObject("TeamRing") { layer = GameLayers.Visual };
            _ring = go.transform;
            _ring.SetParent(transform, false); // not under the lean pivot: always flat on the floor
            _ring.localPosition = new Vector3(0f, ringHeight, 0f);
            _ring.localRotation = Quaternion.identity;
            _ring.localScale = Vector3.one * ringRadius;

            go.AddComponent<MeshFilter>().sharedMesh = RingMesh;
            _ringRenderer = go.AddComponent<MeshRenderer>();
            _ringMaterial = MaterialFactory.CreateParticle("DU_TeamRing", TeamColor, false, RingTexture);
            _ringRenderer.sharedMaterial = _ringMaterial;
            _ringRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _ringRenderer.receiveShadows = false;
            _ringRenderer.lightProbeUsage = LightProbeUsage.Off;
            _ringRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _ringShown = true;
        }

        private void UpdateRingColor()
        {
            if (_ringMaterial == null) return;
            float alpha = Owner != null && Owner.IsLocalPlayer ? localRingOpacity : ringOpacity;
            if (IsCloaked) alpha *= 0.35f;
            Color c = TeamColor;
            c.a = Mathf.Clamp01(alpha);
            SetMaterialColor(_ringMaterial, c);
        }

        private void UpdateRing()
        {
            if (_ringRenderer == null) return;
            bool ragdolled = Ragdoll != null && Ragdoll.IsRagdolled;
            bool show = IsRenderable && !ragdolled;
            if (show == _ringShown) return;
            _ringShown = show;
            _ringRenderer.enabled = show;
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty(s_unlitColor)) material.SetColor(s_unlitColor, color);
            if (material.HasProperty(s_baseColor)) material.SetColor(s_baseColor, color);
            if (material.HasProperty(s_color)) material.SetColor(s_color, color);
            // Legacy particle shaders double the tint.
            if (material.HasProperty(s_tintColor)) material.SetColor(s_tintColor, new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, color.a * 0.5f));
        }

        /// <summary>Unit ring (inner radius 0.72, outer 1) shared by every player; scaled by <see cref="ringRadius"/>.</summary>
        private static Mesh RingMesh
        {
            get
            {
                if (s_ringMesh != null) return s_ringMesh;
                s_ringMesh = CharacterMeshUtil.CreateRing("DU_TeamRing", 0.72f, 1f, 72);
                s_ringMesh.hideFlags = HideFlags.DontSave;
                return s_ringMesh;
            }
        }

        /// <summary>Radial alpha profile (V = inner to outer edge): soft inner fade, crisp bright band, soft outer edge.</summary>
        private static Texture2D RingTexture
        {
            get
            {
                if (s_ringTexture != null) return s_ringTexture;
                const int height = 64;
                var tex = new Texture2D(4, height, TextureFormat.RGBA32, false, false)
                {
                    name = "DU_TeamRingProfile",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                var pixels = new Color32[4 * height];
                for (int y = 0; y < height; y++)
                {
                    float v = (y + 0.5f) / height;
                    float body = Mathf.SmoothStep(0f, 1f, v / 0.55f) * 0.55f;
                    float band = Mathf.Exp(-Mathf.Pow((v - 0.8f) / 0.08f, 2f));
                    float edge = 1f - Mathf.SmoothStep(0.88f, 1f, v);
                    byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * Mathf.Clamp01((body + band) * edge)), 0, 255);
                    for (int x = 0; x < 4; x++) pixels[y * 4 + x] = new Color32(255, 255, 255, a);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                s_ringTexture = tex;
                return tex;
            }
        }

        // ------------------------------------------------------------------ freeze

        /// <summary>Builds the ice coat (skinned copies in the same LOD levels) and the crystals (once, on first freeze).</summary>
        private void BuildIce()
        {
            _iceBuilt = true;
            _iceBlock ??= new MaterialPropertyBlock();

            Material frost = MaterialFactory.GetOrCreate("DU_FrostCoat", () =>
            {
                var desc = LitMaterialDesc.Opaque(frostColor, 0.94f, 0f);
                desc.Transparent = true;
                desc.EmissiveColor = new Color(0.35f, 0.6f, 0.85f, 1f);
                desc.EmissiveIntensity = 20f; // nits: a faint inner cold glow that keeps the ice readable in shadow
                return MaterialFactory.CreateLit("DU_FrostCoat", in desc);
            });
            _frostColorId = HumanoidUtil.FindColorProperty(frost, out _);

            // ---- ice coat over every skinned mesh
            var shells = new List<SkinnedMeshRenderer>(Renderers.Length);
            var shellOf = new Dictionary<Renderer, Renderer>(Renderers.Length);
            for (int i = 0; i < Renderers.Length; i++)
            {
                if (!(Renderers[i] is SkinnedMeshRenderer source) || source.sharedMesh == null) continue;
                Transform st = source.transform;
                var go = new GameObject(source.name + "_Ice") { layer = GameLayers.Visual };
                go.transform.SetParent(st.parent, false);
                go.transform.localPosition = st.localPosition;
                go.transform.localRotation = st.localRotation;
                go.transform.localScale = st.localScale;
                var shell = go.AddComponent<SkinnedMeshRenderer>();
                shell.sharedMesh = source.sharedMesh;
                shell.rootBone = source.rootBone;
                shell.bones = source.bones;
                shell.localBounds = source.localBounds;
                shell.quality = source.quality;
                shell.updateWhenOffscreen = source.updateWhenOffscreen;
                shell.sharedMaterials = FilledMaterialArray(frost, Mathf.Max(1, source.sharedMesh.subMeshCount));
                shell.shadowCastingMode = ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.enabled = false;
                shells.Add(shell);
                shellOf[source] = shell;
            }
            _frostShells = shells.ToArray();

            // Keep the coat in the same LOD level as the mesh it covers (no hi-poly coat over a low-poly body).
            LODGroup group = ModelRoot != null ? ModelRoot.GetComponentInChildren<LODGroup>(true) : null;
            if (group != null && shellOf.Count > 0)
            {
                LOD[] lods = group.GetLODs();
                for (int l = 0; l < lods.Length; l++)
                {
                    Renderer[] current = lods[l].renderers ?? Array.Empty<Renderer>();
                    var extended = new List<Renderer>(current.Length * 2);
                    extended.AddRange(current);
                    for (int r = 0; r < current.Length; r++)
                        if (current[r] != null && shellOf.TryGetValue(current[r], out Renderer shell)) extended.Add(shell);
                    lods[l].renderers = extended.ToArray();
                }
                group.SetLODs(lods);
            }

            // ---- crystals growing from the floor around the legs (under the Visual: they do not lean)
            Material iceMaterial = MaterialFactory.GetOrCreate("DU_IceCrystal", () =>
            {
                var desc = LitMaterialDesc.Opaque(iceCrystalColor, 0.96f, 0f);
                desc.Transparent = true;
                desc.EmissiveColor = new Color(0.4f, 0.65f, 0.9f, 1f);
                desc.EmissiveIntensity = 30f;
                return MaterialFactory.CreateLit("DU_IceCrystal", in desc);
            });

            var rootGo = new GameObject("Ice") { layer = GameLayers.Visual };
            _iceRoot = rootGo.transform;
            _iceRoot.SetParent(transform, false);
            _iceRoot.localPosition = Vector3.zero;
            _iceRoot.localRotation = Quaternion.identity;

            float bodyRadius = Data != null ? Data.radius : 0.32f;
            float bodyHeight = Data != null ? Data.height : 1.8f;
            int count = Mathf.Max(0, iceCrystalCount);
            _crystals = new Transform[count];
            _crystalScales = new Vector3[count];
            _crystalDelays = new float[count];
            var rng = new System.Random(Owner != null ? Owner.PlayerId * 7919 + 17 : 17);
            Mesh crystalMesh = CharacterMeshUtil.IceCrystal;
            for (int i = 0; i < count; i++)
            {
                bool inner = i % 3 == 0; // every third crystal hugs the shins: reads as the legs being encased
                float angle = (i + (float)rng.NextDouble() * 0.6f) / Mathf.Max(1, count) * 360f;
                float r = (inner ? 0.35f : 0.7f + 0.35f * (float)rng.NextDouble()) * bodyRadius;
                float h = (inner ? 0.42f : 0.18f + 0.22f * (float)rng.NextDouble()) * bodyHeight;
                float w = (inner ? 0.075f : 0.06f + 0.06f * (float)rng.NextDouble()) * (bodyHeight / 1.8f);
                float tilt = inner ? 6f + 8f * (float)rng.NextDouble() : 14f + 18f * (float)rng.NextDouble();

                var crystalGo = new GameObject("IceCrystal") { layer = GameLayers.Visual };
                Transform ct = crystalGo.transform;
                ct.SetParent(_iceRoot, false);
                Quaternion yaw = Quaternion.Euler(0f, angle, 0f);
                ct.localPosition = yaw * new Vector3(0f, -0.03f, r);
                ct.localRotation = yaw * Quaternion.Euler(tilt, (float)rng.NextDouble() * 60f, 0f); // leans outward
                ct.localScale = Vector3.zero;
                crystalGo.AddComponent<MeshFilter>().sharedMesh = crystalMesh;
                var mr = crystalGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = iceMaterial;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
                _crystals[i] = ct;
                _crystalScales[i] = new Vector3(w, h, w);
                _crystalDelays[i] = (float)rng.NextDouble() * 0.35f;
            }
            _iceRoot.gameObject.SetActive(false);
        }

        /// <summary>Applies the current ice amount (crystal growth, coat opacity, visibility).</summary>
        private void ApplyIce()
        {
            float amount = _iceAmount;
            bool visible = amount > 0.001f && IsRenderable;

            if (_iceRoot != null)
            {
                if (_iceRoot.gameObject.activeSelf != visible) _iceRoot.gameObject.SetActive(visible);
                if (visible)
                {
                    for (int i = 0; i < _crystals.Length; i++)
                    {
                        if (_crystals[i] == null) continue;
                        // Staggered growth: each crystal starts a little later and overshoots slightly (crystallisation).
                        float d = _crystalDelays[i];
                        float k = Mathf.Clamp01((amount - d) / Mathf.Max(0.05f, 1f - d));
                        float eased = 1f - (1f - k) * (1f - k) * (1f - k);
                        _crystals[i].localScale = _crystalScales[i] * eased;
                    }
                }
            }

            if (_frostShells.Length > 0)
            {
                Color c = frostColor;
                c.a = frostColor.a * Mathf.Clamp01(amount);
                for (int i = 0; i < _frostShells.Length; i++)
                {
                    SkinnedMeshRenderer shell = _frostShells[i];
                    if (shell == null) continue;
                    shell.enabled = visible;
                    if (!visible || _frostColorId == -1) continue;
                    shell.GetPropertyBlock(_iceBlock);
                    _iceBlock.SetColor(_frostColorId, c);
                    shell.SetPropertyBlock(_iceBlock);
                }
            }
        }

        private void ReleaseOwnFreezeEffects()
        {
            if (_ownsFrozenTint)
            {
                if (HitFlash != null) HitFlash.SetTint(frozenTint, 0f);
                _ownsFrozenTint = false;
            }
            if (_ownMist.IsValid) VfxManager.StopEffect(_ownMist);
            _ownMist = default;
        }

        // ------------------------------------------------------------------ afterimages / clones

        private static Material GetAfterimageMaterial(Color tint)
        {
            Color32 c = tint;
            int key = (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;
            if (s_afterimageMaterials.TryGetValue(key, out Material cached) && cached != null) return cached;
            Material created = MaterialFactory.CreateGhost("DU_Afterimage", tint);
            if (created != null) created.hideFlags = HideFlags.DontSave;
            s_afterimageMaterials[key] = created;
            return created;
        }

        /// <summary>Caches the controller's parameter list (Animator.parameters allocates) once per controller.</summary>
        private bool EnsureParameterCache(Animator source)
        {
            RuntimeAnimatorController controller = source.runtimeAnimatorController;
            if (controller == _parameterController && _parameters.Length > 0) return true;
            if (!source.isInitialized) return false;
            _parameters = source.parameters;
            _parameterController = controller;
            _triggerWasSet = new bool[_parameters.Length];
            _triggerEdge = new bool[_parameters.Length];
            _triggerFrame = -1;
            return _parameters.Length > 0;
        }
    }
}
