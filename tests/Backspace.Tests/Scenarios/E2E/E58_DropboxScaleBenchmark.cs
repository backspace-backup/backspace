using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Opt-in Dropbox benchmark at release scale (20,000 files, 2 GiB), driven
/// one step per invocation so another tool can be timed on the same tree:
///
///   BACKSPACE_BENCHMARK_ACTION = generate | mutate | backup | tool
///   BACKSPACE_BENCHMARK_DIR    = scratch folder holding source/ and results
///   BACKSPACE_BENCHMARK_REMOTE = subfolder inside Backspace's Dropbox app folder
///   BACKSPACE_BENCHMARK_FILES  = optional file count for generate (default
///                                20,000), at the same ~105 KiB per file
///   BACKSPACE_BENCHMARK_SOURCE = optional source tree (default DIR\source)
///   BACKSPACE_BENCHMARK_SEED   = optional mutate seed for repeated rounds
///
/// <c>mutate</c> rewrites every fourth file with fresh bytes of the same size.
/// <c>backup</c> runs the real engine with the saved Dropbox test sign-in
/// (see <see cref="CloudCredentialsLoader.VaultRoot"/>), without inline
/// prune or check, and appends its timing to <c>backspace-results.jsonl</c>.
/// The repository persists across invocations, so a second backup is
/// incremental. <c>tool</c> runs the bundled restic or rclone
/// (BACKSPACE_BENCHMARK_TOOL) with the JSON string array in
/// BACKSPACE_BENCHMARK_ARGS against the same repository and sign-in, for
/// profiling outside Backrest; <c>{remote}</c> in an argument becomes the
/// rclone remote name. Output lines are appended with timestamps to
/// BACKSPACE_BENCHMARK_LOG. Without the action variable the test does nothing.
/// </summary>
public sealed class E58_DropboxScaleBenchmark
{
    [AvaloniaFact]
    public async Task Run()
    {
        var action = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_ACTION");
        var dir = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_DIR");
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(dir)) return;
        // BACKSPACE_BENCHMARK_SOURCE lets several repositories share one tree.
        var source = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_SOURCE") is { Length: > 0 } shared ? shared : Path.Combine(dir, "source");
        switch (action)
        {
            case "generate":
                Assert.False(Directory.Exists(source), "The benchmark source already exists.");
                var count = int.TryParse(Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_FILES"), out var n) ? n : 20_000;
                FixtureFactory.GenerateExactTree(source, 58_2026, count, 2L * 1024 * 1024 * 1024 / 20_000 * count);
                return;
            case "mutate":
                var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray();
                // A different BACKSPACE_BENCHMARK_SEED gives a repeat round new bytes.
                var rng = new Random(int.TryParse(Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_SEED"), out var seed) ? seed : 58_2027);
                for (var i = 0; i < files.Length; i += 4)
                {
                    var bytes = new byte[new FileInfo(files[i]).Length];
                    rng.NextBytes(bytes);
                    File.WriteAllBytes(files[i], bytes);
                }
                return;
            case "backup":
                await BackupAsync(dir, source);
                return;
            case "tool":
                await ToolAsync(dir);
                return;
            default:
                throw new ArgumentException("Unknown benchmark action: " + action);
        }
    }

    private static async Task ToolAsync(string dir)
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        var (secrets, destination, cred) = BenchmarkDestination(dir);
        var tool = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_TOOL") ?? "restic";
        var args = JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_ARGS") ?? "[]")!;
        var log = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_LOG") ?? Path.Combine(dir, "tool.log");
        var conf = new RcloneConfigBuilder(secrets).MaterialiseDestination(destination);
        try
        {
            var start = new ProcessStartInfo(tool == "rclone" ? AppPaths.RcloneBinary : AppPaths.ResticBinary)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["RCLONE_CONFIG"] = conf;
            if (tool != "rclone")
            {
                start.Environment["RESTIC_REPOSITORY"] = ResticUriResolver.Resolve(destination, secrets, conf).Uri;
                start.Environment["RESTIC_PASSWORD"] = secrets.Get(destination.StoragePasswordRef!);
            }
            start.Environment["PATH"] = Path.GetDirectoryName(AppPaths.ResticBinary) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            var remoteName = ResticUriResolver.RcloneRemoteNameFor(destination);
            foreach (var a in args) start.ArgumentList.Add(a.Replace("{remote}", remoteName));
            using var writer = new StreamWriter(log, append: true) { AutoFlush = true };
            var gate = new object();
            void Write(string kind, string? line) { if (line is not null) lock (gate) writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {kind} {line}"); }
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => Write("out", e.Data);
            process.ErrorDataReceived += (_, e) => Write("err", e.Data);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            Write("end", $"exit={process.ExitCode} seconds={watch.Elapsed.TotalSeconds:F1}");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            CloudCredentialsLoader.SaveRefreshedToken(cred, destination, secrets);
            File.Delete(conf);
        }
    }

    private static (SecretsStore Secrets, Destination Destination, CloudCredentialsLoader.Credential Credential) BenchmarkDestination(string dir)
    {
        var remote = Environment.GetEnvironmentVariable("BACKSPACE_BENCHMARK_REMOTE");
        Assert.False(string.IsNullOrWhiteSpace(remote), "Set BACKSPACE_BENCHMARK_REMOTE.");
        var cred = CloudCredentialsLoader.TryGet(CloudCredentialsLoader.Provider.Dropbox);
        Assert.True(cred is not null, CloudCredentialsLoader.SkipReason(CloudCredentialsLoader.Provider.Dropbox));

        // One vault and engine folder per benchmark so the repository password
        // and engine state survive between invocations.
        var secrets = new SecretsStore(new Backspace.Services.Platform.Windows.DpapiSecretsEncryption(), Path.Combine(dir, "vault", "secrets.bin"));
        Directory.CreateDirectory(Path.Combine(dir, "vault"));
        const string passwordRef = "benchmark-repository-password";
        if (!secrets.TryGet(passwordRef, out _)) secrets.Set(passwordRef, "benchmark-" + Guid.NewGuid().ToString("N"));
        var destination = CloudCredentialsLoader.BuildDestination(CloudCredentialsLoader.Provider.Dropbox, cred! with { Folder = remote! }, secrets);
        destination.Id = "benchmark-dropbox";
        destination.StoragePasswordRef = passwordRef;
        return (secrets, destination, cred!);
    }

    private static async Task BackupAsync(string dir, string source)
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        var (secrets, destination, cred) = BenchmarkDestination(dir);
        var backup = new Backup
        {
            Id = "benchmark-backup", Name = "Dropbox benchmark", SourcePaths = { source },
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            AbortOnMeteredNetwork = false, UseVss = false, VssDisabledAcknowledged = true,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, Path.Combine(dir, "engine"));
        var engine = new ResticBackrestEngine(sidecar, secrets);
        // The desktop app keeps its engine running, so time its start apart.
        var watch = Stopwatch.StartNew();
        await sidecar.StartAsync(CancellationToken.None);
        var engineSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var record = await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);
        watch.Stop();
        CloudCredentialsLoader.SaveRefreshedToken(cred, destination, secrets);
        File.AppendAllText(Path.Combine(dir, "backspace-results.jsonl"), JsonSerializer.Serialize(new
        {
            finishedUtc = DateTime.UtcNow, engineStartSeconds = Math.Round(engineSeconds, 1), seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            status = record.Status.ToString(), summary = record.Summary, errors = record.Errors,
        }) + Environment.NewLine);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", record.Errors));
    }
}
