using System.IO;
using System.Net;
using System.Text.Json;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Unit tests for the parts of <see cref="BackrestSidecar"/> that don't
/// need an actual backrest binary: the ephemeral port allocator and the
/// pre-launch config-seeding. Process lifecycle is covered by manual
/// smoke-testing when the binary is fetched.
/// </summary>
public class BackrestSidecarTests
{
    [Fact]
    public void AllocateEphemeralPort_returnsLoopbackBindable()
    {
        var port = BackrestSidecar.AllocateEphemeralPort();
        Assert.InRange(port, 1024, 65535);

        // We don't try to re-bind here — the OS may not have released
        // the port yet (TIME_WAIT). The contract is "the kernel told us
        // it's free at this instant", not "guaranteed-free forever."
    }

    [Fact]
    public void AllocateEphemeralPort_producesDifferentPortsAcrossCalls()
    {
        // Probabilistic but practically certain: the ephemeral range is
        // large and the OS doesn't immediately reuse closed ports.
        var p1 = BackrestSidecar.AllocateEphemeralPort();
        var p2 = BackrestSidecar.AllocateEphemeralPort();
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void WriteSeedConfig_neverDisablesAuthentication()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "backspace-sidecar-tests-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var configPath = Path.Combine(tmpDir, "config.json");
            BackrestSidecar.WriteSeedConfig(configPath);

            Assert.True(File.Exists(configPath));
            var raw = File.ReadAllText(configPath);

            // The seed config must be parseable by Backrest. We deserialise
            // it through our own model to verify the wire shape stays
            // valid, then check the load-bearing fields.
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            Assert.Equal("backspace", root.GetProperty("instance").GetString());
            Assert.False(root.GetProperty("auth").GetProperty("disabled").GetBoolean());

            // The repos/plans arrays should be present and empty — the
            // engine adds entries via SetConfig once a backup is wired.
            Assert.Equal(0, root.GetProperty("repos").GetArrayLength());
            Assert.Equal(0, root.GetProperty("plans").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void WriteSeedConfig_createsParentDirectoryIfMissing()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "backspace-sidecar-tests-mkdir-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var configPath = Path.Combine(tmpDir, "nested", "deep", "config.json");
            Assert.False(Directory.Exists(Path.GetDirectoryName(configPath)));

            BackrestSidecar.WriteSeedConfig(configPath);

            Assert.True(File.Exists(configPath));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task MissingPassword_IsReplacedBeforeLaunch_WithoutLosingOtherConfiguration()
    {
        // The orphan-auth recovery scenario: user deleted
        // sidecar-auth.txt but kept config.json. Before Backrest
        // boots we replace only its credentials on disk. There must
        // never be an unprotected listener or loss of repository settings.
        var tmpDir = Path.Combine(Path.GetTempPath(),
            "backspace-strip-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var configPath = Path.Combine(tmpDir, "config.json");
        File.WriteAllText(configPath, """
            {
              "modno": 3,
              "version": 6,
              "instance": "backspace",
              "auth": {
                "disabled": false,
                "users": [
                  { "name": "backspace", "passwordBcrypt": "$2a$10$abcdef..." }
                ]
              },
              "repos": [ { "id": "r1", "uri": "C:/data" } ],
              "unknownFutureField": { "keep": true }
            }
            """);
        try
        {
            await using var sidecar = new BackrestSidecar("unused", "unused", tmpDir);
            sidecar.PrepareAuthentication();
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            Assert.Equal("backspace", root.GetProperty("instance").GetString());
            Assert.Equal(1, root.GetProperty("repos").GetArrayLength());
            Assert.False(root.GetProperty("auth").GetProperty("disabled").GetBoolean());
            Assert.True(root.GetProperty("unknownFutureField").GetProperty("keep").GetBoolean());
            var hash = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(
                root.GetProperty("auth").GetProperty("users")[0].GetProperty("passwordBcrypt").GetString()!));
            var password = File.ReadAllText(Path.Combine(tmpDir, "sidecar-auth.txt"));
            Assert.True(BCrypt.Net.BCrypt.Verify(password, hash));
            Assert.False(BCrypt.Net.BCrypt.Verify("wrong password", hash));
            var prepared = File.ReadAllText(configPath);
            sidecar.PrepareAuthentication();
            Assert.Equal(prepared, File.ReadAllText(configPath));
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void TryParsePidFile_ParsesWellFormedLine()
    {
        var ok = BackrestSidecar.TryParsePidFile(
            "12345|C:/Program Files/Backspace/backrest.exe",
            out var pid, out var bin);

        Assert.True(ok);
        Assert.Equal(12345, pid);
        Assert.Equal("C:/Program Files/Backspace/backrest.exe", bin);
    }

    [Fact]
    public void TryParsePidFile_TrimsTrailingNewlines()
    {
        // pid files are written via File.WriteAllText today (no
        // trailing newline) but historical writers / manual edits add
        // one; tolerate.
        var ok = BackrestSidecar.TryParsePidFile(
            " 42|/usr/local/bin/backrest \n",
            out var pid, out var bin);

        Assert.True(ok);
        Assert.Equal(42, pid);
        Assert.Equal("/usr/local/bin/backrest", bin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-pid|something")]
    [InlineData("0|/usr/local/bin/backrest")]         // pid zero rejected
    [InlineData("-1|/usr/local/bin/backrest")]        // negative rejected
    [InlineData("12345")]                              // no separator
    [InlineData("|/usr/local/bin/backrest")]           // empty pid
    [InlineData("12345|")]                             // empty binary
    public void TryParsePidFile_RejectsMalformed(string raw)
    {
        var ok = BackrestSidecar.TryParsePidFile(raw, out var pid, out var bin);
        Assert.False(ok);
        Assert.Equal(0, pid);
        Assert.Equal("", bin);
    }

    [Fact]
    public void PidFilePath_LivesUnderDataDir()
    {
        // Sanity: cleanup tooling and operator queries depend on this
        // path being predictable. Pin the layout so an accidental
        // refactor (moving the file to AppData or similar) trips this
        // test instead of breaking the orphan-kill flow silently.
        var sidecar = new BackrestSidecar(
            backrestBinaryPath: "/dev/null/backrest",
            resticBinaryPath: "/dev/null/restic",
            dataDir: "/tmp/backspace-test-datadir");

        Assert.EndsWith("sidecar.pid", sidecar.PidFilePath);
        Assert.StartsWith("/tmp/backspace-test-datadir", sidecar.PidFilePath.Replace('\\', '/'));
    }

    [Fact]
    public async System.Threading.Tasks.Task StartAsync_throwsHelpfulErrorIfBinaryMissing()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-such-backrest-" + System.Guid.NewGuid().ToString("N"));
        var alsoNonexistent = Path.Combine(Path.GetTempPath(), "no-such-restic-" + System.Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(Path.GetTempPath(), "backspace-sidecar-data-" + System.Guid.NewGuid().ToString("N"));

        var sidecar = new BackrestSidecar(nonexistent, alsoNonexistent, dataDir);

        // EngineBinaryMissingException inherits from FileNotFoundException;
        // ThrowsAnyAsync accepts the derived type. The message must
        // name the binary so the user knows exactly what to install.
        var ex = await Assert.ThrowsAnyAsync<FileNotFoundException>(() => sidecar.StartAsync());
        Assert.Contains("backrest", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.IsType<Backspace.Services.EngineBinaryMissingException>(ex);
    }

    /// <summary>
    /// Regression for the silent-overwrite portability bug. A
    /// previously-launched Backrest persisted a config with
    /// plans/repos; on the next launch <see cref="BackrestSidecar.WriteSeedConfig"/>
    /// must NOT clobber it.
    /// </summary>
    [Fact]
    public void WriteSeedConfig_preservesCompatibleExistingConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bsp-seed-preserve-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = Path.Combine(dir, "config.json");
            var preserved = """{"modno":42,"version":6,"instance":"backspace","repos":[{"id":"r1","guid":"abc"}]}""";
            File.WriteAllText(cfg, preserved);
            BackrestSidecar.WriteSeedConfig(cfg);
            Assert.Equal(preserved, File.ReadAllText(cfg));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Regression for the schema-skew variant of the bug. An existing
    /// config whose schema version is newer or corrupt must remain intact.
    /// </summary>
    [Theory]
    [InlineData("{\"version\":999,\"repos\":[{\"id\":\"keep\"}]}")]
    [InlineData("{\"version\":6,\"repos\":[")]
    [InlineData("{}")]
    public void WriteSeedConfig_rejectsUnsupportedConfigWithoutReplacingIt(string original)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bsp-seed-overwrite-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = Path.Combine(dir, "config.json");
            File.WriteAllText(cfg, original);
            Assert.Throws<IOException>(() => BackrestSidecar.WriteSeedConfig(cfg));
            Assert.Equal(original, File.ReadAllText(cfg));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Regression for the seed-version-was-4-but-binary-expects-6 bug.
    /// <see cref="BackrestSidecar.SeedConfigVersion"/> must match what
    /// Backrest 1.13.0 actually writes when bootstrapped — verified
    /// by launching the bundled binary and reading its log.
    /// </summary>
    [Fact]
    public void SeedConfigVersion_matchesBundledBackrestExpectation()
    {
        // Hard-coded against the observed value (1.13.0 → 6). If we
        // bump the bundled binary to a version with a different schema,
        // this test fails and forces a deliberate constant update.
        Assert.Equal(6, BackrestSidecar.SeedConfigVersion);
    }
}
