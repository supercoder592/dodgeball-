using System;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - material-parameter flash via MaterialPropertyBlock (no material instancing). Pipeline-aware:
    /// HDRP Lit uses _EmissiveColor with exposure weight; URP/Built-in lerp _BaseColor/_Color toward the flash colour.
    /// Also supports a sustained tint (frozen blue, cloak) layered under flashes.
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HitFlash : MonoBehaviour
    {
        // IMPLEMENT: Juice module
        public void Initialize(Renderer[] renderers) => throw new NotImplementedException();

        /// <summary>Flash for <paramref name="duration"/> unscaled seconds (spec: 0.05 s white).</summary>
        public void Flash(Color color, float duration = Core.GameConstants.HitFlashDuration) => throw new NotImplementedException();

        /// <summary>Sustained tint (amount 0 = off).</summary>
        public void SetTint(Color color, float amount) => throw new NotImplementedException();
    }
}
