// -----------------------------------------------------------------------------------------------------------------
// Compile-check stubs: com.unity.render-pipelines.core 17.0.4 (Unity 6000.0) - Runtime/Volume/*.cs
// Signatures are copied verbatim from the package source; bodies are minimal (compile-only, never executed).
// Only the types/members used by DodgeballUltra.Rendering.HDRP / DodgeballUltra.Editor.HDRP are declared.
// -----------------------------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace UnityEngine.Rendering
{
    // Runtime/Volume/Volume.cs
    [ExecuteAlways]
    [AddComponentMenu("Miscellaneous/Volume")]
    public class Volume : MonoBehaviour
    {
        [SerializeField] bool m_IsGlobal = true;

        public bool isGlobal
        {
            get => m_IsGlobal;
            set => m_IsGlobal = value;
        }

        public float priority = 0f;
        public float blendDistance = 0f;
        [Range(0f, 1f)] public float weight = 1f;
        public VolumeProfile sharedProfile = null;

        VolumeProfile m_InternalProfile;

        public VolumeProfile profile
        {
            get => m_InternalProfile;
            set => m_InternalProfile = value;
        }

        internal List<Collider> m_Colliders = new List<Collider>();
        public List<Collider> colliders => m_Colliders;
        public bool HasInstantiatedProfile() => m_InternalProfile != null;
    }

    // Runtime/Volume/VolumeProfile.cs
    public sealed class VolumeProfile : ScriptableObject
    {
        public List<VolumeComponent> components = new List<VolumeComponent>();

        public void Reset() { }

        public T Add<T>(bool overrides = false)
            where T : VolumeComponent
        {
            return (T)Add(typeof(T), overrides);
        }

        public VolumeComponent Add(Type type, bool overrides = false) => throw new NotImplementedException();

        public void Remove<T>()
            where T : VolumeComponent
        {
            Remove(typeof(T));
        }

        public void Remove(Type type) => throw new NotImplementedException();

        public bool Has<T>()
            where T : VolumeComponent
        {
            return Has(typeof(T));
        }

        public bool Has(Type type) => throw new NotImplementedException();

        public bool TryGet<T>(out T component)
            where T : VolumeComponent
        {
            return TryGet(typeof(T), out component);
        }

        public bool TryGet<T>(Type type, out T component)
            where T : VolumeComponent
            => throw new NotImplementedException();
    }

    // Runtime/Volume/VolumeComponent.cs
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class VolumeComponentMenu : Attribute
    {
        public readonly string menu;

        public VolumeComponentMenu(string menu)
        {
            this.menu = menu;
        }
    }

    [Serializable]
    public partial class VolumeComponent : ScriptableObject
    {
        public bool active = true;
        public string displayName { get; protected set; } = "";
        public ReadOnlyCollection<VolumeParameter> parameters => throw new NotImplementedException();
        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        public virtual void Override(VolumeComponent state, float interpFactor) { }
        public void SetAllOverridesTo(bool state) { }
        public bool AnyPropertiesIsOverridden() => false;
        protected virtual void OnDestroy() { }
        public void Release() { }
    }

    // Runtime/Volume/VolumeParameter.cs
    public abstract class VolumeParameter : ICloneable
    {
        public const string k_DebuggerDisplay = "{m_Value} ({m_OverrideState})";

        [SerializeField]
        protected bool m_OverrideState;

        public virtual bool overrideState
        {
            get => m_OverrideState;
            set => m_OverrideState = value;
        }

        public T GetValue<T>()
        {
            return ((VolumeParameter<T>)this).value;
        }

        public abstract void SetValue(VolumeParameter parameter);
        public virtual void Release() { }
        public abstract object Clone();
    }

    [Serializable]
    public class VolumeParameter<T> : VolumeParameter, IEquatable<VolumeParameter<T>>
    {
        [SerializeField]
        protected T m_Value;

        public virtual T value
        {
            get => m_Value;
            set => m_Value = value;
        }

        public VolumeParameter()
            : this(default, false)
        {
        }

        protected VolumeParameter(T value, bool overrideState = false)
        {
            m_Value = value;
            this.overrideState = overrideState;
        }

        public virtual void Interp(T from, T to, float t)
        {
            m_Value = t > 0f ? to : from;
        }

        public void Override(T x)
        {
            overrideState = true;
            m_Value = x;
        }

        public override void SetValue(VolumeParameter parameter)
        {
            m_Value = ((VolumeParameter<T>)parameter).m_Value;
        }

        public bool Equals(VolumeParameter<T> other) => ReferenceEquals(this, other);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => 0;
        public override object Clone() => throw new NotImplementedException();
    }

    [Serializable]
    public sealed class EnumParameter<T> : VolumeParameter<T>
    {
        public EnumParameter(T value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable]
    public class BoolParameter : VolumeParameter<bool>
    {
        public BoolParameter(bool value, bool overrideState = false)
            : base(value, overrideState)
        {
        }

        public BoolParameter(bool value, DisplayType displayType, bool overrideState = false)
            : base(value, overrideState)
        {
            this.displayType = displayType;
        }

        public enum DisplayType
        {
            Checkbox,
            EnumPopup
        }

        [NonSerialized]
        public DisplayType displayType = DisplayType.Checkbox;
    }

    [Serializable]
    public class LayerMaskParameter : VolumeParameter<LayerMask>
    {
        public LayerMaskParameter(LayerMask value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable]
    public class IntParameter : VolumeParameter<int>
    {
        public IntParameter(int value, bool overrideState = false)
            : base(value, overrideState) { }

        public sealed override void Interp(int from, int to, float t)
        {
            m_Value = (int)(from + (to - from) * t);
        }
    }

    [Serializable]
    public class NoInterpIntParameter : VolumeParameter<int>
    {
        public NoInterpIntParameter(int value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable]
    public class ClampedIntParameter : IntParameter
    {
        [NonSerialized] public int min;
        [NonSerialized] public int max;

        public override int value
        {
            get => m_Value;
            set => m_Value = Mathf.Clamp(value, min, max);
        }

        public ClampedIntParameter(int value, int min, int max, bool overrideState = false)
            : base(value, overrideState)
        {
            this.min = min;
            this.max = max;
        }
    }

    [Serializable]
    public class NoInterpClampedIntParameter : VolumeParameter<int>
    {
        [NonSerialized] public int min;
        [NonSerialized] public int max;

        public override int value
        {
            get => m_Value;
            set => m_Value = Mathf.Clamp(value, min, max);
        }

        public NoInterpClampedIntParameter(int value, int min, int max, bool overrideState = false)
            : base(value, overrideState)
        {
            this.min = min;
            this.max = max;
        }
    }

    [Serializable]
    public class FloatParameter : VolumeParameter<float>
    {
        public FloatParameter(float value, bool overrideState = false)
            : base(value, overrideState) { }

        public sealed override void Interp(float from, float to, float t)
        {
            m_Value = from + (to - from) * t;
        }
    }

    [Serializable]
    public class MinFloatParameter : FloatParameter
    {
        [NonSerialized] public float min;

        public override float value
        {
            get => m_Value;
            set => m_Value = Mathf.Max(value, min);
        }

        public MinFloatParameter(float value, float min, bool overrideState = false)
            : base(value, overrideState)
        {
            this.min = min;
        }
    }

    [Serializable]
    public class NoInterpMinFloatParameter : VolumeParameter<float>
    {
        [NonSerialized] public float min;

        public override float value
        {
            get => m_Value;
            set => m_Value = Mathf.Max(value, min);
        }

        public NoInterpMinFloatParameter(float value, float min, bool overrideState = false)
            : base(value, overrideState)
        {
            this.min = min;
        }
    }

    [Serializable]
    public class ClampedFloatParameter : FloatParameter
    {
        [NonSerialized] public float min;
        [NonSerialized] public float max;

        public override float value
        {
            get => m_Value;
            set => m_Value = Mathf.Clamp(value, min, max);
        }

        public ClampedFloatParameter(float value, float min, float max, bool overrideState = false)
            : base(value, overrideState)
        {
            this.min = min;
            this.max = max;
        }
    }

    [Serializable]
    public class ColorParameter : VolumeParameter<Color>
    {
        [NonSerialized] public bool hdr = false;
        [NonSerialized] public bool showAlpha = true;
        [NonSerialized] public bool showEyeDropper = true;

        public ColorParameter(Color value, bool overrideState = false)
            : base(value, overrideState) { }

        public ColorParameter(Color value, bool hdr, bool showAlpha, bool showEyeDropper, bool overrideState = false)
            : base(value, overrideState)
        {
            this.hdr = hdr;
            this.showAlpha = showAlpha;
            this.showEyeDropper = showEyeDropper;
            this.overrideState = overrideState;
        }

        public override void Interp(Color from, Color to, float t)
        {
            m_Value = Color.LerpUnclamped(from, to, t);
        }
    }

    [Serializable]
    public class Vector2Parameter : VolumeParameter<Vector2>
    {
        public Vector2Parameter(Vector2 value, bool overrideState = false)
            : base(value, overrideState) { }

        public override void Interp(Vector2 from, Vector2 to, float t)
        {
            m_Value = Vector2.LerpUnclamped(from, to, t);
        }
    }

    [Serializable]
    public class CubemapParameter : VolumeParameter<Texture>
    {
        public CubemapParameter(Texture value, bool overrideState = false)
            : base(value, overrideState) { }
    }
}
