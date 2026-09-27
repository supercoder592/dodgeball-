using UnityEngine;

namespace DodgeballUltra
{
    /// <summary>
    /// Unity 6 renamed several Rigidbody members (velocity -> linearVelocity, drag -> linearDamping,
    /// angularDrag -> angularDamping). ALL gameplay code must go through these helpers so the project compiles
    /// warning-free on Unity 6 while the compile-check harness (Unity 2021.3 reference assemblies) still passes.
    /// </summary>
    public static class PhysicsCompat
    {
        public static Vector3 GetVelocity(this Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetVelocity(this Rigidbody rb, Vector3 velocity)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = velocity;
#else
            rb.velocity = velocity;
#endif
        }

        public static Vector3 GetAngularVelocity(this Rigidbody rb) => rb.angularVelocity;

        public static void SetAngularVelocity(this Rigidbody rb, Vector3 angularVelocity) => rb.angularVelocity = angularVelocity;

        public static void SetLinearDamping(this Rigidbody rb, float damping)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearDamping = damping;
#else
            rb.drag = damping;
#endif
        }

        public static float GetLinearDamping(this Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearDamping;
#else
            return rb.drag;
#endif
        }

        public static void SetAngularDamping(this Rigidbody rb, float damping)
        {
#if UNITY_6000_0_OR_NEWER
            rb.angularDamping = damping;
#else
            rb.angularDrag = damping;
#endif
        }

        /// <summary>
        /// Unity 6 renamed PhysicMaterial -> PhysicsMaterial. Creates a physics material in a version-independent way.
        /// </summary>
        public static Object CreatePhysicsMaterial(string name, float bounciness, float dynamicFriction, float staticFriction,
            bool maximumBounce)
        {
#if UNITY_6000_0_OR_NEWER
            var m = new PhysicsMaterial(name)
            {
                bounciness = bounciness,
                dynamicFriction = dynamicFriction,
                staticFriction = staticFriction,
                bounceCombine = maximumBounce ? PhysicsMaterialCombine.Maximum : PhysicsMaterialCombine.Average,
                frictionCombine = PhysicsMaterialCombine.Average,
            };
            return m;
#else
            var m = new PhysicMaterial(name)
            {
                bounciness = bounciness,
                dynamicFriction = dynamicFriction,
                staticFriction = staticFriction,
                bounceCombine = maximumBounce ? PhysicMaterialCombine.Maximum : PhysicMaterialCombine.Average,
                frictionCombine = PhysicMaterialCombine.Average,
            };
            return m;
#endif
        }

        /// <summary>Assigns a material created by <see cref="CreatePhysicsMaterial"/> to a collider.</summary>
        public static void SetPhysicsMaterial(this Collider collider, Object material)
        {
#if UNITY_6000_0_OR_NEWER
            collider.sharedMaterial = material as PhysicsMaterial;
#else
            collider.sharedMaterial = material as PhysicMaterial;
#endif
        }
    }
}
