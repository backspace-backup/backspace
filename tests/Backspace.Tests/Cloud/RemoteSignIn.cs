using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Backspace.Services.OAuth;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Cloud;

/// <summary>
/// Signs the three cloud test accounts in from another computer (U244).
/// Instead of opening a browser here, it writes each provider's sign-in
/// address to the file named by <c>BACKSPACE_TEST_REMOTE_SIGNIN</c> and waits
/// up to six hours. After signing in elsewhere, that browser lands on an
/// unreachable localhost address; opening the same address on this computer
/// (or requesting it, e.g. with curl) completes the sign-in here. The tokens
/// go to secrets.env as for any interactive sign-in.
/// Needs BACKSPACE_TEST_INTERACTIVE=1 as well.
/// </summary>
public class RemoteSignIn
{
    private readonly ITestOutputHelper _out;
    public RemoteSignIn(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Sign_in_the_cloud_test_accounts_from_another_computer()
    {
        var file = Environment.GetEnvironmentVariable("BACKSPACE_TEST_REMOTE_SIGNIN");
        if (string.IsNullOrWhiteSpace(file) || !InteractiveTokenAcquirer.CanAcquireInteractively)
            return; // Only when asked for: see the summary.
        File.WriteAllText(file, "");
        var gate = new object();
        var providers = new[] { CloudCredentialsLoader.Provider.Dropbox, CloudCredentialsLoader.Provider.OneDrive, CloudCredentialsLoader.Provider.GoogleDrive };
        var results = await Task.WhenAll(providers.Select(async provider =>
        {
            var flow = new OAuthFlowCoordinator(new HttpClient(), url => { lock (gate) File.AppendAllText(file, provider + " " + url + Environment.NewLine); })
            {
                TimeoutSeconds = 6 * 3600,
            };
            try { await InteractiveTokenAcquirer.AcquireAsync(provider, _out, default, flow); return provider + ": signed in"; }
            catch (Exception ex) { return provider + ": " + ex.Message; }
        }));
        foreach (var r in results) _out.WriteLine(r);
        Assert.All(results, r => Assert.EndsWith("signed in", r));
    }
}
