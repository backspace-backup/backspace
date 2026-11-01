using System;
using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceConfigurationComparisonTests
{
    private static AppConfig Example() => new() { Backups = new() { new() { Name = "Photos", SourcePaths = new() { @"C:\Photos" }, Targets = new() { new() { DestinationId = "disk" } } } }, Destinations = new() { new() { Id = "disk", PathOrSubpath = @"C:\Backups" } } };
    private static AppConfig Copy(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, ServiceRunAuthorization.Json), ServiceRunAuthorization.Json)!;

    [Fact]
    public void Presentation_and_completed_results_do_not_require_reapproval_or_change_the_inputs()
    {
        var approved = Example(); var current = Copy(approved);
        current.Preferences.Theme = AppTheme.Dark;
        current.Preferences.LastUpdateCheckUtc = DateTime.UtcNow;
        current.Backups[0].LastRunStatus = BackupRunStatus.Failed;
        current.Backups[0].LastRunStartUtc = DateTime.UtcNow;
        current.Backups[0].LastRunSummary = "failure";
        current.Backups[0].SourceLastSizeBytes["photos"] = 100;
        current.Backups[0].LastVerifyPass = false;
        current.Destinations[0].LastTokenValidatedUtc = DateTime.UtcNow;
        var before = JsonSerializer.Serialize(current, ServiceRunAuthorization.Json);
        Assert.True(ServiceConfigurationComparison.Equivalent(approved, current));
        Assert.Equal(before, JsonSerializer.Serialize(current, ServiceRunAuthorization.Json));
        Assert.Equal(BackupRunStatus.NeverRun, approved.Backups[0].LastRunStatus);
    }

    public static IEnumerable<object[]> SignificantChanges()
    {
        yield return new object[] { (Action<AppConfig>)(c => c.Backups.Clear()) };
        yield return new object[] { (Action<AppConfig>)(c => c.Backups[0].Enabled = false) };
        yield return new object[] { (Action<AppConfig>)(c => c.Backups[0].SourcePaths.Add(@"C:\Other")) };
        yield return new object[] { (Action<AppConfig>)(c => c.Destinations[0].PathOrSubpath = @"C:\Elsewhere") };
        yield return new object[] { (Action<AppConfig>)(c => c.Backups[0].Schedule.TimeOfDay = TimeSpan.FromHours(3)) };
        yield return new object[] { (Action<AppConfig>)(c => c.Backups[0].FiltersText = "*.tmp") };
        yield return new object[] { (Action<AppConfig>)(c => c.Backups[0].SkipNotificationGraceResetUtc = DateTime.UtcNow) };
        yield return new object[] { (Action<AppConfig>)(c => c.Logging.RetentionDays++) };
        yield return new object[] { (Action<AppConfig>)(c => c.Preferences.DataRootOverride = @"C:\Other") };
    }

    [Theory, MemberData(nameof(SignificantChanges))]
    public void Execution_changes_require_explicit_approval(Action<AppConfig> change)
    {
        var approved = Example(); var current = Copy(approved); change(current);
        Assert.False(ServiceConfigurationComparison.Equivalent(approved, current));
    }

    /// <summary>Settings saved with a UTF-8 byte order mark (by a text editor)
    /// load in Backspace, so the service must read them too.</summary>
    [Fact]
    public void Settings_saved_with_a_byte_order_mark_are_read()
    {
        using var ws = new Backspace.Tests.TestHelpers.TempWorkspace();
        var folder = System.IO.Directory.CreateDirectory(ServicePortableLayout.Config(ws.Root)).FullName;
        var config = new AppConfig(); config.Backups.Add(new Backup { Id = "b", Name = "with-bom" });
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config, ServiceRunAuthorization.Json);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "config.json"), System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new byte[] { 0xEF, 0xBB, 0xBF }, json)));
        Assert.Equal("with-bom", Assert.Single(ServiceConfigurationComparison.ReadDesktop(ws.Root).Backups).Name);
    }
}
