using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// Sits on the root of an arena built by <see cref="RuntimeArenaBuilder"/>. Remembers the settings it was built with and
    /// owns the procedurally generated meshes, destroying them with the arena so scene reloads / rematches do not leak GPU
    /// memory. Shared materials and textures are cached globally and are not destroyed here.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ArenaRuntimeResources : MonoBehaviour
    {
        [SerializeField, Tooltip("Settings the arena was built with (read-only information).")]
        private ArenaBuildSettings settings;

        // Not serialized on purpose: after a scene saved by the editor builder is loaded, the meshes are assets and must
        // never be destroyed by this component.
        [NonSerialized] private readonly List<UnityEngine.Object> m_owned = new List<UnityEngine.Object>(32);

        /// <summary>Settings the arena was built with.</summary>
        public ArenaBuildSettings Settings => settings;

        internal void Initialize(ArenaBuildSettings buildSettings) => settings = buildSettings;

        /// <summary>Registers a runtime-created object to destroy together with the arena.</summary>
        internal void Register(UnityEngine.Object obj)
        {
            if (obj != null) m_owned.Add(obj);
        }

        private void OnDestroy()
        {
            // Edit mode: the editor scene builder may have saved these meshes as assets - never destroy them there.
            // Play mode: the list is non-serialized, so it only ever holds meshes built during this play session.
            if (Application.isPlaying)
            {
                for (int i = 0; i < m_owned.Count; i++)
                {
                    UnityEngine.Object obj = m_owned[i];
                    if (obj != null) Destroy(obj);
                }
            }
            m_owned.Clear();
        }
    }
}
