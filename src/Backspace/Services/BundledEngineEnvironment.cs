using System.Diagnostics;
using System.IO;

namespace Backspace.Services;

internal static class BundledEngineEnvironment
{
    // Only the child process and its descendants see this PATH. Windows and
    // the user's own shell remain unchanged. A directory containing spaces,
    // commas or apostrophes needs no command-line quoting here.
    internal static void IncludeRclone(ProcessStartInfo start, string? rcloneBinary = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rcloneBinary ?? AppPaths.RcloneBinary))!;
        start.Environment["PATH"] = directory + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var inherited) ? inherited : "");
    }
}
