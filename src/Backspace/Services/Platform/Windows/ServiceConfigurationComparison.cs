using System;
using System.IO;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>The owner's saved settings, as the background service reads them.</summary>
internal static class ServiceConfigurationComparison
{
    internal static AppConfig ReadDesktop(string installation)
    {
        var path = Path.Combine(ServicePortableLayout.Config(installation), "config.json");
        // Shared for writing and deletion, so the app can still save while the
        // service reads; another program holding it is waited out (U135).
        using var source = FileAccessRetry.Get(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), path);
        if (source.Length is <= 0 or > ServiceRunAuthorization.MaximumApprovalBytes)
            throw new InvalidDataException("The saved backup settings could not be checked.");
        SkipByteOrderMark(source);
        return JsonSerializer.Deserialize<AppConfig>(source, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("The saved backup settings are missing.");
    }

    /// <summary>Settings saved by an editor that writes a UTF-8 byte order mark
    /// load in Backspace; the service refused them ("'0xEF' is an invalid start
    /// of a value", lab VM, 1 November 2026).</summary>
    internal static void SkipByteOrderMark(Stream source)
    {
        Span<byte> head = stackalloc byte[3];
        var read = source.Read(head);
        if (read != 3 || head[0] != 0xEF || head[1] != 0xBB || head[2] != 0xBF) source.Position = 0;
    }
}
