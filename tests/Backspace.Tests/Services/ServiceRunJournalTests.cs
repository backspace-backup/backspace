using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceRunJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Backspace-journal-" + Guid.NewGuid().ToString("N"));
    public ServiceRunJournalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Another_run_cannot_hide_the_first_dispatchers_completed_result()
    {
        var journal = new ServiceRunJournal(_root);
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var receipt = Encode(first, "completed", 0);
        journal.Save(receipt);
        journal.Save(Encode(second, "running", null));
        Assert.Equal(receipt, journal.ReadFinished(first));
        Assert.Null(journal.ReadFinished(second));
        journal.Save(Encode(second, "failed", 4));
        Assert.Equal(receipt, journal.ReadFinished(first));
        Assert.NotNull(journal.ReadFinished(second));
    }

    [Fact]
    public void Missing_malformed_and_mismatched_receipts_are_not_success()
    {
        var journal = new ServiceRunJournal(_root);
        var id = Guid.NewGuid().ToString("N");
        Assert.Null(journal.ReadFinished(id));
        Assert.Throws<InvalidDataException>(() => journal.ReadFinished(@"..\service-run-status"));
        journal.Save(Encode(id, "completed", 0));
        var file = Path.Combine(_root, "service-run-receipts", id + ".json");
        File.WriteAllBytes(file, Encode(Guid.NewGuid().ToString("N"), "completed", 0));
        Assert.Throws<InvalidDataException>(() => journal.ReadFinished(id));
        File.WriteAllText(file, new string('x', 4097));
        Assert.Throws<InvalidDataException>(() => journal.ReadFinished(id));
        File.WriteAllText(file, "{}");
        Assert.Throws<InvalidDataException>(() => journal.ReadFinished(id));
    }

    private static byte[] Encode(string id, string state, int? exitCode) =>
        JsonSerializer.SerializeToUtf8Bytes(new ServiceScheduledRun.Response(1, id, state, exitCode, null), ServiceRunAuthorization.Json);
    public void Dispose()
    {
        // Windows indexing/filter drivers can briefly retain the just-written
        // fixture directory. Retry cleanup only; never retry a test assertion.
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) when (until.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(50); }
        }
    }
}
