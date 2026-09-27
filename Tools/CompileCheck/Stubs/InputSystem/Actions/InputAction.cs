// API-exact compile stub of com.unity.inputsystem (Runtime/Actions/InputAction.cs, Input System 1.x).
// Only the members used by Dodgeball Ultra are declared; bodies throw. Used by Tools/CompileCheck only.
using System;

namespace UnityEngine.InputSystem
{
    public sealed class InputAction : ICloneable, IDisposable
    {
        public string name => throw new NotImplementedException();

        public bool enabled => throw new NotImplementedException();

        public InputControl activeControl => throw new NotImplementedException();

        public InputAction()
        {
        }

        public InputAction(string name = null, InputActionType type = default, string binding = null,
                           string interactions = null, string processors = null, string expectedControlType = null)
        {
        }

        public void Dispose() => throw new NotImplementedException();

        public void Enable() => throw new NotImplementedException();

        public void Disable() => throw new NotImplementedException();

        public InputAction Clone() => throw new NotImplementedException();

        object ICloneable.Clone() => throw new NotImplementedException();

        public TValue ReadValue<TValue>()
            where TValue : struct
            => throw new NotImplementedException();

        public bool IsPressed() => throw new NotImplementedException();

        public bool WasPressedThisFrame() => throw new NotImplementedException();

        public bool WasReleasedThisFrame() => throw new NotImplementedException();
    }
}
