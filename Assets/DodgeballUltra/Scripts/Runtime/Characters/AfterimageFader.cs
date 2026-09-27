using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// Fades a baked afterimage (see <see cref="CharacterVisual.SpawnAfterimage"/>) out over its lifetime by lowering the
    /// alpha of its ghost material through a <see cref="MaterialPropertyBlock"/> (the shared ghost material is never
    /// instanced), then destroys the object and the meshes it baked.
    /// <para>Runs on scaled time: hitstop freezes the fade together with the rest of the gameplay.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AfterimageFader : MonoBehaviour
    {
        [Tooltip("Alpha falloff exponent over the normalised lifetime: alpha = a0 * (1 - t)^exponent. " +
                 ">1 fades quickly at first then lingers faintly (reads like a retinal afterimage).")]
        [Range(0.25f, 4f)] public float fadeExponent = 1.6f;

        [Tooltip("Metres the afterimage sinks per second while fading (0 = static). A few centimetres sells the dissolve.")]
        [Range(0f, 0.5f)] public float sinkSpeed = 0f;

        /// <summary>Seconds the afterimage lives.</summary>
        public float Lifetime { get; private set; }

        /// <summary>0..1 progress of the fade.</summary>
        public float Progress => Lifetime > 0f ? Mathf.Clamp01(_elapsed / Lifetime) : 1f;

        private Renderer[] _renderers = System.Array.Empty<Renderer>();
        private Mesh[] _ownedMeshes = System.Array.Empty<Mesh>();
        private MaterialPropertyBlock _block;
        private int _colorId = -1;
        private Color _baseColor = Color.white;
        private float _elapsed;
        private bool _initialized;

        /// <summary>
        /// Starts the fade.
        /// </summary>
        /// <param name="lifetime">Seconds until the object destroys itself.</param>
        /// <param name="renderers">Renderers whose alpha fades.</param>
        /// <param name="ownedMeshes">Baked meshes destroyed together with the afterimage (may contain nulls).</param>
        /// <param name="colorPropertyId">Colour property of the ghost material (-1: no alpha fade, the object just expires).</param>
        /// <param name="baseColor">Colour at full strength (alpha = starting opacity).</param>
        public void Initialize(float lifetime, Renderer[] renderers, Mesh[] ownedMeshes, int colorPropertyId, Color baseColor)
        {
            Lifetime = Mathf.Max(0.02f, lifetime);
            _renderers = renderers ?? System.Array.Empty<Renderer>();
            _ownedMeshes = ownedMeshes ?? System.Array.Empty<Mesh>();
            _colorId = colorPropertyId;
            _baseColor = baseColor;
            _elapsed = 0f;
            _block ??= new MaterialPropertyBlock();
            _initialized = true;
            ApplyAlpha(1f);
        }

        private void Update()
        {
            if (!_initialized) return;
            float dt = Time.deltaTime;
            _elapsed += dt;

            float t = Progress;
            ApplyAlpha(Mathf.Pow(1f - t, fadeExponent));
            if (sinkSpeed > 0f) transform.position += Vector3.down * (sinkSpeed * dt);

            if (t >= 1f) Destroy(gameObject);
        }

        private void ApplyAlpha(float strength)
        {
            if (_colorId == -1 || _block == null) return;
            Color c = _baseColor;
            c.a = _baseColor.a * Mathf.Clamp01(strength);
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_block);
                _block.SetColor(_colorId, c);
                r.SetPropertyBlock(_block);
            }
        }

        private void OnDestroy()
        {
            // Baked meshes are runtime assets: destroy them explicitly or they leak until the next asset unload.
            for (int i = 0; i < _ownedMeshes.Length; i++)
            {
                if (_ownedMeshes[i] != null) HumanoidUtil.DestroySafe(_ownedMeshes[i]);
            }
            _ownedMeshes = System.Array.Empty<Mesh>();
        }
    }
}
