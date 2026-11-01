using System;
using System.IO;

namespace Backspace.Services;

internal static class FileReplacementErrors
{
    // ReplaceFileW retains both original names for 1175. In contrast, 1176
    // and 1177 can mean the old file has already been renamed: do not replay.
    internal static bool CanRetry(Exception error) => OperatingSystem.IsWindows()
        && error is IOException or UnauthorizedAccessException
        && (error.HResult & 0xffff) is 5 or 32 or 33 or 1175;
}
