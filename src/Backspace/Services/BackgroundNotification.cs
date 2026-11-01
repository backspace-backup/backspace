using System;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>A desktop alert, without executable callbacks or file paths.</summary>
internal sealed record BackgroundNotification(string Id, DateTime CreatedUtc, string Title, string Detail,
    NotificationSeverity Severity, bool Toast, bool Sound, string? BackupId, string? RunId);
