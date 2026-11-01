using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class OperationDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsFileErrorsEvenWhenEngineLogIsUnavailable(bool failLog)
    {
        using var handler = new LogHandler(failLog);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        using var errors = JsonDocument.Parse("{\"errors\":[{\"item\":\"/online.tar.gz\",\"error\":\"unsupported file type irregular\"}]}");
        var operation = new BackrestOperation { Id = "42", Logref = "fixture-log", OperationBackup = errors.RootElement.Clone(), Status = "STATUS_WARNING" };
        var engine = new ResticBackrestEngine(new BackrestSidecar("/missing/backrest", "/missing/restic", "/missing/data"), new SecretsStore());
        var output = new List<string>();
        await engine.CaptureOperationDiagnosticsAsync(new BackrestClient(http), operation, output.Add);
        Assert.Contains(output, line => line.Contains("online.tar.gz") && line.Contains("irregular"));
        Assert.Equal("STATUS_WARNING", operation.Status);
        Assert.Equal(1, handler.Requests);
        if (failLog)
            Assert.Contains(output, line => line.Contains("could not be retrieved"));
        else
        {
            Assert.Contains("café: original engine message", output);
            Assert.DoesNotContain(output, line => line.Contains('\uFFFD'));
        }
    }

    private sealed class LogHandler(bool fail) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("/v1.Backrest/GetLogs", request.RequestUri!.AbsolutePath);
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var stream = new MemoryStream();
            // Split a multibyte character between actual Connect stream records.
            var bytes = Encoding.UTF8.GetBytes("café: original engine message\n");
            foreach (var chunk in new[] { bytes[..4], bytes[4..] })
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { value = Convert.ToBase64String(chunk) });
                var header = new byte[5];
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)payload.Length);
                stream.Write(header); stream.Write(payload);
            }
            stream.Write(new byte[] { 2, 0, 0, 0, 2, (byte)'{', (byte)'}' });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }
}
