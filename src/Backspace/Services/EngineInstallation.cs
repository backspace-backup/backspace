using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Where restic, Backrest and rclone live (U100). Releases ship Backspace.exe
/// alone; the engines are fetched from the same GitHub release into
/// %LOCALAPPDATA%\Backspace\bin\&lt;set&gt;, checked against SHA-256 values built
/// into Backspace.exe, and fetched again whenever missing, so a copy moved to
/// another computer just works. One folder per engine set: two Backspace
/// versions on one PC never replace each other's engines.
///
/// Development and test builds, and releases before 1.0.3, keep the engines
/// beside Backspace.exe; those are used when present.
/// </summary>
internal static class EngineInstallation
{
    /// <param name="Release">True for a Release build: its engines run from
    /// <see cref="Root"/>. Development builds keep them beside the exe.</param>
    /// <param name="Engines">The pinned upstream versions (engine-versions.json):
    /// each engine is fetched from its own project, not from Backspace's release.</param>
    internal sealed record Manifest(string Version, Dictionary<string, string> Files, bool Release = false,
        Dictionary<string, string>? Engines = null);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Manifest?> Embedded = new(ReadEmbedded);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Verified = new(StringComparer.OrdinalIgnoreCase);

    internal static string Extension => PlatformInfo.IsWindows ? ".exe" : "";
    internal static string[] Names => new[] { "backrest", "restic", "rclone" }.Select(n => n + Extension).ToArray();

    /// <summary>The engines this build was made with, or null for a build
    /// without them (tests, -p:SkipBackrestDownload).</summary>
    internal static Manifest? Current => Embedded.Value;

    /// <summary>Folder holding every engine set. BACKSPACE_ENGINE_ROOT lets
    /// tests and the background service (which runs as Windows, not as the
    /// owner) point at the owner's folder.</summary>
    internal static string Root =>
        Environment.GetEnvironmentVariable("BACKSPACE_ENGINE_ROOT") is { Length: > 0 } root ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backspace", "bin");

