using Backspace.Services;

namespace Backspace.Tests.TestHelpers;

/// <summary>Start-at-sign-in entry for the test suite. It never touches the
/// Run key, a LaunchAgent or an autostart file of the machine running it.</summary>
internal sealed class RecordingSignInStart : ISignInStart
{
    internal static RecordingSignInStart Shared { get; } = new();
    internal string? Executable { get; set; }
    internal int Writes { get; private set; }

    public string? RegisteredExecutable() => Executable;
    public void Register(string executable) { Executable = executable; Writes++; }
    public void Unregister() { Executable = null; Writes++; }
}
