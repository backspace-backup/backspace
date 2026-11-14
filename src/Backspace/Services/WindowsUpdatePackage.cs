using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>Verified portable Windows bundles. Downloads never touch installed
/// files. Installation runs only after the GUI and its engines have stopped,
/// under an exclusive process-lifetime gate shared by scheduled runs.</summary>
internal static class WindowsUpdatePackage
{
    /// <summary>Releases from 1.1.1 on offer Backspace.exe alone (U102); the
    /// archive is what earlier releases offered.</summary>
    internal const string ExecutableAssetName = "Backspace.exe";
    internal const string AssetName = "Backspace-windows-x64.zip";
    internal const string ReleasesUrl = "https://github.com/backspace-backup/backspace/releases/latest";
    internal const string ApiUrl = "https://api.github.com/repos/backspace-backup/backspace/releases/latest";
    private const long MaxDownload = 2L * 1024 * 1024 * 1024;
    private const long MaxExpanded = 4L * 1024 * 1024 * 1024;
    internal sealed record Release(Version Version, string Tag, string Url, string Notes,
        string AssetUrl, string Digest, long Size, bool Executable = false, string? ApiAssetUrl = null, string? UnixPackage = null);

    /// <summary>The Linux or macOS package for this computer (U245), or null
    /// on Windows, whose releases offer Backspace.exe.</summary>
    internal static string? UnixPackageName() => UnixPackageName(
        OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS() ? "macos" : "linux",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64);
    internal static string? UnixPackageName(string? system, bool arm) => system switch
    {
        "linux" => "Backspace-linux-" + (arm ? "arm64" : "x64") + ".tar.gz",
        "macos" => "Backspace-macos-" + (arm ? "arm64" : "x64") + ".zip",
        _ => null,
    };
    internal sealed record FileEntry(string Name, long Size, string Sha256);
    internal sealed class Plan
    {
        public string Root { get; set; } = "";
        public string Work { get; set; } = "";
        public string ExeName { get; set; } = "Backspace.exe";
        public string Version { get; set; } = "";
        public List<FileEntry> Files { get; set; } = new();
        public List<string> PreviouslyPresent { get; set; } = new();
        public string State { get; set; } = "Prepared";
        public bool ServiceUpdate { get; set; }
    }
    internal static Version CurrentVersion => Normalize(typeof(WindowsUpdatePackage).Assembly.GetName().Version ?? new Version(0, 0));
    internal static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    internal static Release? ParseRelease(string json, Version current) => ParseRelease(json, current, UnixPackageName());

