using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U171: with administrator access on, the background service runs
/// every backup, and it reaches a network share only with the share's own user
/// name and password. The destination editor asks for them when the share is
/// saved, so the "service can't reach it" case is settled before any run.
/// Without the service they stay optional: the window uses the person's own
/// Windows sign-in.</summary>
public sealed class DestinationEditorShareSignInTests
{
    private static DestinationEditorViewModel Share(string? user, string password) => new(new Destination(), isNew: true)
    {
        Name = "NAS " + System.Guid.NewGuid().ToString("N")[..6],
        Kind = DestinationKind.NetworkShare,
        PathOrSubpath = @"\\nas\backups\pc",
        NetworkUsername = user,
        NetworkPasswordInput = password,
    };

    [AvaloniaFact]
    public void With_the_service_set_up_a_share_needs_its_user_name_and_password()
    {
        DestinationEditorViewModel.BackgroundServiceActiveOverride = () => true;
        try
        {
            using var noSignIn = Share(null, "");
            Assert.Equal("USERNAME", noSignIn.NetworkUsernameLabel);
            Assert.Null(noSignIn.Save());
            Assert.Equal("Enter the share's user name and password. With administrator access on, backups run in the background service, which signs in to the share with them.",
                noSignIn.NetworkSignInError);

            using var noPassword = Share("backup", "");
            Assert.Null(noPassword.Save());
            Assert.NotNull(noPassword.NetworkSignInError);

            using var complete = Share("backup", "s3cret");
            var saved = complete.Save();
            Assert.NotNull(saved);
            Assert.Null(complete.NetworkSignInError);
            Assert.Equal("backup", saved!.NetworkUsername);
            Assert.False(string.IsNullOrEmpty(saved.NetworkPasswordRef));
        }
        finally { DestinationEditorViewModel.BackgroundServiceActiveOverride = null; }
    }

    [AvaloniaFact]
    public void Without_the_service_the_sign_in_stays_optional()
    {
        DestinationEditorViewModel.BackgroundServiceActiveOverride = () => false;
        try
        {
            using var noSignIn = Share(null, "");
            Assert.Equal("USERNAME (OPTIONAL)", noSignIn.NetworkUsernameLabel);
            Assert.NotNull(noSignIn.Save());
            Assert.Null(noSignIn.NetworkSignInError);
        }
        finally { DestinationEditorViewModel.BackgroundServiceActiveOverride = null; }
    }
}
