using System;
using System.IO;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>Retain terminal run receipts so a quick subsequent run cannot
/// replace the result a scheduled dispatcher is still waiting to collect.</summary>
internal sealed class ServiceRunJournal(string dataDirectory)
{
    internal void Save(byte[] bytes)
    {
        var result = Parse(bytes);
        if (result.RunId is not null && IsTerminal(result.State))
        {
            var directory = Path.Combine(dataDirectory, "service-run-receipts");
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, result.RunId + ".json"), bytes);
        }
        WriteAtomic(Path.Combine(dataDirectory, "service-run-status.json"), bytes);
    }

    internal byte[]? ReadFinished(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("Invalid run identity.");
        var path = Path.Combine(dataDirectory, "service-run-receipts", runId + ".json");
        using var file = OpenIfPresent(path);
        if (file is null) return null;
        if (file.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid run receipt size.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var result = Parse(bytes);
        if (result.RunId != runId || !IsTerminal(result.State))
            throw new InvalidDataException("The saved receipt does not match this finished run.");
        return bytes;
    }

    private static FileStream? OpenIfPresent(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static bool IsTerminal(string state) => state is "completed" or "failed" or "failed-to-start" or "interrupted";
    private static ServiceScheduledRun.Response Parse(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<ServiceScheduledRun.Response>(bytes, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("Missing run receipt.");
        if (result.Version != 1 || result.Error is not null
            || (result.RunId is not null && !Guid.TryParseExact(result.RunId, "N", out _)))
            throw new InvalidDataException("Invalid run receipt.");
        return result;
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
