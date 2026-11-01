using System;
using System.IO;
using System.Text.RegularExpressions;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>The person's own "start when I sign in" entry. It starts this
/// portable executable in the tray (<see cref="SignInStart.Argument"/>), like
/// Dropbox: Windows' Run key, a macOS LaunchAgent or a Linux XDG autostart
/// file. It needs no administrator rights and lives outside the portable
/// folder, so Backspace recreates it when it's missing or the copy moved.</summary>
public interface ISignInStart
{
    /// <summary>The executable the entry starts, or null when there is none.</summary>
    string? RegisteredExecutable();
    void Register(string executable);
    void Unregister();
}

public static class SignInStart
{
    public const string Argument = "--background";

    public static ISignInStart Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new WindowsRunKey();
#endif
        if (PlatformInfo.IsMacOS) return new MacLaunchAgent();
        return new XdgAutostart();
    }

    public static bool StartsThisCopy(ISignInStart entry, string executable)
        => entry.RegisteredExecutable() is { } registered && SamePath(registered, executable);

    /// <summary>At app start. One entry serves every copy: it is (re)written
    /// when missing, stale or already this copy's, but another existing copy
    /// keeps it unless the person turns the option on in this one
    /// (<paramref name="takeOver"/>). Development builds never add it on their own.</summary>
    public static void Apply(ISignInStart entry, bool wanted, string executable, bool takeOver, bool mayAdd = true)
    {
        var registered = entry.RegisteredExecutable();
        var ours = registered is not null && SamePath(registered, executable);
        if (!wanted)
        {
            if (ours) entry.Unregister();
            return;
        }
        if (ours || takeOver || mayAdd && (registered is null || !File.Exists(registered)))
            entry.Register(executable);
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsRunKey : ISignInStart
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "Backspace";

        public string? RegisteredExecutable()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue(Name) is not string command) return null;
            var match = Regex.Match(command, "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
            return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
        }

        public void Register(string executable)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(Name, "\"" + executable + "\" " + Argument);
        }

        public void Unregister()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true);
            key?.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
#endif

    private sealed class MacLaunchAgent : ISignInStart
    {
        private static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "io.github.backspace-backup.backspace.plist");

        public string? RegisteredExecutable()
        {
            if (!File.Exists(PlistPath)) return null;
            var match = Regex.Match(File.ReadAllText(PlistPath), "<key>ProgramArguments</key>\\s*<array>\\s*<string>([^<]+)</string>");
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        public void Register(string executable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            var exe = System.Security.SecurityElement.Escape(executable);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>io.github.backspace-backup.backspace</string>
                  <key>ProgramArguments</key>
                  <array>
                    <string>{exe}</string>
                    <string>{Argument}</string>
                  </array>
                  <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
        }

        public void Unregister() { if (File.Exists(PlistPath)) File.Delete(PlistPath); }
    }

    private sealed class XdgAutostart : ISignInStart
    {
        private static string DesktopPath
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(config, "autostart", "backspace.desktop");
            }
        }

        public string? RegisteredExecutable()
        {
            if (!File.Exists(DesktopPath)) return null;
            var match = Regex.Match(File.ReadAllText(DesktopPath), "^Exec=\"((?:[^\"\\\\]|\\\\.)+)\"", RegexOptions.Multiline);
            return match.Success ? Regex.Replace(match.Groups[1].Value, "\\\\(.)", "$1") : null;
        }

        public void Register(string executable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
            var quoted = Regex.Replace(executable, "([\"`$\\\\])", "\\$1");
            File.WriteAllText(DesktopPath,
                "[Desktop Entry]\nType=Application\nName=Backspace\nExec=\"" + quoted + "\" " + Argument + "\nX-GNOME-Autostart-enabled=true\n");
        }

        public void Unregister() { if (File.Exists(DesktopPath)) File.Delete(DesktopPath); }
    }
}
