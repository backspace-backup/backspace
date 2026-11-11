using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backspace.Services;

/// <summary>
/// Removes the working folders Backrest left on this computer (U175 step 4,
/// the owner's answer of 10 November 2026): its data folder
/// (<c>backrest</c>, with the mounted drive's old cache), the background
/// workers' copies (<c>backrest-1</c> … <c>backrest-7</c>) and their
/// <c>worker-slot-N.lock</c> files, and the per-run folders of command-line
/// runs and the shadow-copy helper (<c>engine-runs</c>). Backrest kept a
/// readable copy of every repository's password in them (its config.json);
/// Backspace needs nothing from them since it runs restic itself.
///
/// <para>Only these names, directly inside this configuration's engine folder
/// (<see cref="EngineWorkPaths.Root"/>), are removed, never another copy's or
/// anything else in it (exclude files, rclone remotes, scratch). A link with
/// one of these names, or an engine folder that is itself a link, is left
/// alone, and nothing is ever followed through a link. A folder still in use
/// stays until a later start.</para>
/// </summary>
internal static class BackrestLeftovers
{
    /// <summary>The folders Backrest's engines used: the window's, then one
    /// per background worker (at most <see cref="Models.BackupLimits.Maximum"/>
    /// at once), then the per-run ones.</summary>
    internal static IReadOnlyList<string> FolderNames { get; } =
        ["backrest", .. Enumerable.Range(1, Models.BackupLimits.Maximum - 1).Select(i => "backrest-" + i), "engine-runs"];

    /// <summary>The files the workers claimed their folder with.</summary>
    internal static IReadOnlyList<string> FileNames { get; } =
        Enumerable.Range(0, Models.BackupLimits.Maximum).Select(i => $"worker-slot-{i}.lock").ToArray();

    /// <summary>Removes what is there; returns the folders removed. Never
    /// throws: what cannot be removed now is logged and tried again on a later
    /// start.</summary>
    internal static IReadOnlyList<string> Remove(string? engineRoot = null)
    {
        var removed = new List<string>();
        string root;
        try
        {
            root = Path.GetFullPath(engineRoot ?? EngineWorkPaths.Root);
            if (!Directory.Exists(root) || IsLink(root)) return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogger.For(nameof(BackrestLeftovers)).Debug(ex, "Could not look for Backrest's old folders");
            return removed;
        }
        foreach (var name in FolderNames)
        {
            var folder = Path.Combine(root, name);
            try
            {
                if (!Directory.Exists(folder)) continue;
                if (IsLink(folder))
                {
                    AppLogger.For(nameof(BackrestLeftovers)).Information("Left {Folder} in place: it is a link, not Backrest's folder", folder);
                    continue;
                }
#if WINDOWS
                // Never through a link inside it, and only where its name says.
                Platform.Windows.SafeTreeDelete.Delete(folder);
#else
                Directory.Delete(folder, recursive: true);
#endif
                removed.Add(folder);
            }
            // SafeTreeDelete reports a failed Windows call as Win32Exception.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AppLogger.For(nameof(BackrestLeftovers)).Information(ex, "Backrest's old folder {Folder} stays until a later start", folder);
            }
        }
        foreach (var name in FileNames)
        {
            var file = Path.Combine(root, name);
            try { if (File.Exists(file) && !IsLink(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For(nameof(BackrestLeftovers)).Debug(ex, "Leaving {File} until a later start", file); }
        }
        if (removed.Count > 0)
            AppLogger.For(nameof(BackrestLeftovers)).Information(
                "Removed Backrest's old working folders, which held a readable copy of repository passwords: {Folders}", removed);
        return removed;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
