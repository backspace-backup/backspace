using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RcloneConfigFileLockTests
{
    [Fact]
    public async Task Materialise_waits_for_a_real_temporary_Windows_replacement_lock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var builder = new RcloneConfigBuilder(new SecretsStore());
        var destination = new Destination { Kind = DestinationKind.DropboxAppScoped };
        var path = builder.MaterialiseDestination(destination);
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "# Old formatting\n" + original);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task<string> rewrite;
        try
        {
            rewrite = Task.Run(() => builder.MaterialiseDestination(destination));
            // The temporary file proves the real writer reached replacement
            // while our handle still denied it; waiting for a task alone does not.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any()
                && !rewrite.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            await Task.Delay(150);
            Assert.False(rewrite.IsCompleted);
        }
        finally { held.Dispose(); }
        Assert.Equal(path, await rewrite.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Materialise_reports_a_persistent_lock_without_damaging_the_previous_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var builder = new RcloneConfigBuilder(new SecretsStore());
        var destination = new Destination { Kind = DestinationKind.DropboxAppScoped };
        var path = builder.MaterialiseDestination(destination);
        var original = "# Keep this valid configuration\n" + File.ReadAllText(path);
        File.WriteAllText(path, original);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => builder.MaterialiseDestination(destination));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        // A later operation can recover normally without a stale mutex or temp.
        builder.MaterialiseDestination(destination);
        Assert.DoesNotContain("Keep this valid configuration", File.ReadAllText(path));
    }

    [Fact]
    public void Materialise_does_not_rewrite_an_unchanged_file_even_when_replacement_is_locked()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var builder = new RcloneConfigBuilder(new SecretsStore());
        var destination = new Destination { Kind = DestinationKind.DropboxAppScoped };
        var path = builder.MaterialiseDestination(destination);
        var written = File.GetLastWriteTimeUtc(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(path, builder.MaterialiseDestination(destination));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
