using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class CloudDestinationPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cloud_folder_names_cannot_be_mistaken_for_provider_error_messages(bool includeStandardOutput)
    {
        if (!OperatingSystem.IsWindows()) return;
        var binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(binary);
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::Out.WriteLine('connection refused/'); [Console]::Error.WriteLine('503 Service Unavailable'); exit 1" })
            start.ArgumentList.Add(arg);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await StreamingRestoreProcess.RunAsync(start, null, deadline.Token,
            "cloud connection test", includeStandardOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.Contains("503 Service Unavailable", result.Output);
        if (includeStandardOutput) Assert.Contains("connection refused/", result.Output);
        else
        {
            Assert.DoesNotContain("connection refused/", result.Output);
            Assert.Null(CloudDestinationPreflight.FailureForDiagnostics(result.Output));
        }
    }

    [Theory]
    [InlineData("oauth2: cannot fetch token: 400 Bad Request Response: {\"error\":\"invalid_grant\"}")]
    [InlineData("oauth2: \"invalid_client\" \"client credentials rejected\"")]
    [InlineData("NOTICE: Failed to lsjson: Post \"https://api.dropboxapi.com/2/files/list_folder\": couldn't fetch token: invalid_grant: maybe token expired? - try refreshing with rclone config reconnect")]
    [InlineData("Failed to list: invalid_access_token/.")]
    [InlineData("Failed to list: 401 Unauthorized")]
    [InlineData("Failed to list: 403 Forbidden")]
    [InlineData("Failed to list: missing_scope/files.metadata.read")]
    public void Rejected_credentials_fail_with_a_sign_in_instruction(string diagnostic)
    {
        var failure = Assert.IsType<EngineAuthFailedException>(CloudDestinationPreflight.FailureForDiagnostics(diagnostic));
        Assert.Contains("connect your account again", failure.Message);
        Assert.Contains(diagnostic, failure.Message);
    }

    [Theory]
    [InlineData("directory not found")]
    [InlineData("object not found")]
    [InlineData("unexpected HTTP response (500): 500 Internal Server Error")]
    [InlineData("googleapi: Error 403: User rate limit exceeded., userRateLimitExceeded")]
    [InlineData("503 Service Unavailable")]
    public void New_repository_and_transient_server_errors_keep_the_normal_engine_path(string diagnostic)
        => Assert.Null(CloudDestinationPreflight.FailureForDiagnostics(diagnostic));

    [Fact]
    public void Refused_connection_is_unavailable_and_never_authentication_failure()
    {
        var failure = CloudDestinationPreflight.FailureForDiagnostics(
            "dial tcp 127.0.0.1:54781: connectex: No connection could be made because the target machine actively refused it.");
        Assert.IsType<EngineUnreachableDestinationException>(failure);
    }

    [Theory]
    [InlineData("private:", "private:")]
    [InlineData("private:folder with spaces/it's [mine]/", "private:")]
    public void Lists_only_the_configured_cloud_root_without_recursing_or_reading_file_contents(string remote, string expected)
    {
        var start = CloudDestinationPreflight.BuildStartInfo(@"C:\Program Files\Backspace\rclone.exe", remote, @"C:\private config\remote.conf");
        Assert.Equal("lsf", start.ArgumentList[0]);
        Assert.Equal(expected, start.ArgumentList[1]);
        Assert.Contains("--dirs-only", start.ArgumentList);
        Assert.Contains("--max-depth", start.ArgumentList);
        Assert.DoesNotContain("--recursive", start.ArgumentList);
        Assert.Equal(@"C:\private config\remote.conf", start.Environment["RCLONE_CONFIG"]);
        Assert.DoesNotContain(start.ArgumentList, arg => arg.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Error_details_do_not_reveal_access_refresh_or_id_tokens()
    {
        const string token = "{\"access_token\":\"private-access+123\",\"refresh_token\":\"private-refresh/456\",\"id_token\":\"private-identity.789\"}";
        var output = CloudDestinationPreflight.RedactCredentials(
            "oauth2: \"invalid_grant\" private-access+123 private-refresh%2F456 private-identity.789", token);
        Assert.Contains("invalid_grant", output);
        Assert.DoesNotContain("private-", output);
        Assert.Equal(3, output.Split("[REDACTED]").Length - 1);
    }
}
