using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Backspace.Services;
using Backspace.Tests.Scenarios;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U247: the window froze for about 40 s when a drive didn't answer (here an
/// SFTP mount of a phone out of reach): the backup editor, warmed up on the
/// window's thread at start, asked every drive whether it holds Google
/// Drive's "My Drive". Now a drive that doesn't answer in time is left out,
/// and the answer is kept, so the editor doesn't ask again.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class CloudFolderDetectionTests
{
    [Fact]
    public void A_drive_that_does_not_answer_is_left_out_without_waiting_for_it()
    {
        var probe = RecommendedFilters.HoldsMyDrive;
        var answerTime = RecommendedFilters.DriveAnswerTime;
        using var release = new ManualResetEventSlim();
        try
        {
            RecommendedFilters.DriveRootsOverride = () => ["X:\\", "Y:\\"];
            RecommendedFilters.DriveAnswerTime = TimeSpan.FromMilliseconds(500);
            var asked = 0;
            RecommendedFilters.HoldsMyDrive = root =>
            {
                Interlocked.Increment(ref asked);
                if (root == "X:\\") { release.Wait(TimeSpan.FromSeconds(30)); return true; }
                return true;
            };
            RecommendedFilters.ForgetCloudSyncFolders();

            var watch = Stopwatch.StartNew();
            var found = RecommendedFilters.DetectCloudSyncFolders();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Waited {watch.Elapsed.TotalSeconds:0.0} s for a drive that doesn't answer.");
            // Joined as the app joins it, so the test holds on Linux and macOS too.
            Assert.Contains(found, f => f.Path == System.IO.Path.Combine("Y:\\", "My Drive"));
            Assert.DoesNotContain(found, f => f.Path == System.IO.Path.Combine("X:\\", "My Drive"));

            // Asked again (the editor opening): the kept answer, no drive asked.
            var before = asked;
            watch.Restart();
            Assert.Equal(found, RecommendedFilters.DetectCloudSyncFolders());
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(100));
            Assert.Equal(before, asked);
        }
        finally
        {
            release.Set();
            RecommendedFilters.HoldsMyDrive = probe;
            RecommendedFilters.DriveAnswerTime = answerTime;
            RecommendedFilters.DriveRootsOverride = null;
            RecommendedFilters.ForgetCloudSyncFolders();
        }
    }
}
