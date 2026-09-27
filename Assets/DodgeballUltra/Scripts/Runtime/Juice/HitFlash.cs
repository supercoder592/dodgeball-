using System.Collections.Generic;
using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - material-parameter flash via MaterialPropertyBlock (no material instancing). Pipeline-aware:
    /// HDRP Lit uses _EmissiveColor with exposure weight; URP/Built-in lerp _BaseColor/_Color toward the flash colour.
    /// Also supports a sustained tint (frozen blue, cloak) layered under flashes.
    /// <para>
    /// Property sets per pipeline (<see cref="RenderPipelineUtil.Current"/>, each property only if the material has it):
    /// <list type="bullet">
    /// <item><b>HDRP</b>: <c>_EmissiveColor = original + tint·tintEmission·amount + flash·flashEmission·w</c> (HDR) and
    /// <c>_EmissiveExposureWeight = 0</c> while anything is showing, so the emission is expressed relative to the current
    /// camera exposure and reads the same in a dark arena or in daylight (a physically authored original glow is left
    /// out while the effect shows, it would be over-bright in that space). The sustained tint also multiplies
    /// <c>_BaseColor</c> (keeps the albedo texture detail).</item>
    /// <item><b>URP</b>: <c>_BaseColor = lerp(original·tint, flashColour, w·baseFlashBlend)</c> and <c>_EmissionColor</c> as above
    /// (effective only when the material has emission enabled; the base-colour lerp always works).</item>
    /// <item><b>Built-in</b>: the same with <c>_Color</c> / <c>_EmissionColor</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Original values are cached from the shared materials (never instanced) and re-cached automatically if a renderer's
    /// materials are swapped (e.g. cloak / ghost materials). Blocks are read-modify-written so values other systems put
    /// in the same block survive. When the effect ends, a renderer that had no property block before is cleared again
    /// (so no stale value can override a material swapped in later, and SRP-Batcher compatibility returns); a renderer
    /// whose block is shared with another system gets the cached originals written back instead. A renderer-wide block
    /// is used when all sub-materials share identical originals (the common case); otherwise per-material blocks.
    /// </para>
    /// <para>Timing is unscaled: the flash is visible during the hitstop freeze frame. The component disables itself when
    /// idle (no per-frame cost) and re-enables on <see cref="Flash"/> / <see cref="SetTint"/>.</para>
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(310)]
    public sealed class HitFlash : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Flash")]
        [Tooltip("Flash weight over its normalised lifetime (0..1 -> 0..1). Default: fully on for 60 %, then a fast fade.")]
        public AnimationCurve flashCurve = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.6f, 1f), new Keyframe(1f, 0f));

        [Tooltip("HDR emission of a full-strength flash, in exposure-relative units (1 = display white before tonemapping).")]
        [Range(0f, 8f)] public float flashEmission = 1.6f;

        [Tooltip("How far the base colour is pushed toward the flash colour (URP / Built-in, and HDRP materials without emission).")]
        [Range(0f, 1f)] public float baseFlashBlend = 0.85f;

        [Header("Sustained tint")]
        [Tooltip("Emission of a full-strength tint (exposure-relative). Keeps frozen / cloaked players readable in shadow.")]
        [Range(0f, 4f)] public float tintEmission = 0.35f;

        [Tooltip("How strongly the tint multiplies the base colour at amount 1.")]
        [Range(0f, 1f)] public float tintBaseBlend = 0.75f;

        [Header("Renderers")]
        [Tooltip("When no renderers were supplied via Initialize, gather Mesh/SkinnedMesh renderers from the children.")]
        public bool autoGatherRenderers = true;

        // ------------------------------------------------------------------ state

        /// <summary>True while a flash is showing.</summary>
        public bool IsFlashing => _flashRemaining > 0f;

        /// <summary>Current sustained tint amount (0 = off).</summary>
        public float TintAmount => _tintAmount;

        private static readonly int s_emissiveColor = Shader.PropertyToID("_EmissiveColor");               // HDRP
        private static readonly int s_emissiveExposureWeight = Shader.PropertyToID("_EmissiveExposureWeight"); // HDRP
        private static readonly int s_baseColor = Shader.PropertyToID("_BaseColor");                       // HDRP / URP
        private static readonly int s_color = Shader.PropertyToID("_Color");                               // Built-in
        private static readonly int s_unlitColor = Shader.PropertyToID("_UnlitColor");                     // HDRP Unlit
        private static readonly int s_emissionColor = Shader.PropertyToID("_EmissionColor");               // URP / Built-in

        /// <summary>Cached originals and capabilities of one sub-material.</summary>
        private struct SlotData
        {
            public Material Material;
            public bool HasHdrpEmissive;
            public bool HasExposureWeight;
            public bool HasEmission;       // _EmissionColor
            public int BaseColorId;        // _BaseColor / _Color / _UnlitColor, 0 = none
            public Color OriginalBase;
            public Color OriginalEmissive; // _EmissiveColor (HDRP) or _EmissionColor
            public float OriginalExposureWeight;
        }

        /// <summary>One renderer and its sub-material slots.</summary>
        private sealed class RendererEntry
        {
            public Renderer Renderer;
            public SlotData[] Slots = System.Array.Empty<SlotData>();
            public bool PerMaterial;     // originals differ between sub-materials -> per-material property blocks
            public bool Dirty;           // we wrote effect values that must be released
            public bool SharedBlock;     // another system already had a property block when our effect started
        }

        private readonly List<RendererEntry> _entries = new List<RendererEntry>(8);
        private readonly List<Material> _materialScratch = new List<Material>(8);
        private MaterialPropertyBlock _block;
        private PipelineKind _pipeline;
        private bool _initialized;

        private Color _flashColor = Color.white;
        private float _flashDuration;
        private float _flashRemaining;
        private int _flashStartFrame = -1;

        private Color _tintColor = Color.white;
        private float _tintAmount;

        private bool _anyDirty;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Sets the renderers to flash (typically every LOD renderer of the character model). Null entries are ignored.
        /// Can be called again to replace the set (e.g. after a model swap); previous renderers are restored first.
        /// </summary>
        public void Initialize(Renderer[] renderers)
        {
            RestoreAll();
            _entries.Clear();
            _pipeline = DetectPipeline();

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null || ContainsRenderer(r)) continue;
                    var entry = new RendererEntry { Renderer = r };
                    BuildSlots(entry);
                    _entries.Add(entry);
                }
            }

            _initialized = true;
            if (_tintAmount > 0f || _flashRemaining > 0f) enabled = true;
        }

        /// <summary>Flash for <paramref name="duration"/> unscaled seconds (spec: 0.05 s white).</summary>
        public void Flash(Color color, float duration = Core.GameConstants.HitFlashDuration)
        {
            if (duration <= 0f) return;
            EnsureInitialized();
            _flashColor = color;
            _flashDuration = duration;
            _flashRemaining = duration; // a new flash restarts at full strength
            _flashStartFrame = Time.frameCount; // the frame it starts on renders at full strength (see LateUpdate)
            enabled = true;
            Apply(); // show on this very frame, even if our LateUpdate already ran
        }

        /// <summary>Sustained tint (amount 0 = off).</summary>
        public void SetTint(Color color, float amount)
        {
            EnsureInitialized();
            _tintColor = color;
            _tintAmount = Mathf.Clamp01(amount);
            enabled = true;
            Apply();
        }

        /// <summary>Stops the flash and the tint and restores the original material values.</summary>
        public void ClearAll()
        {
            _flashRemaining = 0f;
            _tintAmount = 0f;
            RestoreAll();
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            _block = new MaterialPropertyBlock();
        }

        private void OnDisable()
        {
            // If disabled externally while showing, leave the materials in their authored state.
            if (_anyDirty) RestoreAll();
        }

        private void OnDestroy()
        {
            if (_anyDirty) RestoreAll();
        }

        private void LateUpdate()
        {
            // Count down in unscaled time, but not on the frame the flash started: that frame's delta elapsed before
            // the hit happened (hits are usually resolved in FixedUpdate earlier in the same frame).
            if (_flashRemaining > 0f && Time.frameCount != _flashStartFrame)
                _flashRemaining = Mathf.Max(0f, _flashRemaining - Time.unscaledDeltaTime);

            Apply();

            // Idle: originals written back, nothing to animate -> stop ticking.
            if (_flashRemaining <= 0f && _tintAmount <= 0f && !_anyDirty) enabled = false;
        }

        // ------------------------------------------------------------------ core

        private void Apply()
        {
            if (!_initialized) return;
            if (_block == null) _block = new MaterialPropertyBlock();

            float w = 0f;
            if (_flashRemaining > 0f && _flashDuration > 0f)
            {
                float t = 1f - _flashRemaining / _flashDuration;
                w = Mathf.Clamp01(flashCurve != null && flashCurve.length > 0 ? flashCurve.Evaluate(t) : 1f);
            }
            float tint = _tintAmount;

            if (w <= 0f && tint <= 0f)
            {
                if (_anyDirty) RestoreAll();
                return;
            }

            bool anyWritten = false;
            for (int e = _entries.Count - 1; e >= 0; e--)
            {
                var entry = _entries[e];
                if (entry.Renderer == null)
                {
                    _entries.RemoveAt(e); // destroyed (LOD swap, model replaced)
                    continue;
                }

                RefreshSlotsIfMaterialsChanged(entry);
                if (entry.Slots.Length == 0) continue;

                // First write of this effect session: remember whether someone else owns a block on this renderer.
                if (!entry.Dirty) entry.SharedBlock = entry.Renderer.HasPropertyBlock();

                if (entry.PerMaterial)
                {
                    for (int i = 0; i < entry.Slots.Length; i++)
                    {
                        entry.Renderer.GetPropertyBlock(_block, i);
                        WriteEffect(_block, in entry.Slots[i], w, tint);
                        entry.Renderer.SetPropertyBlock(_block, i);
                    }
                }
                else
                {
                    entry.Renderer.GetPropertyBlock(_block);
                    WriteEffect(_block, in entry.Slots[0], w, tint);
                    entry.Renderer.SetPropertyBlock(_block);
                }
                entry.Dirty = true;
                anyWritten = true;
            }
            _anyDirty = anyWritten;
        }

        /// <summary>Writes flash weight <paramref name="w"/> and tint amount <paramref name="tint"/> for one slot.</summary>
        private void WriteEffect(MaterialPropertyBlock block, in SlotData slot, float w, float tint)
        {
            // Sustained tint: multiplicative on the base colour, plus a faint glow.
            Color tintMul = Color.Lerp(Color.white, _tintColor, tint * tintBaseBlend);
            Color tintGlow = _tintColor * (tintEmission * tint);
            Color flashGlow = _flashColor * (flashEmission * w);

            bool useHdrpEmissive = _pipeline == PipelineKind.HighDefinition && slot.HasHdrpEmissive;

            if (slot.BaseColorId != 0)
            {
                Color baseColor = slot.OriginalBase * tintMul;
                // In HDRP the emissive carries the flash; elsewhere (or if the material has no HDRP emissive) the base
                // colour is pushed toward the flash colour so the flash is visible even without an emission keyword.
                if (!useHdrpEmissive) baseColor = Color.Lerp(baseColor, _flashColor, w * baseFlashBlend);
                baseColor.a = slot.OriginalBase.a; // never change transparency / alpha-clip coverage
                block.SetColor(slot.BaseColorId, baseColor);
            }

            if (useHdrpEmissive)
            {
                // With _EmissiveExposureWeight = 0 the emission is exposure-relative (1 = display white before
                // tonemapping), so the flash is equally visible in a dark arena and in daylight. A physically authored
                // original glow (weight > 0, in nits) would be wildly over-bright in that space, so it is left out for
                // the few frames the effect shows; an exposure-relative or black original is kept.
                bool keepOriginal = !slot.HasExposureWeight || slot.OriginalExposureWeight <= 0.001f || IsBlack(slot.OriginalEmissive);
                Color emissive = (keepOriginal ? slot.OriginalEmissive : Color.black) + tintGlow + flashGlow;
                emissive.a = 1f;
                block.SetColor(s_emissiveColor, emissive);
                if (slot.HasExposureWeight) block.SetFloat(s_emissiveExposureWeight, 0f);
            }
            else if (slot.HasEmission)
            {
                Color emission = slot.OriginalEmissive + tintGlow + flashGlow;
                emission.a = 1f;
                block.SetColor(s_emissionColor, emission);
            }
        }

        /// <summary>
        /// Releases every renderer we wrote to: clears blocks we introduced, or writes the cached originals back into
        /// blocks shared with other systems (their values survive).
        /// </summary>
        private void RestoreAll()
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            for (int e = _entries.Count - 1; e >= 0; e--)
            {
                var entry = _entries[e];
                if (entry.Renderer == null)
                {
                    _entries.RemoveAt(e);
                    continue;
                }
                if (!entry.Dirty) continue;

                if (!entry.SharedBlock)
                {
                    ClearBlocks(entry);
                }
                else if (entry.PerMaterial)
                {
                    for (int i = 0; i < entry.Slots.Length; i++)
                    {
                        entry.Renderer.GetPropertyBlock(_block, i);
                        WriteOriginal(_block, in entry.Slots[i]);
                        entry.Renderer.SetPropertyBlock(_block, i);
                    }
                }
                else if (entry.Slots.Length > 0)
                {
                    entry.Renderer.GetPropertyBlock(_block);
                    WriteOriginal(_block, in entry.Slots[0]);
                    entry.Renderer.SetPropertyBlock(_block);
                }
                entry.Dirty = false;
            }
            _anyDirty = false;
        }

        /// <summary>
        /// Removes the blocks we introduced (only used when no other system had a block on the renderer). Passing null
        /// detaches the block entirely, so the renderer is back to its pristine, SRP-Batcher-compatible state.
        /// </summary>
        private static void ClearBlocks(RendererEntry entry, int materialCount = int.MaxValue)
        {
            if (entry.PerMaterial)
            {
                int count = Mathf.Min(entry.Slots.Length, materialCount); // never address a sub-material that no longer exists
                for (int i = 0; i < count; i++) entry.Renderer.SetPropertyBlock(null, i);
            }
            else
            {
                entry.Renderer.SetPropertyBlock(null);
            }
            entry.Dirty = false;
        }

        private void WriteOriginal(MaterialPropertyBlock block, in SlotData slot)
        {
            if (slot.BaseColorId != 0) block.SetColor(slot.BaseColorId, slot.OriginalBase);
            if (_pipeline == PipelineKind.HighDefinition && slot.HasHdrpEmissive)
            {
                block.SetColor(s_emissiveColor, slot.OriginalEmissive);
                if (slot.HasExposureWeight) block.SetFloat(s_emissiveExposureWeight, slot.OriginalExposureWeight);
            }
            else if (slot.HasEmission)
            {
                block.SetColor(s_emissionColor, slot.OriginalEmissive);
            }
        }

        // ------------------------------------------------------------------ slot caching

        private void EnsureInitialized()
        {
            if (_initialized) return;
            if (autoGatherRenderers)
            {
                // Fallback when nobody called Initialize: only real geometry, never particles/trails/lines.
                var meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
                var skinned = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var all = new Renderer[meshRenderers.Length + skinned.Length];
                for (int i = 0; i < skinned.Length; i++) all[i] = skinned[i];
                for (int i = 0; i < meshRenderers.Length; i++) all[skinned.Length + i] = meshRenderers[i];
                Initialize(all);
            }
            else
            {
                Initialize(null);
            }
        }

        private bool ContainsRenderer(Renderer r)
        {
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Renderer == r) return true;
            return false;
        }

        private void RefreshSlotsIfMaterialsChanged(RendererEntry entry)
        {
            entry.Renderer.GetSharedMaterials(_materialScratch);
            bool changed = _materialScratch.Count != entry.Slots.Length;
            for (int i = 0; !changed && i < _materialScratch.Count; i++)
                changed = _materialScratch[i] != entry.Slots[i].Material;
            if (!changed) return;

            // Materials were swapped (cloak / ghost / LOD rebuild): drop what we wrote for the old materials (their
            // cached originals must never land on the new ones), then re-cache from the new materials.
            // (With a shared block the entry stays dirty: the next write / restore uses the new originals.)
            if (entry.Dirty && !entry.SharedBlock) ClearBlocks(entry, _materialScratch.Count);
            BuildSlots(entry);
        }

        private void BuildSlots(RendererEntry entry)
        {
            entry.Renderer.GetSharedMaterials(_materialScratch);
            var slots = new SlotData[_materialScratch.Count];
            for (int i = 0; i < slots.Length; i++) slots[i] = CaptureSlot(_materialScratch[i]);
            entry.Slots = slots;
            entry.PerMaterial = !AllSlotsEquivalent(slots);
        }

        private SlotData CaptureSlot(Material m)
        {
            var slot = new SlotData { Material = m, OriginalBase = Color.white, OriginalEmissive = Color.black, OriginalExposureWeight = 1f };
            if (m == null) return slot;

            if (m.HasProperty(s_baseColor)) slot.BaseColorId = s_baseColor;
            else if (m.HasProperty(s_unlitColor)) slot.BaseColorId = s_unlitColor;
            else if (m.HasProperty(s_color)) slot.BaseColorId = s_color;
            if (slot.BaseColorId != 0) slot.OriginalBase = m.GetColor(slot.BaseColorId);

            slot.HasHdrpEmissive = m.HasProperty(s_emissiveColor);
            slot.HasExposureWeight = m.HasProperty(s_emissiveExposureWeight);
            slot.HasEmission = m.HasProperty(s_emissionColor);

            if (_pipeline == PipelineKind.HighDefinition && slot.HasHdrpEmissive)
            {
                slot.OriginalEmissive = m.GetColor(s_emissiveColor);
                if (slot.HasExposureWeight) slot.OriginalExposureWeight = m.GetFloat(s_emissiveExposureWeight);
            }
            else if (slot.HasEmission)
            {
                // Without the _EMISSION keyword the emission colour is ignored by the shader: treat it as black so the
                // restored value matches what was rendered.
                slot.OriginalEmissive = m.IsKeywordEnabled("_EMISSION") ? m.GetColor(s_emissionColor) : Color.black;
            }
            return slot;
        }

        private static bool AllSlotsEquivalent(SlotData[] slots)
        {
            if (slots.Length <= 1) return true;
            var a = slots[0];
            for (int i = 1; i < slots.Length; i++)
            {
                var b = slots[i];
                if (a.BaseColorId != b.BaseColorId || a.HasHdrpEmissive != b.HasHdrpEmissive ||
                    a.HasExposureWeight != b.HasExposureWeight || a.HasEmission != b.HasEmission) return false;
                if (a.OriginalBase != b.OriginalBase || a.OriginalEmissive != b.OriginalEmissive) return false;
                if (!Mathf.Approximately(a.OriginalExposureWeight, b.OriginalExposureWeight)) return false;
            }
            return true;
        }

        private static PipelineKind DetectPipeline() => RenderPipelineUtil.Current;

        private static bool IsBlack(Color c) => c.r <= 1e-4f && c.g <= 1e-4f && c.b <= 1e-4f;
    }
}
