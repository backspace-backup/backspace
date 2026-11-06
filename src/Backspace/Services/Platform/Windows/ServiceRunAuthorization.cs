using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>The background service's strict JSON rules and the run it was
/// asked for. The desktop supplies only a backup ID and an operation, never
/// configuration bytes, a command line, a working directory or a restore
/// target; the service reads the saved settings itself (SystemServiceRuns).</summary>
internal static class ServiceRunAuthorization
{
    internal const int MaximumApprovalBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal sealed record AuthorizedRun(string Revision, Backup Backup, AppConfig Configuration, RunKind Kind);

    internal static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new System.Collections.Generic.Stack<System.Collections.Generic.HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate JSON properties are not permitted in service requests or approvals.");
        }
    }
}
