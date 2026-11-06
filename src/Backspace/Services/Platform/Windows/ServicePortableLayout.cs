using System;
using System.IO;

namespace Backspace.Services.Platform.Windows;

/// <summary>The portable Backspace.config folder beside the executable.</summary>
internal static class ServicePortableLayout
{
    internal static string Config(string installation) => Path.Combine(installation, AppPaths.AppFolderName);

    /// <summary>The configuration is this copy's own portable folder (not a
    /// test or review configuration elsewhere).</summary>
    internal static bool IsPortableConfig(string installation, string config)
        => string.Equals(Path.GetFullPath(config).TrimEnd('\\'), Config(installation), StringComparison.OrdinalIgnoreCase);
}