    internal static string SetId(Manifest manifest)
    {
        var text = string.Join("\n", manifest.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value.ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>Folder the engines run from: this release's set under
    /// <see cref="Root"/>; beside Backspace.exe for development builds.</summary>
    internal static string Directory
    {
        get
        {
            if (Current is not { Release: true } manifest) return AppPaths.ExeDirectory;
            return Path.Combine(Root, SetId(manifest));
        }
    }

    internal static bool IsComplete(string directory, Manifest manifest) => manifest.Files.All(f => Matches(Path.Combine(directory, f.Key), f.Value));

    /// <summary>Makes sure this build's engines are present, fetching any
    /// that are missing or damaged. Cheap when they are already there.</summary>
    internal static Task EnsureAsync(IProgress<string>? progress, CancellationToken ct)
        => Current is { Release: true } manifest ? EnsureAsync(manifest, Directory, progress, ct) : Task.CompletedTask;

    internal static async Task EnsureAsync(Manifest manifest, string directory, IProgress<string>? progress, CancellationToken ct)
    {
        // Checked once per process: hashing the engines on every start of
        // the engine would cost a fraction of a second each time.
        if (Verified.TryGetValue(directory, out var seen) && seen == Signature(directory, manifest)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Backspace/" + manifest.Version);
            foreach (var (name, hash) in manifest.Files)
            {
                var target = Path.Combine(directory, name);
                if (Matches(target, hash)) continue;
                // Updaters before 1.0.3 put the engines beside Backspace.exe:
                // take those instead of downloading them again.
                var beside = Path.Combine(AppPaths.ExeDirectory, name);
                if (Matches(beside, hash) && TryAdopt(beside, target)) continue;
                progress?.Report($"Downloading the backup engine ({name})…");
                await FetchAsync(http, manifest, name, hash, target, ct).ConfigureAwait(false);
            }
            RemoveOtherSets(directory);
            RemoveLegacyCopies(manifest);
            Verified[directory] = Signature(directory, manifest);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Where an engine comes from: its own project's official Windows
    /// download (U102, 3 November 2026: Backspace's release offers Backspace.exe
    /// alone), with the file to take from that archive. BACKSPACE_ENGINE_SOURCE
    /// (a folder or base URL of plain engine files) replaces it for checks.</summary>
    internal static (string Url, string? Entry) Origin(Manifest manifest, string name)
    {
        if (Environment.GetEnvironmentVariable("BACKSPACE_ENGINE_SOURCE") is { Length: > 0 } source)
            return (source.Contains("://", StringComparison.Ordinal) ? source.TrimEnd('/') + "/" + name : Path.Combine(source, name), null);
        string? Pinned(string engine) => manifest.Engines?.GetValueOrDefault(engine) is { Length: > 1 } v && v.StartsWith('v') ? v : null;
        return name switch
        {
            "backrest.exe" when Pinned("backrest") is { } v
                => ($"https://github.com/garethgeorge/backrest/releases/download/{v}/backrest_Windows_x86_64.zip", "backrest.exe"),
            // Backspace's restic build (engine/restic, U137): the official
            // source with a faster exclusion matcher, until restic ships it.
            "restic.exe" when Pinned("resticBackspace") is { } b
                => ($"https://github.com/backspace-backup/backspace/releases/download/engine-restic-{b}/restic_{b[1..]}_windows_amd64.zip", $"restic_{b[1..]}_windows_amd64.exe"),
            "restic.exe" when Pinned("restic") is { } v
                => ($"https://github.com/restic/restic/releases/download/{v}/restic_{v[1..]}_windows_amd64.zip", $"restic_{v[1..]}_windows_amd64.exe"),
            "rclone.exe" when Pinned("rclone") is { } v
                => ($"https://downloads.rclone.org/{v}/rclone-{v}-windows-amd64.zip", $"rclone-{v}-windows-amd64/rclone.exe"),
            // Builds before 1.1.1 published the engines with the release.
            _ => ($"https://github.com/backspace-backup/backspace/releases/download/v{manifest.Version}/{name}", null),
        };
    }

    private static async Task FetchAsync(HttpClient http, Manifest manifest, string name, string hash, string target, CancellationToken ct)
    {
        var (source, entry) = Origin(manifest, name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".download";
        var archive = entry is null ? null : temporary + ".zip";
        try
        {
            try
            {
                await using (var output = new FileStream(archive ?? temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (source.Contains("://", StringComparison.Ordinal))
                    {
                        using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await using var input = File.OpenRead(source);
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    }
                    output.Flush(flushToDisk: true);
                }
                if (archive is not null) TakeFromArchive(archive, entry!, temporary);
            }
            catch (InvalidDataException ex)
            {
                throw new EngineBinaryMissingException(
                    $"The download of the backup engine ({name}) was not the expected archive: {ex.Message}", target);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new EngineBinaryMissingException(
                    $"Backspace couldn't download its backup engine ({name}): {ex.Message} It downloads automatically once this computer is online.",
                    target);
            }
            if (!Matches(temporary, hash))
                throw new EngineBinaryMissingException(
                    $"The downloaded backup engine ({name}) didn't match the copy this Backspace version was built with, so it wasn't used.",
                    target);
            if (!PlatformInfo.IsWindows)
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            foreach (var leftover in new[] { temporary, archive })
                try { if (leftover is not null && File.Exists(leftover)) File.Delete(leftover); } catch (IOException) { }
        }
    }

    /// <summary>Copies one named file out of a downloaded archive; nothing else
    /// in it is written anywhere.</summary>
    internal static void TakeFromArchive(string archive, string entryName, string destination)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        var entry = zip.Entries.SingleOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("it does not contain " + entryName + ".");
        if (entry.Length is <= 0 or > 1024L * 1024 * 1024) throw new InvalidDataException("its " + entryName + " has an unexpected size.");
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    /// <summary>Removes every other engine set as soon as this one is ready.
    /// One set in use is the supported case; a set still running (another
    /// Backspace version open) is locked and stays until next time, and a
    /// copy whose set was removed fetches it again when it starts.</summary>
    private static void RemoveOtherSets(string keep)
    {
        if (!System.IO.Directory.Exists(Root)) return;
        foreach (var set in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (string.Equals(Path.GetFullPath(set), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { System.IO.Directory.Delete(set, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For(nameof(EngineInstallation)).Debug(ex, "Keeping engine set {Set}", set); }
        }
    }

    /// <summary>Size and time of each engine file: a change since the last
    /// check (a damaged or replaced file) makes the next start hash them again.</summary>
    private static string Signature(string directory, Manifest manifest) => string.Join("|", manifest.Files.Keys.Order(StringComparer.Ordinal)
        .Select(n => new FileInfo(Path.Combine(directory, n))).Select(f => f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-"));

    private static bool TryAdopt(string source, string target)
    {
        try { File.Copy(source, target + ".adopt", overwrite: true); File.Move(target + ".adopt", target, overwrite: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For(nameof(EngineInstallation)).Debug(ex, "Could not reuse {Engine}; downloading it", source);
            return false;
        }
    }

    /// <summary>Deletes engines left beside Backspace.exe by an older version,
    /// once this release's own copies are in place. A copy still running (an
    /// older background service) is locked and removed on a later start.</summary>
    private static void RemoveLegacyCopies(Manifest manifest)
    {
        if (!manifest.Release || !IsComplete(Directory, manifest)) return;
        foreach (var name in manifest.Files.Keys)
        {
            var beside = Path.Combine(AppPaths.ExeDirectory, name);
            try { if (File.Exists(beside)) File.Delete(beside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For(nameof(EngineInstallation)).Debug(ex, "Leaving {Engine} until it is no longer running", beside); }
        }
    }

    internal static bool Matches(string path, string expectedHash)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return string.Equals(Convert.ToHexString(SHA256.HashData(file)), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static Manifest? ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("engine-manifest.json");
        if (stream is null) return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return manifest is { Files.Count: > 0 } && !string.IsNullOrWhiteSpace(manifest.Version) ? manifest : null;
    }
}
