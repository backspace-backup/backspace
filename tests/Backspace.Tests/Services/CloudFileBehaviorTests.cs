using System.IO;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Sanity-checks the cloud-file attribute probe. Synthesising a real
/// online-only placeholder requires a registered cloud filter driver,
/// which we don't have in CI — these tests cover the predicate's
/// correctness on real on-disk files (which must NOT be flagged as
/// cloud-only) and on missing paths (which must NOT throw). The
/// documented behavior for actual cloud-only files lives in the
/// XML doc on <see cref="CloudFileBehavior"/>.
/// </summary>
public class CloudFileBehaviorTests
{
    [Fact]
    public void IsCloudOnly_OnRegularFile_ReturnsFalse()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "ordinary local content");
            Assert.False(CloudFileBehavior.IsCloudOnly(tmp));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void IsCloudOnly_OnMissingFile_ReturnsFalse()
    {
        // Caller may walk a directory while the user moves files in
        // and out — a transient FileNotFoundException would surface as
        // a stuck enumeration. Predicate swallows the error.
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-such-file-" + System.Guid.NewGuid().ToString("N"));
        Assert.False(CloudFileBehavior.IsCloudOnly(nonexistent));
    }

    [Fact]
    public void IsCloudOnly_OnEmptyOrNullPath_ReturnsFalse()
    {
        Assert.False(CloudFileBehavior.IsCloudOnly(""));
        Assert.False(CloudFileBehavior.IsCloudOnly(null!));
    }

    [Fact]
    public void ExplainBackupBehavior_DistinctMessagesPerFlag()
    {
        // The tooltip pulls its text through this helper. The two
        // explanations need to be substantively different so a user
        // toggling the switch sees real signal in the tooltip, not
        // just punctuation drift.
        var on  = CloudFileBehavior.ExplainBackupBehavior(excludeCloudFiles: true);
        var off = CloudFileBehavior.ExplainBackupBehavior(excludeCloudFiles: false);

        Assert.NotEqual(on, off);
        Assert.Contains("skipped", on, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("download", off, System.StringComparison.OrdinalIgnoreCase);
    }
}
