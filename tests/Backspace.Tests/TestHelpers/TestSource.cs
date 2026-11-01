using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Backspace.Tests.TestHelpers;

/// <summary>Find source files even when --artifacts-path puts binaries outside the checkout.</summary>
internal static class TestSource
{
    public static string FilePath(string relativePath, [CallerFilePath] string caller = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(caller), Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "Backspace.sln"))) continue;
                var path = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("Cannot locate the Backspace checkout source file.", relativePath);
    }
}
