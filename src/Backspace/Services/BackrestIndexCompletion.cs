using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

internal static class BackrestIndexCompletion
{
    internal static async Task WaitAsync(BackrestClient client, string repoId, string repoGuid,
        string snapshotId, BackrestIndexCounters before, MaintenanceCoordinator maintenance, CancellationToken ct,
        TimeSpan? uncertaintyTimeout = null, bool schedulingUncertain = false)
    {
        DateTime? uncertainUntil = null;
        while (true)
        {
            if (ct.IsCancellationRequested || schedulingUncertain)
                uncertainUntil ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
            var failed = false;
            var confirmed = false;
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var after = await client.ReadIndexCountersAsync(repoId, probe.Token).ConfigureAwait(false);
                var indexed = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                    "/v1.Backrest/GetOperations", new()
                    { Selector = new() { RepoGuid = repoGuid, SnapshotId = snapshotId }, LastN = 32 }, probe.Token).ConfigureAwait(false);
                confirmed = after.Success > before.Success && indexed.Operations?.Any(op =>
                    op.SnapshotId == snapshotId && op.Status == "STATUS_SUCCESS" &&
                    op.OperationIndexSnapshot is not null) == true;
                failed = after.Failed > before.Failed || after.Success < before.Success || after.Failed < before.Failed;
                if (!ct.IsCancellationRequested && !schedulingUncertain) uncertainUntil = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && uncertainUntil is not null)
            {
                // Cancellation is not evidence that the untracked daemon task
                // stopped. Keep the repository lease while seeking confirmation.
            }
            catch (Exception ex)
            {
                AppLogger.For("BackrestIndexCompletion").Debug(ex, "Waiting for snapshot indexing confirmation");
                uncertainUntil ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
            }
            if (confirmed) { ct.ThrowIfCancellationRequested(); return; }
            if (failed || uncertainUntil is not null && DateTime.UtcNow >= uncertainUntil)
            {
                const string reason = "The backup was saved, but the engine could not confirm that updating its snapshot history finished. Restart Backspace before continuing.";
                maintenance.BlockUntilRestart(reason);
                throw new IOException(reason);
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
