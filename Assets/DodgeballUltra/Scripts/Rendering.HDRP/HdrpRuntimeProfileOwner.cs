using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Rendering.HDRP
{
    /// <summary>
    /// Owns a VolumeProfile created at runtime (not an asset) and destroys it - with its component instances - when the
    /// owning GameObject is destroyed (scene unload, arena rebuild), so repeated matches do not leak ScriptableObjects.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class HdrpRuntimeProfileOwner : MonoBehaviour
    {
        [SerializeField, Tooltip("Runtime profile destroyed together with this object.")]
        private VolumeProfile ownedProfile;

        /// <summary>The owned profile.</summary>
        public VolumeProfile Profile => ownedProfile;

        /// <summary>Takes ownership of <paramref name="profile"/> (a previously owned profile is destroyed).</summary>
        public void Own(VolumeProfile profile)
        {
            if (ownedProfile != null && ownedProfile != profile)
                HdrpVolumeUtility.DestroyProfile(ownedProfile);
            ownedProfile = profile;
        }

        private void OnDestroy()
        {
            HdrpVolumeUtility.DestroyProfile(ownedProfile);
            ownedProfile = null;
        }
    }
}
