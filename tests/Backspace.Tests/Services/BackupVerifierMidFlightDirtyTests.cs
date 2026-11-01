using System.Collections.Generic;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pin the <c>IsMidFlightDirty</c> contract on <see cref="FileVerifyResult"/>:
/// default false, settable via the record's init-only property, survives
/// the record's <c>with</c>-expression copy. The full replacement-loop
/// behaviour is exercised end-to-end via <see cref="BackupVerifierTests"/>
/// and E16; this file pins the discriminator the loop relies on so a
/// future change can't silently flip its default and break the loop's
/// "is this a productive replacement candidate?" decision.
/// </summary>
public class BackupVerifierMidFlightDirtyTests
{
    [Fact]
    public void IsMidFlightDirty_DefaultsToFalse()
    {
        var r = new FileVerifyResult(
            Path: "docs/file.txt",
            ExpectedBytes: 100,
            ActualBytes: 100,
            Status: FileVerifyStatus.OK,
            Detail: null);
        Assert.False(r.IsMidFlightDirty);
    }

    [Fact]
    public void IsMidFlightDirty_CanBeSetViaInitOnlyProperty()
    {
        var r = new FileVerifyResult(
            Path: "docs/file.txt",
            ExpectedBytes: 100,
            ActualBytes: 100,
            Status: FileVerifyStatus.OkNoCompare,
            Detail: "Source modified after snapshot; size matches. Byte comparison skipped.")
        { IsMidFlightDirty = true };
        Assert.True(r.IsMidFlightDirty);
    }

    [Fact]
    public void IsMidFlightDirty_SurvivesWithExpression()
    {
        // Records' `with` syntax must carry the flag forward; the
        // replacement loop's accounting filters on this property, so a
        // future refactor that uses `with` to update an unrelated
        // field shouldn't silently flip the flag back to false.
        var r1 = new FileVerifyResult("x", 1, 1, FileVerifyStatus.OkNoCompare, "")
        { IsMidFlightDirty = true };
        var r2 = r1 with { Detail = "modified" };
        Assert.True(r2.IsMidFlightDirty);
    }

    [Fact]
    public void RemoveOldestDirty_ReplacesDirtyEntries_NotAppending()
    {
        // Pin the bug code-review caught: the original replacement loop
        // AddRange'd fresh results without removing the dirty ones,
        // so dirtyCount monotonically increased and FilesPicked
        // inflated 2-3x. The fix is in-place removal of the oldest
        // dirty entries up to the count of fresh replacements, keeping
        // the result list bounded to ~FilesToSample.
        var results = new List<FileVerifyResult>
        {
            new("a", 1, 1, FileVerifyStatus.OkNoCompare, "dirty") { IsMidFlightDirty = true },
            new("b", 1, 1, FileVerifyStatus.OK, null),
            new("c", 1, 1, FileVerifyStatus.OkNoCompare, "dirty") { IsMidFlightDirty = true },
            new("d", 1, 1, FileVerifyStatus.OK, null),
        };
        var removeOldestDirty = typeof(BackupVerifier).GetMethod(
            "RemoveOldestDirty",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(removeOldestDirty);

        removeOldestDirty!.Invoke(null, new object[] { results, 1 });
        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, r => r.Path == "a");      // oldest dirty gone
        Assert.Contains(results, r => r.Path == "c" && r.IsMidFlightDirty); // newer dirty stays

        removeOldestDirty.Invoke(null, new object[] { results, 5 });
        Assert.Equal(2, results.Count); // both remaining dirties gone
        Assert.All(results, r => Assert.False(r.IsMidFlightDirty));
    }
}
