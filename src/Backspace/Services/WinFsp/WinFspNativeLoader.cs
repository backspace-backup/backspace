using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace Backspace.Services.WinFsp;

/// <summary>
/// Extracts embedded WinFSP assets from <c>Backspace.exe</c> to a
/// user-writable folder, loads the managed wrapper from disk, and
/// pre-loads the native DLL before WinFsp.Net's static constructor
/// runs. End result: Mount works from a single, portable exe.
///
/// <para><b>Why both an embedded resource AND a runtime extract.</b>
/// WinFsp.Net's <c>Fsp.Interop.Api..cctor()</c> calls
/// <c>FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location)</c>.
/// In a <c>PublishSingleFile</c> bundle, <c>Assembly.Location</c>
/// returns the empty string (.NET IL3000), so the version lookup
/// throws <c>ArgumentException: The path is empty</c>. The only
/// way to get a non-empty <c>Location</c> is to load the assembly
/// from an actual on-disk file. We embed the DLL inside
/// Backspace.exe (so the dist stays a single portable exe), then
/// at startup we extract it to
/// <c>Backspace.config\winfsp\winfsp-msil.dll</c> and
/// <c>Assembly.LoadFrom</c> that path. The wrapper now has a real
/// Location, and the cctor passes.</para>
///
/// <para>The native runtime DLL (<c>winfsp-x64.dll</c>) is loaded
/// from the installed WinFSP runtime, normally under
/// <c>C:\Program Files (x86)\WinFsp\bin\</c>, by
/// absolute path so the cctor's own <c>LoadLibrary</c> finds it.</para>
///
/// <para>The WinFSP MSI installer (<c>winfsp.msi</c>) is also
/// embedded and extracted; the MountedSnapshotRegistry passes the
/// extracted path to <c>msiexec /i</c> on first-Mount-on-clean-
/// machine.</para>
///
/// <para><b>Call <see cref="EnsureRegistered"/> exactly once at
/// startup, BEFORE any code references a <c>Fsp.*</c> type.</b>
/// Wired into <c>Program.Main</c> and
/// <c>App.OnFrameworkInitializationCompleted</c>; also re-called
/// from <c>MountedSnapshotRegistry.MountForAsync</c> as a post-
/// install retry after a clean-machine WinFSP install.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinFspNativeLoader
{
    private static ILogger _log => AppLogger.For<WinFspMountCoordinator>();

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS    = 0x00000400;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string NewDirectory);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    private static int _registered;

    /// <summary>The folder embedded assets get extracted to. Public
    /// so <see cref="WinFspInstaller"/> can find the extracted MSI
    /// without re-implementing the path logic.</summary>
    public static string ExtractRoot => Path.Combine(AppPaths.ConfigRoot, "winfsp");

    /// <summary>
    /// Idempotent. Extracts embedded WinFSP assets to disk if not
    /// already there, loads winfsp-msil.dll from the extracted
    /// path, and pre-loads the native runtime DLL. Returns true on
    /// success, false on a recoverable failure (callers route
    /// through the bundled installer or surface a friendly toast).
    /// </summary>
    public static bool EnsureRegistered()
    {
        SentinelWrite("EnsureRegistered called");

        if (!OperatingSystem.IsWindows()) { SentinelWrite("not windows"); return false; }

        // Idempotent guard. Subsequent calls just return success; the
        // one-time work happened on the first call.
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0)
        {
            SentinelWrite("already registered (sentinel != 0)");
            return true;
        }

        try
        {
            Directory.CreateDirectory(ExtractRoot);

            // Step 1: extract the managed wrapper from our embedded
            // resource and Assembly.LoadFrom the extracted path.
            // This is THE step that fixes the
            // "FileVersionInfo.GetVersionInfo('')" cctor crash.
            var msilPath = Path.Combine(ExtractRoot, "winfsp-msil.dll");
            ExtractEmbeddedResource("winfsp-msil.dll", msilPath);

            // Reflect-call so this file doesn't statically reference
            // winfsp-msil — which would force the bundled copy to
            // load first, defeating the whole point.
            var loadFromMethod = typeof(Assembly).GetMethod("LoadFrom", new[] { typeof(string) });
            var asm = loadFromMethod!.Invoke(null, new object[] { msilPath }) as Assembly;
            SentinelWrite($"Assembly.LoadFrom({msilPath}) OK: Location={asm?.Location}");

            // Step 2: extract the bundled WinFSP MSI installer (used
            // by WinFspInstaller on first-Mount-on-clean-machine).
            // Best-effort — the install flow surfaces "missing
            // bundle" cleanly if the resource is absent.
            try { ExtractEmbeddedResource("winfsp.msi", Path.Combine(ExtractRoot, "winfsp.msi")); }
            catch (Exception msiEx) { SentinelWrite($"MSI extract WARN: {msiEx.GetType().Name}: {msiEx.Message}"); }

            // Step 3: pre-load the native runtime DLL by absolute
            // path so WinFsp.Net's own LoadLibrary call inside the
            // cctor finds it. We source it from the WinFSP install
            // dir; if the runtime isn't installed yet, we skip this
            // step — the MountedSnapshotRegistry re-runs
            // EnsureRegistered after the bundled installer
            // completes, at which point this step succeeds.
            var dir = WinFspRuntimeDetector.GetInstallDir();
            if (!string.IsNullOrEmpty(dir))
            {
                var binDir = Path.Combine(dir, "bin");
                var nativeDllPath = Path.Combine(binDir, "winfsp-x64.dll");
                if (File.Exists(nativeDllPath))
                {
                    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
                    AddDllDirectory(binDir);
                    var handle = NativeLibrary.Load(nativeDllPath);
                    SentinelWrite($"NativeLibrary.Load native runtime → 0x{handle.ToInt64():X}");
                    var shortHandle = GetModuleHandleW("winfsp-x64.dll");
                    SentinelWrite($"GetModuleHandleW('winfsp-x64.dll') → 0x{shortHandle.ToInt64():X}");
                }
                else
                {
                    SentinelWrite($"native DLL not at {nativeDllPath} — runtime not installed yet");
                }
            }
            else
            {
                SentinelWrite("WinFSP runtime not installed yet — will retry after MSI install");
            }

            // Step 4: verify the cctor passes by forcing it via
            // reflection. If something is wrong, we get the
            // diagnostic NOW (sentinel + app log) instead of at
            // first Mount click in production.
            try
            {
                var apiType = Type.GetType("Fsp.Interop.Api, winfsp-msil", throwOnError: false);
                if (apiType is null)
                {
                    SentinelWrite("WARN: Fsp.Interop.Api type not yet resolvable (loaded later)");
                }
                else
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(apiType.TypeHandle);
                    SentinelWrite("Fsp.Interop.Api cctor OK");
                }
            }
            catch (Exception cctorEx)
            {
                SentinelWrite($"Fsp.Interop.Api cctor THREW: {cctorEx.GetType().Name}: {cctorEx.Message}");
                for (var ie = cctorEx.InnerException; ie != null; ie = ie.InnerException)
                    SentinelWrite($"  inner: {ie.GetType().Name}: {ie.Message}");
                Volatile.Write(ref _registered, 0);
                return false;
            }

            _log.Information("WinFSP assets extracted to {ExtractRoot}", ExtractRoot);
            return true;
        }
        catch (Exception ex)
        {
            SentinelWrite($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _log.Warning(ex, "EnsureRegistered failed");
            Volatile.Write(ref _registered, 0);
            return false;
        }
    }

    /// <summary>Copy an embedded resource out of the running
    /// executable to a path on disk. Idempotent — skips when the
    /// target already contains the exact embedded bytes.</summary>
    internal static void ExtractEmbeddedResource(string resourceName, string targetPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{resourceName}' not found in {asm.GetName().Name}.dll. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        var written = EmbeddedAssetCache.WriteIfDifferent(stream, targetPath);
        SentinelWrite($"{(written ? "extracted" : "already current")}: {targetPath} ({stream.Length} bytes)");
    }

    /// <summary>Append a line to %TEMP%\Backspace-winfsp-loader.log.
    /// Best-effort; throws nothing. Diagnostic tracing for the
    /// pre-AppLogger startup window.</summary>
    private static void SentinelWrite(string line)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "Backspace-winfsp-loader.log");
            File.AppendAllText(path, $"{DateTime.UtcNow:HH:mm:ss.fff} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { /* never let diagnostics break startup */ }
    }
}
