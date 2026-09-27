using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Match;
using DodgeballUltra.Rendering;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// The harmless ball a Shadow clone "throws" when the real player throws (Night Parade / Mirage Formation).
    /// <para>
    /// Purely visual (layer <see cref="GameLayers.Visual"/>, no collider, never registered with the BallManager): it
    /// borrows the exact mesh and materials of a real match ball so opponents cannot tell it apart at a glance, flies
    /// ballistically with the real throw's velocity and gravity scale (scaled time, so hitstop freezes it too), and
    /// vanishes in a puff of smoke after its short lifetime (spec: 0.6 s) or when it reaches the floor.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ShadowIllusionBall : MonoBehaviour
    {
        private static Mesh s_fallbackMesh;
        private static Material s_fallbackMaterial;

        private Vector3 _velocity;
        private float _gravity;
        private float _remaining;
        private float _radius = GameConstants.BallRadius;

        /// <summary>Tint of the smoke puff when the illusion vanishes (same smoke as a popping clone).</summary>
        private static readonly Color s_vanishTint = new Color(0.3f, 0.28f, 0.38f, 1f);

        /// <summary>Spawns an illusion ball at <paramref name="origin"/> flying with <paramref name="velocity"/>.</summary>
        public static ShadowIllusionBall Spawn(Vector3 origin, Vector3 velocity, float gravityScale, float lifetime)
        {
            var go = new GameObject("ShadowIllusionBall") { layer = GameLayers.Visual };
            go.transform.SetPositionAndRotation(origin, velocity.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(velocity) : Quaternion.identity);

            var ball = go.AddComponent<ShadowIllusionBall>();
            ball._velocity = velocity;
            ball._gravity = GameConstants.Gravity * Mathf.Max(0f, gravityScale);
            ball._remaining = Mathf.Max(0.05f, lifetime);
            ball.BuildVisual();
            return ball;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            _remaining -= dt;
            _velocity += Vector3.down * (_gravity * dt);
            Vector3 position = transform.position + _velocity * dt;
            transform.position = position;
            if (_velocity.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(_velocity);

            float floorY = Court.Instance != null ? Court.Instance.FloorY : 0f;
            if (_remaining <= 0f || position.y <= floorY + _radius) Vanish();
        }

        private void Vanish()
        {
            VfxManager.Spawn(VfxId.CloneDissolve, transform.position, Quaternion.identity, 0.35f, s_vanishTint);
            Destroy(gameObject);
        }

        /// <summary>Copies the look of a real match ball (mesh + materials, world scale); falls back to a foam-red sphere.</summary>
        private void BuildVisual()
        {
            var visual = new GameObject("Visual") { layer = GameLayers.Visual };
            visual.transform.SetParent(transform, false);
            var filter = visual.AddComponent<MeshFilter>();
            var renderer = visual.AddComponent<MeshRenderer>();

            if (TryCopyMatchBallLook(filter, renderer, visual.transform)) return;

            filter.sharedMesh = GetFallbackMesh();
            if (s_fallbackMaterial == null)
            {
                // Matte red foam dodgeball (physically plausible roughness, no metal).
                s_fallbackMaterial = MaterialFactory.CreateLit("DU_IllusionBall", new Color(0.6f, 0.08f, 0.06f, 1f), 0.3f);
            }
            renderer.sharedMaterial = s_fallbackMaterial;
            visual.transform.localScale = Vector3.one * (GameConstants.BallRadius * 2f);
        }

        private bool TryCopyMatchBallLook(MeshFilter filter, MeshRenderer renderer, Transform visual)
        {
            var manager = BallManager.Instance;
            var balls = manager != null ? manager.MatchBalls : null;
            if (balls == null) return false;

            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || ball.VisualRoot == null) continue;
                var srcFilter = ball.VisualRoot.GetComponentInChildren<MeshFilter>();
                var srcRenderer = srcFilter != null ? srcFilter.GetComponent<MeshRenderer>() : null;
                if (srcFilter == null || srcFilter.sharedMesh == null || srcRenderer == null) continue;

                filter.sharedMesh = srcFilter.sharedMesh;
                renderer.sharedMaterials = srcRenderer.sharedMaterials;
                renderer.shadowCastingMode = srcRenderer.shadowCastingMode;

                // World scale of the source mesh, ignoring any squash & stretch in progress on the real ball.
                float uniform = Mathf.Max(srcFilter.transform.lossyScale.x, Mathf.Max(srcFilter.transform.lossyScale.y, srcFilter.transform.lossyScale.z));
                visual.localScale = Vector3.one * uniform;
                _radius = Mathf.Max(0.05f, ball.BaseRadius);
                return true;
            }
            return false;
        }

        private static Mesh GetFallbackMesh()
        {
            if (s_fallbackMesh != null) return s_fallbackMesh;
            // Borrow Unity's built-in sphere mesh once; the temporary primitive (and its collider) is discarded.
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = temp.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            s_fallbackMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
            return s_fallbackMesh;
        }
    }
}
