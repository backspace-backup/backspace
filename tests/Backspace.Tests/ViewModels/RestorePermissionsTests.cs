using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U217 (owner go-ahead, 13 November 2026): restores run as you; a
/// "Keep the files' permissions" choice, ticked only for the original place on
/// the computer that made the backup; a folder you can't write to is said
/// before anything starts.</summary>
public sealed class RestorePermissionsTests
{
    [Fact]
    public void Permissions_are_kept_by_default_only_back_in_place_on_the_same_computer()
    {
        Assert.True(RestoreViewModel.KeepPermissionsByDefault(originalPlace: true, Environment.MachineName));
        Assert.True(RestoreViewModel.KeepPermissionsByDefault(originalPlace: true, Environment.MachineName.ToLowerInvariant()));
        Assert.True(RestoreViewModel.KeepPermissionsByDefault(originalPlace: true, null));
        Assert.False(RestoreViewModel.KeepPermissionsByDefault(originalPlace: true, "OLD-LAPTOP"));
        Assert.False(RestoreViewModel.KeepPermissionsByDefault(originalPlace: false, Environment.MachineName));
    }

    [AvaloniaFact]
    public void The_choice_follows_the_target_until_the_person_picks()
    {
        using var vm = new RestoreViewModel();
        vm.SelectedRevision = new RevisionSummary(3, DateTime.UtcNow, 10, 100) { Hostname = "OLD-LAPTOP" };
        vm.TargetOriginalSource = true;
        Assert.False(vm.KeepPermissions); // another computer
        vm.SelectedRevision = new RevisionSummary(4, DateTime.UtcNow, 10, 100) { Hostname = Environment.MachineName };
        Assert.True(vm.KeepPermissions);
        vm.TargetOriginalSource = false;
        Assert.False(vm.KeepPermissions); // another folder
        vm.KeepPermissions = true; // the person's choice stays
        vm.TargetOriginalSource = true;
        vm.TargetOriginalSource = false;
        Assert.True(vm.KeepPermissions);
    }

    [Fact]
    public void A_folder_you_cannot_write_to_is_known_before_starting()
    {
        var mine = Path.Combine(Path.GetTempPath(), "backspace-u217-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mine);
        try
        {
            Assert.True(RestoreViewModel.CanWriteInto(mine));
            Assert.True(RestoreViewModel.CanWriteInto(Path.Combine(mine, "not", "yet", "there")));
            if (OperatingSystem.IsWindows() && !new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                Assert.False(RestoreViewModel.CanWriteInto(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        finally { Directory.Delete(mine, true); }
    }
}
