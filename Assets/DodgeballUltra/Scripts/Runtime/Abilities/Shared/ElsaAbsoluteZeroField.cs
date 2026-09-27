using System;
using System.Collections.Generic;
using DodgeballUltra.Juice;
using DodgeballUltra.VFX;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Look of an <see cref="ElsaAbsoluteZeroField"/>.</summary>
    public struct ElsaAbsoluteZeroFieldStyle
    {
        /// <summary>Colour (and peak alpha) of the frost sheen covering the floor.</summary>
        public Color SheenColor;
        /// <summary>Tint of the cold-mist VFX.</summary>
        public Color MistTint;
        /// <summary>Scale of each cold-mist emitter.</summary>
        public float MistScale;
        /// <summary>Seconds the frost takes to creep from the centre line to the baseline.</summary>
        public float SpreadTime;
        /// <summary>Seconds the frost takes to thaw away at the end.</summary>
        public float FadeTime;
        /// <summary>Height (m) of the sheen above the floor (avoids z-fighting with court markings).</summary>
        public float SurfaceOffset;

        public static ElsaAbsoluteZeroFieldStyle Default => new ElsaAbsoluteZeroFieldStyle
        {
            SheenColor = new Color(0.86f, 0.93f, 1f, 0.62f),
            MistTint = new Color(0.8f, 0.9f, 1f, 1f),
            MistScale = 1.6f,
            SpreadTime = 0.45f,
            FadeTime = 0.7f,
            SurfaceOffset = 0.006f,
        };
    }

    /// <summary>
    /// The visual of Elsa's [Absolute Zero]: the whole enemy half of the court glazed with frost.
    /// <list type="bullet">
    /// <item>A frost sheen (alpha-blended, crystalline frost texture tiled in world space every
    /// <see cref="ElsaFrostAssets.FrostTileWorldSize"/> m) laid 6 mm above the floor, which physically CREEPS from the
    /// centre line to the baseline over <see cref="ElsaAbsoluteZeroFieldStyle.SpreadTime"/> (vertices move, UVs stay
    /// world-locked so the crystals do not stretch), holds, then thaws (alpha fade).</item>
    /// <item>A grid of looping FrozenMist emitters: low cold fog rolling over the ice.</item>
    /// <item>Optional sustained Freeze screen tint for the local player while they stand on it
    /// (<see cref="SetLocalFrost"/>), always cleared when the field goes away - even if its ability owner vanished.</item>
    /// </list>
    /// The field is self-terminating: it destroys itself after its duration plus the thaw, so it can never leak.
    /// Gameplay effects (Slippery / DodgeDisabled) are applied by <see cref="ElsaAbsoluteZero"/>, not by this visual.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElsaAbsoluteZeroField : MonoBehaviour
    {
        private enum Stage
        {
            Spreading,
            Holding,
            Thawing,
        }

        private const int MistColumns = 3;
        private const int MistRows = 2;

        private readonly List<Vector3> _vertices = new List<Vector3>(4);
        private readonly List<Vector2> _uvs = new List<Vector2>(4);
        private readonly VfxHandle[] _mist = new VfxHandle[MistColumns * MistRows];

        private ElsaAbsoluteZeroFieldStyle _style;
        private Mesh _mesh;
        private Material _material;
        private float _width;
        private float _depth;
        private float _duration;
        private float _age;
        private float _thawAge;
        private float _localFrost;
        private float _thawStartFrost;
        private bool _removed;
        private Stage _stage;

        /// <summary>Team whose half is frozen.</summary>
        public TeamId FrozenTeam { get; private set; }

        /// <summary>True until the thaw has started.</summary>
        public bool IsHolding => _stage != Stage.Thawing;

        /// <summary>
        /// Optional callback invoked every frame (scaled delta time) while the field holds. Lets the owning ability keep
        /// its gameplay effect running after the ability itself was interrupted (e.g. Elsa stunned mid-ultimate) without
        /// the visual owning any gameplay logic. Cleared automatically when the thaw starts.
        /// </summary>
        public Action<float> ExternalTick { get; set; }

        /// <summary>
        /// Creates a field covering <paramref name="halfBounds"/> (the frozen team's infield) that spreads from the side
        /// at <paramref name="centreLineZ"/> toward the opposite baseline and lasts <paramref name="duration"/> seconds.
        /// </summary>
        public static ElsaAbsoluteZeroField Create(Bounds halfBounds, float floorY, float centreLineZ, TeamId frozenTeam,
            float duration, in ElsaAbsoluteZeroFieldStyle style)
        {
            var go = new GameObject("ElsaAbsoluteZeroField");
            go.layer = GameLayers.Visual;
            var field = go.AddComponent<ElsaAbsoluteZeroField>();
            field.Build(halfBounds, floorY, centreLineZ, frozenTeam, duration, style);
            return field;
        }

        /// <summary>
        /// Starts the thaw now (<paramref name="immediate"/> = remove at once, e.g. round reset).
        /// </summary>
        public void Stop(bool immediate)
        {
            if (immediate)
            {
                _removed = true;
                ExternalTick = null;
                StopMist(true);
                Destroy(gameObject);
                return;
            }
            if (_stage == Stage.Thawing) return;
            ExternalTick = null;
            _stage = Stage.Thawing;
            _thawAge = 0f;
            _thawStartFrost = _localFrost; // the screen tint fades out with the ice
            StopMist(false);
        }

        /// <summary>
        /// Sustained Freeze screen tint for the local player (0 = off). The field owns the value so it is always reset
        /// when the field disappears.
        /// </summary>
        public void SetLocalFrost(float amount)
        {
            amount = Mathf.Clamp01(amount);
            if (Mathf.Approximately(amount, _localFrost)) return;
            _localFrost = amount;
            ScreenFx.SetSustained(ScreenPulse.Freeze, amount);
        }

        // ------------------------------------------------------------------ construction

        private void Build(Bounds halfBounds, float floorY, float centreLineZ, TeamId frozenTeam, float duration,
            in ElsaAbsoluteZeroFieldStyle style)
        {
            FrozenTeam = frozenTeam;
            _style = style;
            _style.SpreadTime = Mathf.Max(0.01f, _style.SpreadTime);
            _style.FadeTime = Mathf.Max(0.01f, _style.FadeTime);
            _duration = Mathf.Max(0.1f, duration);
            _width = halfBounds.size.x;
            _depth = halfBounds.size.z;

            // Local +Z points from the centre line into the frozen half (toward its baseline).
            float dirSign = halfBounds.center.z >= centreLineZ ? 1f : -1f;
            float nearZ = dirSign > 0f ? halfBounds.min.z : halfBounds.max.z;
            transform.SetPositionAndRotation(new Vector3(halfBounds.center.x, floorY + _style.SurfaceOffset, nearZ),
                Quaternion.LookRotation(new Vector3(0f, 0f, dirSign), Vector3.up));

            // Frost sheen mesh (4 vertices, rewritten while spreading).
            _mesh = new Mesh { name = "DU_AbsoluteZeroSheen" };
            _mesh.MarkDynamic();
            WriteSheen(0.02f);
            _mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            _mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            _mesh.RecalculateBounds();

            _material = ElsaFrostAssets.CreateSheenMaterial(_style.SheenColor);
            ElsaFrostAssets.SetMaterialColor(_material, _style.SheenColor);

            var sheen = new GameObject("FrostSheen");
            sheen.layer = GameLayers.Visual;
            sheen.transform.SetParent(transform, false);
            sheen.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = sheen.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            // Low cold fog rolling over the ice (grid over the half). Spawned in world space rather than parented to the
            // field so destroying the field can never destroy pooled VFX instances; the handles are stopped explicitly.
            for (int r = 0; r < MistRows; r++)
            {
                for (int c = 0; c < MistColumns; c++)
                {
                    float x = ((c + 0.5f) / MistColumns - 0.5f) * _width;
                    float z = (r + 0.5f) / MistRows * _depth;
                    Vector3 world = transform.TransformPoint(new Vector3(x, 0.05f, z));
                    _mist[r * MistColumns + c] = VfxManager.Spawn(VfxId.FrozenMist, world, transform.rotation,
                        _style.MistScale, _style.MistTint);
                }
            }

            _stage = Stage.Spreading;
            _age = 0f;
        }

        // ------------------------------------------------------------------ update

        private void Update()
        {
            if (_removed) return;

            float dt = Time.deltaTime; // gameplay object: scaled time
            _age += dt;

            if (_stage != Stage.Thawing && ExternalTick != null)
            {
                ExternalTick(dt);
                if (_removed) return; // the callback removed the field
            }

            switch (_stage)
            {
                case Stage.Spreading:
                {
                    float t = Mathf.Clamp01(_age / _style.SpreadTime);
                    // Fast initial flash-freeze that decelerates toward the baseline.
                    WriteSheen(1f - (1f - t) * (1f - t) * (1f - t));
                    if (t >= 1f) _stage = Stage.Holding;
                    if (_age >= _duration) Stop(false);
                    break;
                }
                case Stage.Holding:
                    if (_age >= _duration) Stop(false);
                    break;
                case Stage.Thawing:
                {
                    _thawAge += dt;
                    float k = 1f - Mathf.Clamp01(_thawAge / _style.FadeTime);
                    var c = _style.SheenColor;
                    c.a *= k * k;
                    ElsaFrostAssets.SetMaterialColor(_material, c);
                    if (_thawStartFrost > 0f) SetLocalFrost(_thawStartFrost * k);
                    if (k <= 0f)
                    {
                        _removed = true;
                        Destroy(gameObject);
                    }
                    break;
                }
            }
        }

        private void OnDestroy()
        {
            ExternalTick = null;
            StopMist(true);
            if (_localFrost > 0f)
            {
                _localFrost = 0f;
                ScreenFx.SetSustained(ScreenPulse.Freeze, 0f);
            }
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }

        /// <summary>
        /// Rewrites the sheen quad to cover <paramref name="progress"/> (0..1) of the half's depth. UVs are world-scaled so
        /// the frost crystals stay put while the edge advances.
        /// </summary>
        private void WriteSheen(float progress)
        {
            float d = Mathf.Max(0.01f, _depth * Mathf.Clamp01(progress));
            float hw = _width * 0.5f;
            float tile = ElsaFrostAssets.FrostTileWorldSize;

            _vertices.Clear();
            _vertices.Add(new Vector3(-hw, 0f, 0f));
            _vertices.Add(new Vector3(hw, 0f, 0f));
            _vertices.Add(new Vector3(-hw, 0f, d));
            _vertices.Add(new Vector3(hw, 0f, d));

            _uvs.Clear();
            _uvs.Add(new Vector2(0f, 0f));
            _uvs.Add(new Vector2(_width / tile, 0f));
            _uvs.Add(new Vector2(0f, d / tile));
            _uvs.Add(new Vector2(_width / tile, d / tile));

            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uvs);
            _mesh.RecalculateBounds();
        }

        private void StopMist(bool immediate)
        {
            for (int i = 0; i < _mist.Length; i++)
            {
                VfxManager.StopEffect(_mist[i], immediate);
                _mist[i] = default;
            }
        }
    }
}
