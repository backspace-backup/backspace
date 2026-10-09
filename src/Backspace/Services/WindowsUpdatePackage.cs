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
    internal const string AssetName = "Backspace-windows-x64.zip";
    internal const string ReleasesUrl = "https://github.com/backspace-backup/backspace/releases/latest";
    internal const string ApiUrl = "https://api.github.com/repos/backspace-backup/backspace/releases/latest";
    private const long MaxDownload = 2L * 1024 * 1024 * 1024;
    private const long MaxExpanded = 4L * 1024 * 1024 * 1024;
    internal sealed record Release(Version Version, string Tag, string Url, string Notes,
        string AssetUrl, string Digest, long Size);
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
    }
    internal static Version CurrentVersion => Normalize(typeof(WindowsUpdatePackage).Assembly.GetName().Version ?? new Version(0, 0));
    internal static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    internal static Release? ParseRelease(string json, Version current)
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
        var matches = r.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("This release does not contain one compatible Windows update. Open the release page to update manually.");
        var asset = matches[0];
        var assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
        var expectedPath = "/backspace-backup/backspace/releases/download/" + tag + "/" + AssetName;
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
        return new Release(Normalize(version), tag, url, notes, assetUrl, digest[7..], size);
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

    internal static string CreateWorkDirectory(string root)
    {
        root = Path.GetFullPath(root);
        RejectReparse(root);
        var parent = Path.Combine(root, ".backspace-updates");
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
        var archive = Path.Combine(work, "download.zip");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
            request.Headers.UserAgent.ParseAdd("Backspace-Updater");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
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
            var plan = Extract(archive, root, work, Path.GetFileName(exe), release.Version.ToString(), ct);
            var productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(work, "incoming", plan.ExeName)).ProductVersion?.Split('+')[0];
            if (!Version.TryParse(productVersion, out var builtVersion) || Normalize(builtVersion) != release.Version)
                throw new InvalidDataException("The application version inside the download does not match the release. Your installed version has not changed.");
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

    internal static Plan Extract(string archive, string root, string work, string exeName, string version, CancellationToken ct)
    {
        var plan = new Plan { Root = Path.GetFullPath(root), Work = Path.GetFullPath(work), ExeName = exeName, Version = version };
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
                    && !new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" }.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase))
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
        foreach (var required in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" })
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
            if (new FileInfo(incoming).Length != f.Size || !Hash(incoming).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A prepared update file changed before installation.");
            var target = Path.Combine(plan.Root, f.Name);
            if (Directory.Exists(target)) throw new IOException("A folder occupies an update filename: " + f.Name);
            if (File.Exists(target)) RejectReparse(target);
        }
        var previous = Path.Combine(plan.Work, "previous"); Directory.CreateDirectory(previous);
        plan.PreviouslyPresent = plan.Files.Where(f => File.Exists(Path.Combine(plan.Root, f.Name))).Select(f => f.Name).ToList();
        plan.State = "Installing"; SavePlan(plan);
        try
        {
            int moved = 0;
            foreach (var f in plan.Files)
            {
                var target = Path.Combine(plan.Root, f.Name);
                if (File.Exists(target)) File.Move(target, Path.Combine(previous, f.Name));
                afterMove?.Invoke(++moved);
                File.Move(Path.Combine(plan.Work, "incoming", f.Name), target);
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
            if (File.Exists(previous)) RejectReparse(previous);
            if (File.Exists(target) && (File.Exists(previous)
                || (!plan.PreviouslyPresent.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
                    && !File.Exists(Path.Combine(plan.Work, "incoming", f.Name)))))
            {
                RejectReparse(target);
                if (!Hash(target).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Application files changed after this update. Automatic rollback was stopped; the previous files are preserved in " + plan.Work);
            }
        }
        foreach (var f in plan.Files.AsEnumerable().Reverse())
        {
            var previous = Path.Combine(plan.Work, "previous", f.Name);
            var target = Path.Combine(plan.Root, f.Name);
            var incoming = Path.Combine(plan.Work, "incoming", f.Name);
            if (File.Exists(previous))
            {
                if (File.Exists(target)) File.Move(target, incoming);
                File.Move(previous, target);
            }
            else if (!plan.PreviouslyPresent.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
                     && !File.Exists(incoming) && File.Exists(target)) File.Move(target, incoming);
        }
        plan.State = "RolledBack"; SavePlan(plan);
    }

    internal static string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }
    internal static void SavePlan(Plan plan)
    {
        var path = Path.Combine(plan.Work, "plan.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(plan));
        File.Move(path + ".new", path, true);
    }
    internal static void ValidateLocation(Plan plan)
    {
        var root = Path.GetFullPath(plan.Root);
        var parent = Path.Combine(root, ".backspace-updates");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.Work)), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(plan.Work), "N", out _) || !IsProgramFile(plan.ExeName)
            || !plan.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || plan.Files.Any(f => !IsProgramFile(f.Name))
            || plan.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Count)
            throw new InvalidDataException("The update location or file list is invalid.");
        RejectReparse(root); RejectReparse(parent); RejectReparse(plan.Work);
        foreach (var dir in new[] { "incoming", "previous" })
            if (Directory.Exists(Path.Combine(plan.Work, dir))) RejectReparse(Path.Combine(plan.Work, dir));
    }
    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Automatic updates cannot replace linked files or folders. Use the manual download instead.");
    }
    internal static void SafeRemoveWork(string root, string work)
    {
        try
        {
            ValidateLocation(new Plan { Root = root, Work = work });
            foreach (var item in Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories)) RejectReparse(item);
            Directory.Delete(work, true);
        }
        catch { /* Leave only this attempt's staging folder if safe cleanup fails. */ }
    }
}
