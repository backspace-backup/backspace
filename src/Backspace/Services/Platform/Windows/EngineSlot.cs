using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// The engine folder of one background worker (U156). The service runs
/// several workers at once, and two engines can't share a data folder (each
/// takes it alone: <see cref="BackrestSidecar.AcquireDataDirectoryLease"/>).
/// The first free one of <c>backrest</c>, <c>backrest-1</c>…<c>backrest-7</c>
/// is claimed for the worker's lifetime with <c>worker-slot-N.lock</c> beside
/// it. The folders are kept between runs, like the first one always was, so
/// each engine keeps its operation history (when it last pruned, for one).
/// </summary>
internal sealed class EngineSlot : IDisposable
{
    internal const int Count = Models.BackupLimits.Maximum;
    private readonly FileStream _claim;

    private EngineSlot(FileStream claim, string dataDirectory, int index)
    {
        _claim = claim; DataDirectory = dataDirectory; Index = index;
    }

    internal string DataDirectory { get; }
    internal int Index { get; }

    internal static string DirectoryFor(string first, int index) => index == 0 ? first : first + "-" + index;

    /// <summary>Waits for a free folder; more workers than folders wait their turn.</summary>
    internal static async Task<EngineSlot> ClaimAsync(string first, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(first).TrimEnd('\\', '/'))!;
        Directory.CreateDirectory(root);
        while (true)
        {
            for (var index = 0; index < Count; index++)
            {
                var path = Path.Combine(root, $"worker-slot-{index}.lock");
                try
                {
                    var claim = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new EngineSlot(claim, DirectoryFor(first, index), index);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException ex) when (FileAccessRetry.IsLock(ex)) { }
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _claim.Dispose();
}
