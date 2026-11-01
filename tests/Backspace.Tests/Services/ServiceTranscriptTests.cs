using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceTranscriptTests
{
    [Fact]
    public void Reads_only_the_requested_record_and_caps_the_utf8_tail()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("logs"); var file = Path.Combine(root, "run.log");
        File.WriteAllText(file, new string('é', 80000) + "\nEND OF REQUESTED RUN", new UTF8Encoding(false));
        var run = new RunRecord { BackupId = "b", LogPath = file };
        var protocol = new ServiceTranscriptProtocol(root, _ => [run]);
        var result = Send(protocol, new(1, "b", run.Id));
        Assert.Null(result.Error); Assert.True(result.Truncated);
        Assert.EndsWith("END OF REQUESTED RUN", result.Text); Assert.DoesNotContain('\uFFFD', result.Text);
        Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= ServiceTranscriptProtocol.TailBytes);
        Assert.Equal("run-not-found", Send(protocol, new(1, "other", run.Id)).Error);
        Assert.Equal("run-not-found", Send(protocol, new(1, "b", Guid.NewGuid().ToString("N"))).Error);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("missing")]
    [InlineData("wrong-extension")]
    public void Unavailable_or_outside_files_are_never_returned(string kind)
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("logs");
        var file = kind == "outside" ? Path.Combine(ws.Root, "private.log") : Path.Combine(root, kind == "missing" ? "missing.log" : "vault.bin");
        if (kind != "missing") File.WriteAllText(file, "must not be returned");
        var run = new RunRecord { BackupId = "b", LogPath = file };
        var result = Send(new(root, _ => [run]), new(1, "b", run.Id));
        Assert.NotNull(result.Error); Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("duplicate")]
    [InlineData("invalid-id")]
    public void Request_cannot_supply_a_file_or_ambiguous_identity(string kind)
    {
        var bytes = JsonSerializer.Serialize(new ServiceTranscriptProtocol.Request(1, "b", Guid.NewGuid().ToString("N")), ServiceRunAuthorization.Json);
        bytes = kind switch
        {
            "path" => bytes[..^1] + ",\"path\":\"C:/Windows/secret.log\"}",
            "duplicate" => bytes[..^1] + ",\"runId\":\"other\"}",
            _ => "{\"version\":1,\"backupId\":\"b\",\"runId\":\"../private\"}"
        };
        Assert.ThrowsAny<Exception>(() => ServiceTranscriptProtocol.Parse(Encoding.UTF8.GetBytes(bytes)));
        Assert.Throws<InvalidDataException>(() => ServiceTranscriptProtocol.Parse(new byte[4097]));
    }

    private static ServiceTranscriptProtocol.Response Send(ServiceTranscriptProtocol protocol, ServiceTranscriptProtocol.Request request)
        => JsonSerializer.Deserialize<ServiceTranscriptProtocol.Response>(protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!;
}
