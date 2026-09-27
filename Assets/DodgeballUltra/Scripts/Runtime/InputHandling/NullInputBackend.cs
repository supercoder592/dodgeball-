namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// Backend that never reports input. Used when neither the Input System package nor the legacy Input Manager is
    /// compiled in (dedicated servers, some test runners) and as a safe fallback if a real backend fails to start.
    /// </summary>
    public sealed class NullInputBackend : IInputBackend
    {
        public string Name => "None";
        public bool IsEnabled { get; private set; } = true;

        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
        public void Dispose() => IsEnabled = false;

        public void Read(ref InputFrame frame) => frame = default;
    }
}