    internal static Release? ParseRelease(string json, Version current, string? unixPackage)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$")
            || !Version.TryParse(tag.TrimStart('v'), out var version) || Normalize(version) <= Normalize(current)) return null;
        var url = r.GetProperty("html_url").GetString() ?? "";
        if (!IsReleaseUrl(url)) throw new InvalidDataException("The release link is not from the Backspace repository.");
        var assets = r.GetProperty("assets").EnumerateArray().ToArray();
        var assetName = unixPackage
            ?? (assets.Any(a => a.GetProperty("name").GetString() == ExecutableAssetName) ? ExecutableAssetName : AssetName);
        var matches = assets.Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException(unixPackage is null
            ? "This release does not contain one compatible Windows update. Open the release page to update manually."
            : "This release has no " + assetName + " for this computer. Open the release page to update manually.");
        var asset = matches[0];
        var assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
        var expectedPath = "/backspace-backup/backspace/releases/download/" + tag + "/" + assetName;
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || uri.UserInfo.Length != 0 || uri.Port != 443
            || uri.AbsolutePath != expectedPath || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("The update download does not belong to this release.");
        var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("This release has no valid download checksum. Automatic installation is unavailable; open the release page instead.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxDownload) throw new InvalidDataException("The update size is outside the supported limit.");
        var notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        // The same file through GitHub's API: used when github.com's download
        // link fails (it answered 504 for hours on 8 November 2026 while the
        // API served the file). The checksum still decides.
        var apiUrl = asset.TryGetProperty("url", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() : null;
        if (apiUrl is not null && !(Uri.TryCreate(apiUrl, UriKind.Absolute, out var api) && api.Scheme == "https"
            && api.Host == "api.github.com" && api.UserInfo.Length == 0 && api.Port == 443 && api.Query.Length == 0
            && Regex.IsMatch(api.AbsolutePath, @"^/repos/backspace-backup/backspace/releases/assets/[0-9]+$")))
            apiUrl = null;
        return new Release(Normalize(version), tag, url, notes, assetUrl, digest[7..], size, assetName == ExecutableAssetName, apiUrl, unixPackage);
    }

    internal static bool IsReleaseUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0 && uri.Port == 443
        && uri.AbsolutePath.StartsWith("/backspace-backup/backspace/releases/", StringComparison.Ordinal);

    internal static bool IsProgramFile(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$")
        && name is not "." and not ".."
        && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            || name is "Backspace.deps.json" or "Backspace.runtimeconfig.json");

    /// <summary>The name of the running program on Linux, or its .app on macOS:
    /// one plain name, no folder.</summary>
    internal static bool IsUnixProgram(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9_. ()-]+$") && name is not "." and not ".."
        && !name.StartsWith('.') && (!OperatingSystem.IsMacOS() || name.EndsWith(".app", StringComparison.Ordinal));

    internal static string CreateWorkDirectory(string root)
    {
        root = Path.GetFullPath(root);
        RejectReparse(root);
        var parent = WindowsUpdateRuntime.UpdatesFolder(root);
        Directory.CreateDirectory(parent); RejectReparse(parent);
        var work = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    internal static async Task<Plan> DownloadAsync(HttpClient http, Release release, string exe,
        IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        // Space for the download, extracted bundle, previous files and a margin.
        var available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (available < checked(release.Size * 5 + 256L * 1024 * 1024))
            throw new IOException("There is not enough free space to download and safely replace this update. Free some space and try again.");
        var work = CreateWorkDirectory(root);
        var archive = Path.Combine(work, release.Executable ? "download.exe" : release.UnixPackage?.EndsWith(".tar.gz", StringComparison.Ordinal) == true ? "download.tar.gz" : "download.zip");
        try
        {
            using var response = await GetAssetAsync(http, release, ct).ConfigureAwait(false);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long count = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    count += read;
                    if (count > release.Size) throw new InvalidDataException("The download is larger than the release says it should be.");
                    if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < read + 64L * 1024 * 1024)
                        throw new IOException("Free space ran low during the update. Your installed version has not changed.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)count / release.Size * 0.85);
                }
                if (count != release.Size) throw new InvalidDataException("The update download was incomplete. Your installed version has not changed.");
            }
            if (!Hash(archive).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its checksum check. Your installed version has not changed.");
            var plan = release.UnixPackage is not null
                ? StageUnix(archive, root, work, Path.GetFileName(exe.TrimEnd('/')), release.Version.ToString(), ct)
                : release.Executable
                ? Stage(archive, root, work, Path.GetFileName(exe), release.Version.ToString())
                : Extract(archive, root, work, Path.GetFileName(exe), release.Version.ToString(), ct);
            ValidateApplicationVersion(plan, release.Version);
            progress?.Report(1);
            return plan;
        }
        catch
        {
            // Only this attempt's owned files; never sweep the install folder.
            SafeRemoveWork(root, work);
            throw;
        }
    }

    /// <summary>The release's file from github.com, or, when that fails (a
    /// server error or no connection), the same file through GitHub's API.</summary>
    internal static async Task<HttpResponseMessage> GetAssetAsync(HttpClient http, Release release, CancellationToken ct)
    {
        static HttpRequestMessage Request(string url, bool api)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Backspace-Updater");
            if (api) request.Headers.Accept.ParseAdd("application/octet-stream");
            return request;
        }
        HttpResponseMessage? first = null;
        Exception? failure = null;
        try
        {
            using var request = Request(release.AssetUrl, api: false);
            first = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (first.IsSuccessStatusCode || release.ApiAssetUrl is null || (int)first.StatusCode < 500) { first.EnsureSuccessStatusCode(); return first; }
        }
        catch (HttpRequestException ex) when (release.ApiAssetUrl is not null && ex.StatusCode is null) { failure = ex; }
        AppLogger.For(nameof(WindowsUpdatePackage)).Warning(failure, "The update download from github.com failed ({Status}); trying GitHub's API", first?.StatusCode);
        first?.Dispose();
        using var retry = Request(release.ApiAssetUrl!, api: true);
        var second = await http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        second.EnsureSuccessStatusCode();
        return second;
    }

    internal static void ValidateApplicationVersion(Plan plan, Version expected)
    {
        if (!OperatingSystem.IsWindows()) { ValidateUnixVersion(plan, expected); return; }
        // The Win32 version API needs the extended prefix beyond MAX_PATH.
        var versionPath = Path.GetFullPath(Path.Combine(plan.Work, "incoming", plan.ExeName));
        if (OperatingSystem.IsWindows() && !versionPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            versionPath = versionPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + versionPath[2..] : @"\\?\" + versionPath;
        var productVersion = FileVersionInfo.GetVersionInfo(versionPath).ProductVersion?.Split('+')[0];
        if (!Version.TryParse(productVersion, out var builtVersion) || Normalize(builtVersion) != expected)
            throw new InvalidDataException("The application version inside the download does not match the release. Your installed version has not changed.");
    }

    /// <summary>A release that is Backspace.exe alone: the new program replaces
    /// this one; its engines come from %LOCALAPPDATA%\Backspace\bin.</summary>
    internal static Plan Stage(string download, string root, string work, string exeName, string version)
    {
        var plan = new Plan { Root = Path.GetFullPath(root), Work = Path.GetFullPath(work), ExeName = exeName, Version = version };
        ValidateLocation(plan);
        var incoming = Path.Combine(work, "incoming"); Directory.CreateDirectory(incoming);
        var path = Path.Combine(incoming, exeName);
        File.Copy(download, path);
        using (var pe = File.OpenRead(path))
            if (pe.ReadByte() != 'M' || pe.ReadByte() != 'Z') throw new InvalidDataException("Backspace.exe is not a Windows executable.");
        plan.Files.Add(new FileEntry(exeName, new FileInfo(path).Length, Hash(path)));
        SavePlan(plan);
        return plan;
    }

    /// <summary>The program inside a Linux or macOS package (U245): the
    /// Backspace program from the .tar.gz, or Backspace.app from the .zip,
    /// staged under the name the user gave the running copy. Nothing else in
    /// the package is accepted.</summary>
    internal static Plan StageUnix(string package, string root, string work, string itemName, string version, CancellationToken ct)
    {
        var plan = new Plan { Root = Path.GetFullPath(root), Work = Path.GetFullPath(work), ExeName = itemName, Version = version };
        ValidateLocation(plan);
        var incoming = Path.Combine(work, "incoming"); Directory.CreateDirectory(incoming);
        var target = Path.Combine(incoming, itemName);
        if (package.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            using (var gz = new GZipStream(File.OpenRead(package), CompressionMode.Decompress))
            using (var tar = new System.Formats.Tar.TarReader(gz))
            {
                var found = false;
                for (System.Formats.Tar.TarEntry? entry; (entry = tar.GetNextEntry()) is not null;)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Name is not ("Backspace" or "./Backspace") || found
                        || entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)
                        || entry.Length is <= 0 or > MaxExpanded)
                        throw new InvalidDataException("The update contains an unexpected item: " + entry.Name);
                    entry.ExtractToFile(target, overwrite: false);
                    found = true;
                }
                if (!found) throw new InvalidDataException("The update does not contain Backspace.");
            }
            using (var elf = File.OpenRead(target))
                if (elf.ReadByte() != 0x7F || elf.ReadByte() != 'E' || elf.ReadByte() != 'L' || elf.ReadByte() != 'F')
                    throw new InvalidDataException("The update is not a Linux program.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        else
        {
            using (var zip = ZipFile.OpenRead(package))
            {
                if (zip.Entries.Count is 0 or > 64) throw new InvalidDataException("The update archive has an unexpected number of files.");
                long expanded = 0;
                Directory.CreateDirectory(target);
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith("Backspace.app/", StringComparison.Ordinal) || name.Split('/').Any(p => p is ".." or ".")
                        || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("The update contains an unsafe or unexpected path: " + entry.FullName);
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxExpanded) throw new InvalidDataException("The extracted update exceeds the size limit.");
                    var path = Path.Combine(target, name["Backspace.app/".Length..]);
                    if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: false);
                }
            }
            var program = Path.Combine(target, "Contents", "MacOS", "Backspace");
            if (!File.Exists(program) || !File.Exists(Path.Combine(target, "Contents", "Info.plist")))
                throw new InvalidDataException("The update does not contain Backspace.app.");
            using (var macho = File.OpenRead(program))
            {
                var magic = new byte[4];
                macho.ReadExactly(magic);
                if (!(magic.SequenceEqual(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }) || magic.SequenceEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE })))
                    throw new InvalidDataException("The update is not a Mac program.");
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        plan.Files.Add(new FileEntry(itemName, ItemSize(target), ItemHash(target)));
        SavePlan(plan);
        return plan;
    }

    /// <summary>Asks the downloaded program its version, as Backspace --version
    /// prints it: Linux and macOS programs carry no Windows version record.</summary>
    internal static void ValidateUnixVersion(Plan plan, Version expected)
    {
        var item = Path.Combine(plan.Work, "incoming", plan.ExeName);
        var program = Directory.Exists(item) ? Path.Combine(item, "Contents", "MacOS", "Backspace") : item;
        var start = new ProcessStartInfo(program, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.Combine(plan.Work, "incoming") };
        start.Environment["BACKSPACE_NO_UPDATE_CHECK"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidDataException("The downloaded program could not be started to check its version.");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { try { process.Kill(); } catch { } throw new InvalidDataException("The downloaded program did not report its version."); }
        var match = Regex.Match(output.GetAwaiter().GetResult(), @"Backspace (\d+\.\d+\.\d+)");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var built) || Normalize(built) != expected)
            throw new InvalidDataException("The application version inside the download does not match the release. Your installed version has not changed.");
    }

    /// <summary>A program file, or Backspace.app on macOS (a folder): its
    /// fingerprint is that of the program inside.</summary>
    internal static string ItemHash(string path) => Directory.Exists(path) ? Hash(Path.Combine(path, "Contents", "MacOS", "Backspace")) : Hash(path);
    internal static long ItemSize(string path) => Directory.Exists(path) ? new FileInfo(Path.Combine(path, "Contents", "MacOS", "Backspace")).Length : new FileInfo(path).Length;
    private static bool ItemExists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void MoveItem(string from, string to) { if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to); }

    internal static Plan Extract(string archive, string root, string work, string exeName, string version, CancellationToken ct, bool serviceUpdate = false)
    {
        var plan = new Plan { Root = Path.GetFullPath(root), Work = Path.GetFullPath(work), ExeName = exeName, Version = version, ServiceUpdate = serviceUpdate };
        ValidateLocation(plan);
        var incoming = Path.Combine(work, "incoming"); Directory.CreateDirectory(incoming);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 512) throw new InvalidDataException("The update archive has an unexpected number of files.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProgramFile(entry.FullName) || !names.Add(entry.FullName)
                || (entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !new[] { "Backspace.exe", "restic.exe", "rclone.exe" }.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The update contains an unsafe or unexpected path: " + entry.FullName);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpanded) throw new InvalidDataException("The extracted update exceeds the size limit.");
            var targetName = entry.FullName.Equals("Backspace.exe", StringComparison.OrdinalIgnoreCase) ? exeName : entry.FullName;
            if (plan.Files.Any(f => f.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The renamed application conflicts with another update file.");
            var path = Path.Combine(incoming, targetName);
            using (var source = entry.Open())
            using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long count = 0; int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); count += read;
                    if (count > entry.Length) throw new InvalidDataException("An update file exceeded its declared size.");
                    if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < read + 64L * 1024 * 1024)
                        throw new IOException("Free space ran low while unpacking the update.");
                    target.Write(buffer, 0, read);
                }
                if (count != entry.Length) throw new InvalidDataException("An update file was truncated.");
            }
            plan.Files.Add(new FileEntry(targetName, entry.Length, Hash(path)));
        }
        foreach (var required in new[] { "Backspace.exe", "restic.exe", "rclone.exe" })
        {
            if (!names.Contains(required)) throw new InvalidDataException("The update is missing " + required + ".");
            using var pe = File.OpenRead(Path.Combine(incoming, required == "Backspace.exe" ? exeName : required));
            if (pe.ReadByte() != 'M' || pe.ReadByte() != 'Z') throw new InvalidDataException(required + " is not a Windows executable.");
        }
        SavePlan(plan);
        return plan;
    }

    internal static void Install(Plan plan, Action<string> launch, Action<int>? afterMove = null)
    {
        ValidateLocation(plan);
        foreach (var f in plan.Files)
        {
            var incoming = Path.Combine(plan.Work, "incoming", f.Name);
            RejectReparse(incoming);
            if (ItemSize(incoming) != f.Size || !ItemHash(incoming).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A prepared update file changed before installation.");
            var target = Path.Combine(plan.Root, f.Name);
            if (Directory.Exists(target) != Directory.Exists(incoming)) throw new IOException("A folder occupies an update filename: " + f.Name);
            if (ItemExists(target)) RejectReparse(target);
        }
        var previous = Path.Combine(plan.Work, "previous"); Directory.CreateDirectory(previous);
        plan.PreviouslyPresent = plan.Files.Where(f => ItemExists(Path.Combine(plan.Root, f.Name))).Select(f => f.Name).ToList();
        plan.State = "Installing"; SavePlan(plan);
        try
        {
            int moved = 0;
            foreach (var f in plan.Files)
            {
                var target = Path.Combine(plan.Root, f.Name);
                // Dropbox or a virus scanner reading a program file (it often
                // hashes a new Backspace.exe) is waited out, as long as it
                // takes, instead of rolling the update back (U135).
                if (ItemExists(target)) FileAccessRetry.Run(() => MoveItem(target, Path.Combine(previous, f.Name)), target);
                afterMove?.Invoke(++moved);
                FileAccessRetry.Run(() => MoveItem(Path.Combine(plan.Work, "incoming", f.Name), target), target);
                afterMove?.Invoke(++moved);
            }
            // Launcher exceptions roll the entire bundle back, including files
            // added by this version. Previous files are retained after success.
            launch(Path.Combine(plan.Root, plan.ExeName));
            plan.State = "Installed"; SavePlan(plan);
        }
        catch
        {
            RollBack(plan);
            throw;
        }
    }

    internal static void RollBack(Plan plan)
    {
        ValidateLocation(plan);
        // A recovery launcher from an older update must not replace a later
        // installation or discard a file someone changed since this attempt.
        foreach (var f in plan.Files)
        {
            var target = Path.Combine(plan.Root, f.Name);
            var previous = Path.Combine(plan.Work, "previous", f.Name);
            if (ItemExists(previous)) RejectReparse(previous);
            if (ItemExists(target) && (ItemExists(previous)
                || (!plan.PreviouslyPresent.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
                    && !ItemExists(Path.Combine(plan.Work, "incoming", f.Name)))))
            {
                RejectReparse(target);
                if (!ItemHash(target).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Application files changed after this update. Automatic rollback was stopped; the previous files are preserved in " + plan.Work);
            }
        }
        foreach (var f in plan.Files.AsEnumerable().Reverse())
        {
            var previous = Path.Combine(plan.Work, "previous", f.Name);
            var target = Path.Combine(plan.Root, f.Name);
            var incoming = Path.Combine(plan.Work, "incoming", f.Name);
            if (ItemExists(previous))
            {
                if (ItemExists(target)) FileAccessRetry.Run(() => MoveItem(target, incoming), target);
                FileAccessRetry.Run(() => MoveItem(previous, target), target);
            }
            else if (!plan.PreviouslyPresent.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
                     && !ItemExists(incoming) && ItemExists(target)) FileAccessRetry.Run(() => MoveItem(target, incoming), target);
        }
        plan.State = "RolledBack"; SavePlan(plan);
    }

    internal static string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }
    internal static void SavePlan(Plan plan) => SavePlan(plan, File.Move, Thread.Sleep);

    internal static void SavePlan(Plan plan, Action<string, string, bool> moveFile, Action<int> delay)
    {
        var path = Path.Combine(plan.Work, "plan.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(plan));
        SavePlanReplacement(path + ".new", path, moveFile, delay);
    }

    private static void SavePlanReplacement(string stagedPath, string planPath,
        Action<string, string, bool> moveFile, Action<int> delay)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                moveFile(stagedPath, planPath, true);
                return;
            }
            catch (Exception ex) when (FileReplacementErrors.CanRetry(ex))
            {
                // Security software and sync clients can hold a just-written plan.
                // Keep the complete staged record and retry, as long as it takes
                // (U135), only Windows errors known to leave the previous plan
                // intact; never replay an uncertain rename.
                delay(Math.Min(1000, 50 << Math.Min(attempt, 5)));
            }
        }
    }
    internal static void ValidateLocation(Plan plan)
    {
        var root = Path.GetFullPath(plan.Root);
        var parent = plan.ServiceUpdate
            ? Path.Combine(root, AppPaths.AppFolderName, "windows-service", "control", "updates")
            : WindowsUpdateRuntime.UpdatesFolder(root);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.Work)), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(plan.Work), "N", out _)
            || !(OperatingSystem.IsWindows() ? IsProgramFile(plan.ExeName) && plan.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) : IsUnixProgram(plan.ExeName))
            || plan.Files.Any(f => !(OperatingSystem.IsWindows() ? IsProgramFile(f.Name) : IsUnixProgram(f.Name)))
            || plan.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Count)
            throw new InvalidDataException("The update location or file list is invalid.");
        RejectReparse(root); RejectReparse(parent); RejectReparse(plan.Work);
        if (plan.ServiceUpdate)
        {
            RejectReparse(Path.Combine(root, AppPaths.AppFolderName));
            RejectReparse(Path.Combine(root, AppPaths.AppFolderName, "windows-service"));
            RejectReparse(Path.Combine(root, AppPaths.AppFolderName, "windows-service", "control"));
        }
        foreach (var dir in new[] { "incoming", "previous" })
            if (Directory.Exists(Path.Combine(plan.Work, dir))) RejectReparse(Path.Combine(plan.Work, dir));
    }
    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Automatic updates cannot replace linked files or folders. Use the manual download instead.");
    }
    internal static void SafeRemoveWork(string root, string work, bool serviceUpdate = false, Action<Exception>? onFailure = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(work) && !File.Exists(work)) return;
                // Revalidate the entire owned tree on EVERY retry. Never use a
                // prior path inspection to authorize deleting a replacement.
                ValidateLocation(new Plan { Root = root, Work = work, ServiceUpdate = serviceUpdate });
                foreach (var item in Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories)) RejectReparse(item);
                Directory.Delete(work, true);
                return;
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(50 << attempt); }
            catch (Exception ex) { onFailure?.Invoke(ex); return; /* Retain work if safe cleanup fails. */ }
        }
    }
}
