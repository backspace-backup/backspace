using System;

namespace Backspace.Models;

/// <summary>The scope and outcome actually observed, independent of today's settings.</summary>
public sealed record BackupCheckResult
{
    public CheckDepth Depth { get; init; }
    public BackupRunStatus Status { get; init; }
    public DateTime EndedUtc { get; init; }
    public string TargetKey { get; init; } = "";
    public string DestinationName { get; init; } = "";
    public string Summary { get; init; } = "";
}

public enum CheckDepth
{
    Unknown,
    RepositoryStructure,
    SavedDataSample,
    RestoreSample,
    RestoreAllFiles,
    RestoreUnspecified,
}
