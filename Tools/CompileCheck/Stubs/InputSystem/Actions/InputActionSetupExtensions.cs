// API-exact compile stub of com.unity.inputsystem (Runtime/Actions/InputActionSetupExtensions.cs, Input System 1.x).
// Only the members used by Dodgeball Ultra are declared; bodies throw. Used by Tools/CompileCheck only.
using System;

namespace UnityEngine.InputSystem
{
    public static class InputActionSetupExtensions
    {
        public static InputAction AddAction(this InputActionMap map, string name, InputActionType type = default, string binding = null,
            string interactions = null, string processors = null, string groups = null, string expectedControlLayout = null)
            => throw new NotImplementedException();

        public static BindingSyntax AddBinding(this InputAction action, string path, string interactions = null,
            string processors = null, string groups = null)
            => throw new NotImplementedException();

        public static CompositeSyntax AddCompositeBinding(this InputAction action, string composite,
            string interactions = null, string processors = null)
            => throw new NotImplementedException();

        public struct BindingSyntax
        {
            public BindingSyntax WithProcessor(string processor) => throw new NotImplementedException();
        }

        public struct CompositeSyntax
        {
            public CompositeSyntax With(string name, string binding, string groups = null, string processors = null)
                => throw new NotImplementedException();
        }
    }
}
