using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U123 (4 November 2026): local_c showed "about 791.54 GB" although its own
/// rules leave out the Dropbox folder, and local_d 1.03 TB, much more than
/// its Duplicacy backup. The size counted every file in the folder. It now
/// counts what the backup holds: its exclusion rules applied (excluded
/// folders aren't even read), files kept only in the cloud left out.
/// </summary>
public sealed class SourceSizeRulesTests
{
    // The shape of local_c's imported rules: Duplicacy regexes and globs.
    private const string Rules = "e:(?i)^Users/[^/]+/Dropbox/\ne:(?i)/node_modules/\ncache/\n";

    private static string Tree(TempWorkspace workspace)
    {
        var root = Path.Combine(workspace.Root, "source");
        void Put(string relative, int bytes)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        Put(@"Users\me\Documents\taxes.pdf", 1_000);
        Put(@"Users\me\Dropbox\synced.mp4", 500_000);      // excluded by a regex rule
        Put(@"Users\me\code\app\node_modules\lib.js", 70_000); // excluded anywhere
        Put(@"Users\me\code\app\main.cs", 200);
        Put(@"Users\me\AppData\cache\blob.bin", 30_000);   // excluded by a glob
        Put(@"Users\me\Dropbox.txt", 5);                    // a file, not the folder: kept
        return root;
    }

    [Fact]
    public async Task The_size_counts_only_what_the_backup_holds()
    {
        using var workspace = new TempWorkspace("size-rules");
        var root = Tree(workspace);
        var probe = new SourceSizeProbe();
        var everything = await probe.GetOrComputeAsync(root, CancellationToken.None);
        Assert.Equal(601_205, everything.Bytes);
        Assert.Equal(6, everything.FileCount);

        var backup = new Backup { FiltersText = Rules, ExcludeCloudFiles = true };
        var held = await probe.GetOrComputeAsync(root, CancellationToken.None, rules: SourceSizeRules.For(backup));
        Assert.Equal(1_205, held.Bytes);
        Assert.Equal(3, held.FileCount);
        // Each set of rules has its own result; the rule-less one is untouched.
        Assert.Equal(1_205, probe.TryGetCached(root, SourceSizeRules.For(backup))!.Value.Bytes);
        Assert.Equal(601_205, probe.TryGetCached(root)!.Value.Bytes);
        // Edited rules are counted again, not served from the earlier result.
        var fewer = await probe.GetOrComputeAsync(root, CancellationToken.None, rules: new SourceSizeRules("cache/", true));
        Assert.Equal(571_205, fewer.Bytes);
    }

    [Fact]
    public async Task Files_kept_only_in_the_cloud_are_not_counted_when_the_backup_skips_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("size-cloud");
        var root = Path.Combine(workspace.Root, "source");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "here.docx"), new byte[300]);
        var online = Path.Combine(root, "online-only.mp4");
        File.WriteAllBytes(online, new byte[9_000]);
        // How Windows marks a file whose content isn't on this computer (an
        // attribute, not a permission).
        File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
        try
        {
            var skipping = await new SourceSizeProbe().GetOrComputeAsync(root, CancellationToken.None, rules: new SourceSizeRules(null, true));
            Assert.Equal(300, skipping.Bytes);
            Assert.Equal(1, skipping.CloudOnlyFiles);
            Assert.Equal("1 file kept only in the cloud isn't counted: it isn't on this computer.", skipping.CloudOnlyNote);
            // A backup that reads them (downloading them) counts them.
            var reading = await new SourceSizeProbe().GetOrComputeAsync(root, CancellationToken.None, rules: new SourceSizeRules(null, false));
            Assert.Equal(9_300, reading.Bytes);
            Assert.Null(reading.CloudOnlyNote);
        }
        finally { File.SetAttributes(online, FileAttributes.Normal); }
    }

    [Fact]
    public async Task The_background_service_counts_with_the_same_rules()
    {
        using var workspace = new TempWorkspace("size-rules-service");
        var root = Tree(workspace);
        var measurements = new ServiceSizeMeasurements();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var started = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Start(root, new SourceSizeRules(Rules, true)), options)!;
        ServiceSizeMeasurements.Reply reply;
        do
        {
            await Task.Delay(50);
            reply = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Result(started.RunId!), options)!;
        } while (reply.State == "running");
        Assert.Equal(1_205, reply.Bytes);
        Assert.Equal(3, reply.Files);
    }

    [Fact]
    public void A_measurement_request_may_carry_long_rules_and_no_other_request_may()
    {
        using var workspace = new TempWorkspace("size-rules-request");
        ServiceHostRequest Parse(object request) => ServiceHostRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(request));
        // local_c's imported rules run to about 20 KB.
        var longRules = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"e:(?i)^Users/[^/]+/AppData/[^/]+/Folder{i}/"));
        Assert.True(Encoding.UTF8.GetByteCount(longRules) > 4096);
        var request = Parse(new { version = 1, command = "measure", path = workspace.Root, filters = longRules, skipCloudOnly = true });
        Assert.Equal(longRules, request.Filters);
        Assert.True(request.SkipCloudOnly);
        // Every other request stays small and can't carry rules.
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "activity", filters = "x" }));
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "activity", skipCloudOnly = true }));
        var id = Guid.NewGuid().ToString("N");
        var padded = Encoding.UTF8.GetBytes($"{{\"version\":1,\"command\":\"measure-result\",\"runId\":\"{id}\"}}" + new string(' ', 5000));
        Assert.Throws<InvalidDataException>(() => ServiceHostRequest.Parse(padded));
        Assert.Equal(id, Parse(new { version = 1, command = "measure-result", runId = id }).RunId);
    }

    /// <summary>U124: the speed shown is what the engine stored, measured
    /// from what its process writes (half of it: restic writes each pack to
    /// a temporary file first).</summary>
    [Fact]
    public void What_a_process_writes_can_be_read_and_its_one_child_found()
    {
        // Written twice (temporary file, then the folder or rclone's pipe), or
        // once when restic sends over the network itself, which isn't counted.
        Assert.Equal(52_439_903, EngineTransfer.Stored(104_879_806, direct: false));
        Assert.Equal(52_439_903, EngineTransfer.Stored(52_439_903, direct: true));
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("engine-transfer");
        var self = Environment.ProcessId;
        var before = EngineTransfer.Written(self)!.Value;
        using (var file = new FileStream(Path.Combine(workspace.Root, "data.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            file.Write(new byte[4_000_000]);
        Assert.True(EngineTransfer.Written(self)!.Value - before >= 4_000_000);

        Process Ping() => Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        using var one = Ping();
        try
        {
            Assert.Equal(one.Id, EngineTransfer.OnlyChild(self, "PING.EXE"));
            Assert.Null(EngineTransfer.OnlyChild(self, "restic.exe"));
            Assert.Null(EngineTransfer.SentBytes(self, direct: false)); // no engine running under this process
            using var two = Ping();
            try { Assert.Null(EngineTransfer.OnlyChild(self, "ping.exe")); } // two can't be told apart
            finally { two.Kill(); }
        }
        finally { one.Kill(); }
    }
}
