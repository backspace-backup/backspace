using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// What makes the schedule look again, from Windows itself (U185, owner, 12
/// November 2026: "make it event based ... rely on OS notifications as much as
/// possible for long waits"). The schedule otherwise sleeps until its next
/// due time, at most a day:
/// <list type="bullet">
/// <item>the power source changing (AC or battery), and the computer waking
/// from sleep (a timer doesn't count the time asleep the same way everywhere):
/// <c>PowerSettingRegisterNotification</c> and
/// <c>PowerRegisterSuspendResumeNotification</c>, callbacks that need no
/// window, so they work in the window and in the background service;</item>
/// <item>the network coming or going, or its cost (metered) changing:
/// <see cref="System.Net.NetworkInformation.NetworkChange"/> and Windows'
/// connectivity API;</item>
/// <item>the settings or the Quit state changing on disk (the background
/// service reads the window's settings file): a folder watch, which also
/// wakes the schedule if Windows reports it lost track of changes.</item>
/// </list>
/// A missed notification is caught by the daily look.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScheduleSignals
{
    /// <summary>Windows' answers to the two power registrations (0 is
    /// success), once started; for tests and diagnostics.</summary>
    internal static (uint AcDc, uint Resume)? PowerRegistration { get; private set; }

    /// <summary>Something the schedule depends on changed.</summary>
    public static event Action? Changed;

    /// <summary>A network appeared, went or changed (U226): destinations that
    /// couldn't be reached are tried again.</summary>
    public static event Action? NetworkChanged;

    private static readonly object Gate = new();
    private static bool _started;
    // Kept alive for as long as the process: Windows calls them.
    private static PowerCallback? _power;
    private static IntPtr _acDcHandle, _suspendHandle, _recipient;
    private static NetworkCostInterop.NetworkInformation? _cost;
    private static IDisposable? _costSubscription;
    private static FileSystemWatcher? _settings;

    /// <summary>Starts listening, once per process.</summary>
    /// <param name="settingsFolder">The folder holding the settings and the
    /// Quit state that the schedule reads.</param>
    public static void Start(string settingsFolder)
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }
        var log = AppLogger.For(nameof(ScheduleSignals));
        try
        {
            _power = OnPower;
            // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS: the callback and its context,
            // in memory that stays for as long as the process.
            _recipient = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(_recipient, Marshal.GetFunctionPointerForDelegate(_power));
            Marshal.WriteIntPtr(_recipient, IntPtr.Size, IntPtr.Zero);
            var acDc = AcDcPowerSource;
            var registered = PowerSettingRegisterNotification(ref acDc, DeviceNotifyCallback, _recipient, out _acDcHandle);
            var resume = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _recipient, out _suspendHandle);
            PowerRegistration = (registered, resume);
            ScheduleConditions.PowerEventsArrive = registered == 0;
            if (registered != 0 || resume != 0) log.Warning("Power notifications could not all be registered ({AcDc}, {Resume}); the daily look covers them", registered, resume);
        }
        catch (Exception ex) { log.Warning(ex, "Power notifications are unavailable; the daily look covers them"); }
        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => Raise("network availability", network: true);
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Raise("network address", network: true);
        }
        catch (Exception ex) { log.Warning(ex, "Network notifications are unavailable; the daily look covers them"); }
        try
        {
            _cost = new NetworkCostInterop.NetworkInformation();
            _costSubscription = _cost.SubscribeNetworkStatusChanged(() => Raise("network status", network: true));
        }
        catch (Exception ex) { log.Warning(ex, "Metered-connection notifications are unavailable; the daily look covers them"); }
        try
        {
            _settings = WatchSettings(settingsFolder, () => Raise("settings"));
        }
        catch (Exception ex) { log.Warning(ex, "The settings watch is unavailable; the daily look covers it"); }
    }

    /// <summary>Watches the settings file and the Quit state in this folder;
    /// null when the folder is missing.</summary>
    internal static FileSystemWatcher? WatchSettings(string folder, Action changed)
    {
        if (!Directory.Exists(folder)) return null;
        var watch = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        watch.Filters.Add("config.json");
        watch.Filters.Add("task-pause-*.json");
        watch.Changed += (_, _) => changed();
        watch.Created += (_, _) => changed();
        watch.Deleted += (_, _) => changed();
        watch.Renamed += (_, _) => changed();
        // Windows lost track (too many changes at once): look anyway.
        watch.Error += (_, _) => changed();
        watch.EnableRaisingEvents = true;
        return watch;
    }

    private static void Raise(string why, bool network = false)
    {
        try { if (network) NetworkChanged?.Invoke(); }
        catch (Exception ex) { AppLogger.For(nameof(ScheduleSignals)).Debug(ex, "A network listener failed after {Why}", why); }
        try { Changed?.Invoke(); }
        catch (Exception ex) { AppLogger.For(nameof(ScheduleSignals)).Debug(ex, "A schedule listener failed after {Why}", why); }
    }

    private static uint OnPower(IntPtr context, uint type, IntPtr setting)
    {
        // PBT_APMPOWERSTATUSCHANGE, PBT_POWERSETTINGCHANGE, PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND.
        if (type is 0x0A or 0x8013 or 0x12 or 0x07) Raise("power");
        return 0;
    }

    private const uint DeviceNotifyCallback = 2;
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, IntPtr recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr handle);
}
