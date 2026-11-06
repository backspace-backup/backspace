using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceConfigurationComparisonTests
{
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
