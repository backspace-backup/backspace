using System;
using System.IO;
using System.Security;

namespace Backspace.Services.Platform.Windows;

/// <summary>Both desktop data and private unattended data travel with the one
/// executable. The shared container permits creating ordinary desktop files,
/// but only administrators can replace its protected service child.</summary>
internal static class ServicePortableLayout
{
    internal static string Config(string installation) => Path.Combine(installation, AppPaths.AppFolderName);
    internal static string Service(string installation) => Path.Combine(Config(installation), "windows-service");
    internal static string Control(string installation) => Path.Combine(Service(installation), "control");
    internal static string Data(string installation) => Path.Combine(Service(installation), "data");
    internal static string Record(string installation, string name) => Path.Combine(Control(installation), name);

    // Version 1 was used by the isolated development VM. Keep its protected
    // record readable while replacing those fixtures; never mix records from
    // the two layouts or fall back after an invalid version-2 record.
    internal static string ExistingRecord(string installation, string name)
        => File.Exists(Record(installation, ServiceWorkerBinding.FileName))
            ? Record(installation, name) : Path.Combine(installation, name);

    internal static void ValidateDataLocation(ServiceWorkerBinding binding, string installation)
    {
        if (binding.Version == 2 && !string.Equals(binding.DataDirectory, Data(installation), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The backup service data must remain inside this copy's Backspace.config folder.");
    }
}
