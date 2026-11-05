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
    public void Materialise_waits_out_a_lock_without_damaging_the_previous_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var builder = new RcloneConfigBuilder(new SecretsStore());
        var destination = new Destination { Kind = DestinationKind.DropboxAppScoped };
        var path = builder.MaterialiseDestination(destination);
        var original = "# Keep this valid configuration\n" + File.ReadAllText(path);
        File.WriteAllText(path, original);
        // Held by another program (Dropbox, a virus scanner): the write waits,
        // as long as it takes (U135), leaving the previous file whole meanwhile.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writing = System.Threading.Tasks.Task.Run(() => builder.MaterialiseDestination(destination));
        System.Threading.Thread.Sleep(2500);
        Assert.False(writing.IsCompleted);
        Assert.Equal(original, File.ReadAllText(path));
        held.Dispose();
        writing.GetAwaiter().GetResult();
        Assert.DoesNotContain("Keep this valid configuration", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
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
