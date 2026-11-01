using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Direct calls into Windows' <c>Windows.Networking.Connectivity</c> API,
/// the same one the Windows SDK projection wraps. The projection is a
/// 24 MB assembly and this is its only use in Backspace, so the app targets
/// plain <c>net10.0-windows</c> and calls the handful of methods it needs
/// through their documented vtable slots (U032, 30 October 2026).
/// Interface IDs and slot numbers come from the Windows SDK metadata.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetworkCostInterop
{
    private static readonly Guid IID_INetworkInformationStatics = new("5074F851-950D-4165-9C15-365619481EEA");
    private static readonly Guid IID_IConnectionCost2 = new("8E113A05-E209-4549-BB25-5E0DB691CB05");
    private static readonly Guid IID_NetworkStatusChangedEventHandler = new("71BA143F-598E-49D0-84EB-8FEBAEDCC195");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IID_IMarshal = new("00000003-0000-0000-C000-000000000046");
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    // Slots after IUnknown (0-2) and IInspectable (3-5).
    private const int StaticsGetConnectionProfiles = 6, StaticsGetInternetConnectionProfile = 7,
        StaticsAddNetworkStatusChanged = 12, StaticsRemoveNetworkStatusChanged = 13;
    private const int ProfileGetProfileName = 6, ProfileGetNetworkConnectivityLevel = 7,
        ProfileGetConnectionCost = 9, ProfileGetNetworkAdapter = 11;
    private const int CostGetNetworkCostType = 6, CostGetRoaming = 7, CostGetOverDataLimit = 8;
    private const int Cost2GetBackgroundDataUsageRestricted = 6;
    private const int AdapterGetNetworkAdapterId = 10;
    private const int VectorGetAt = 6, VectorGetSize = 7;

    internal enum NetworkConnectivityLevel { None = 0, LocalAccess = 1, ConstrainedInternetAccess = 2, InternetAccess = 3 }
    internal enum NetworkCostType { Unknown = 0, Unrestricted = 1, Fixed = 2, Variable = 3 }

    internal sealed record ConnectionCost(NetworkCostType NetworkCostType, bool Roaming, bool OverDataLimit,
        bool BackgroundDataUsageRestricted);

    /// <summary>One connection profile. Dispose releases the Windows object.</summary>
    internal sealed class ConnectionProfile : IDisposable
    {
        private IntPtr _ptr;
        internal ConnectionProfile(IntPtr ptr) => _ptr = ptr;

        public string? ProfileName
        {
            get
            {
                Check(Call<GetPtrFn>(_ptr, ProfileGetProfileName)(_ptr, out var hstring));
                try { return ReadHString(hstring); }
                finally { if (hstring != IntPtr.Zero) WindowsDeleteString(hstring); }
            }
        }

        public Guid NetworkAdapterId
        {
            get
            {
                Check(Call<GetPtrFn>(_ptr, ProfileGetNetworkAdapter)(_ptr, out var adapter));
                if (adapter == IntPtr.Zero) return Guid.Empty;
                try { Check(Call<GetGuidFn>(adapter, AdapterGetNetworkAdapterId)(adapter, out var id)); return id; }
                finally { Marshal.Release(adapter); }
            }
        }

        public NetworkConnectivityLevel GetNetworkConnectivityLevel()
        {
            Check(Call<GetInt32Fn>(_ptr, ProfileGetNetworkConnectivityLevel)(_ptr, out var level));
            return (NetworkConnectivityLevel)level;
        }

        public ConnectionCost GetConnectionCost()
        {
            Check(Call<GetPtrFn>(_ptr, ProfileGetConnectionCost)(_ptr, out var cost));
            if (cost == IntPtr.Zero) throw new InvalidOperationException("Windows returned no connection cost.");
            try
            {
                Check(Call<GetInt32Fn>(cost, CostGetNetworkCostType)(cost, out var type));
                Check(Call<GetBooleanFn>(cost, CostGetRoaming)(cost, out var roaming));
                Check(Call<GetBooleanFn>(cost, CostGetOverDataLimit)(cost, out var over));
                var iid = IID_IConnectionCost2;
                Check(Marshal.QueryInterface(cost, in iid, out var cost2));
                try
                {
                    Check(Call<GetBooleanFn>(cost2, Cost2GetBackgroundDataUsageRestricted)(cost2, out var restricted));
                    return new ConnectionCost((NetworkCostType)type, roaming != 0, over != 0, restricted != 0);
                }
                finally { Marshal.Release(cost2); }
            }
            finally { Marshal.Release(cost); }
        }

        /// <summary>True when both refer to the same Windows object.</summary>
        public bool IsSameObject(ConnectionProfile other)
        {
            var iid = IID_IUnknown;
            var a = Marshal.QueryInterface(_ptr, in iid, out var ua);
            var b = Marshal.QueryInterface(other._ptr, in iid, out var ub);
            try { return a == 0 && b == 0 && ua == ub; }
            finally { if (ua != IntPtr.Zero) Marshal.Release(ua); if (ub != IntPtr.Zero) Marshal.Release(ub); }
        }

        public void Dispose()
        {
            var ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
    }

    /// <summary>The NetworkInformation statics. Dispose releases them.</summary>
    internal sealed class NetworkInformation : IDisposable
    {
        private IntPtr _statics;

        public NetworkInformation()
        {
            EnsureMultithreadedApartment();
            var name = CreateHString("Windows.Networking.Connectivity.NetworkInformation");
            try
            {
                var iid = IID_INetworkInformationStatics;
                Check(RoGetActivationFactory(name, ref iid, out _statics));
            }
            finally { WindowsDeleteString(name); }
        }

        public ConnectionProfile? GetInternetConnectionProfile()
        {
            Check(Call<GetPtrFn>(_statics, StaticsGetInternetConnectionProfile)(_statics, out var profile));
            return profile == IntPtr.Zero ? null : new ConnectionProfile(profile);
        }

        /// <summary>Every profile Windows reports. The caller disposes each.</summary>
        public List<ConnectionProfile?> GetConnectionProfiles()
        {
            Check(Call<GetPtrFn>(_statics, StaticsGetConnectionProfiles)(_statics, out var vector));
            var result = new List<ConnectionProfile?>();
            if (vector == IntPtr.Zero) return result;
            try
            {
                Check(Call<GetInt32Fn>(vector, VectorGetSize)(vector, out var size));
                for (uint i = 0; i < (uint)size; i++)
                {
                    Check(Call<GetAtFn>(vector, VectorGetAt)(vector, i, out var item));
                    result.Add(item == IntPtr.Zero ? null : new ConnectionProfile(item));
                }
                return result;
            }
            catch
            {
                foreach (var p in result) p?.Dispose();
                throw;
            }
            finally { Marshal.Release(vector); }
        }

        /// <summary>Subscribes to NetworkStatusChanged; dispose the result to unsubscribe.</summary>
        public IDisposable SubscribeNetworkStatusChanged(Action handler)
        {
            var callback = StatusChangedHandler.Create(handler);
            try
            {
                Check(Call<AddEventFn>(_statics, StaticsAddNetworkStatusChanged)(_statics, callback, out var token));
                return new Subscription(this, callback, token);
            }
            catch { Marshal.Release(callback); throw; }
        }

        private sealed class Subscription(NetworkInformation owner, IntPtr callback, long token) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { Check(Call<RemoveEventFn>(owner._statics, StaticsRemoveNetworkStatusChanged)(owner._statics, token)); }
                finally { Marshal.Release(callback); }
            }
        }

        public void Dispose()
        {
            var ptr = Interlocked.Exchange(ref _statics, IntPtr.Zero);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
    }

    /// <summary>
    /// A NetworkStatusChangedEventHandler that Windows can call: IUnknown,
    /// IAgileObject (any thread may call it) and the free-threaded marshaler,
    /// as the SDK projection's own handlers provide. Native layout is
    /// [vtable][GCHandle to this]; Windows' references keep it alive.
    /// </summary>
    internal sealed class StatusChangedHandler
    {
        private static readonly QueryInterfaceFn QueryInterfaceThunk = QueryInterface;
        private static readonly RefCountFn AddRefThunk = AddRef;
        private static readonly RefCountFn ReleaseThunk = Release;
        private static readonly InvokeFn InvokeThunk = Invoke;
        private static readonly IntPtr Vtable = BuildVtable();

        private readonly Action _handler;
        private IntPtr _native;
        private IntPtr _marshaler;
        private GCHandle _self;
        private int _refs = 1;
        private static int _live;

        /// <summary>Handlers Windows has not yet released; 0 once every subscription is disposed.</summary>
        internal static int Live => Volatile.Read(ref _live);

        private StatusChangedHandler(Action handler) => _handler = handler;

        internal static IntPtr Create(Action handler)
        {
            var instance = new StatusChangedHandler(handler);
            Interlocked.Increment(ref _live);
            instance._self = GCHandle.Alloc(instance);
            instance._native = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(instance._native, Vtable);
            Marshal.WriteIntPtr(instance._native, IntPtr.Size, GCHandle.ToIntPtr(instance._self));
            var hr = CoCreateFreeThreadedMarshaler(instance._native, out instance._marshaler);
            if (hr < 0) { Release(instance._native); Marshal.ThrowExceptionForHR(hr); }
            return instance._native;
        }

        private static IntPtr BuildVtable()
        {
            var table = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(QueryInterfaceThunk));
            Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddRefThunk));
            Marshal.WriteIntPtr(table, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseThunk));
            Marshal.WriteIntPtr(table, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(InvokeThunk));
            return table;
        }

        private static StatusChangedHandler From(IntPtr native) =>
            (StatusChangedHandler)GCHandle.FromIntPtr(Marshal.ReadIntPtr(native, IntPtr.Size)).Target!;

        private static int QueryInterface(IntPtr native, ref Guid iid, out IntPtr result)
        {
            if (iid == IID_IUnknown || iid == IID_IAgileObject || iid == IID_NetworkStatusChangedEventHandler)
            {
                AddRef(native);
                result = native;
                return 0;
            }
            var self = From(native);
            if (iid == IID_IMarshal && self._marshaler != IntPtr.Zero) return Marshal.QueryInterface(self._marshaler, in iid, out result);
            result = IntPtr.Zero;
            return E_NOINTERFACE;
        }

        private static uint AddRef(IntPtr native) => (uint)Interlocked.Increment(ref From(native)._refs);

        private static uint Release(IntPtr native)
        {
            var self = From(native);
            var left = Interlocked.Decrement(ref self._refs);
            if (left == 0)
            {
                // The marshaler is aggregated into this object; releasing it
                // last is the documented teardown order.
                if (self._marshaler != IntPtr.Zero) Marshal.Release(self._marshaler);
                self._self.Free();
                Marshal.FreeHGlobal(self._native);
                Interlocked.Decrement(ref _live);
            }
            return (uint)left;
        }

        private static int Invoke(IntPtr native, IntPtr sender)
        {
            // Exceptions must never cross back into Windows.
            try { From(native)._handler(); } catch { }
            return 0;
        }
    }

    private static T Call<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    private static IntPtr CreateHString(string value)
    {
        Check(WindowsCreateString(value, (uint)value.Length, out var hstring));
        return hstring;
    }

    private static string? ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buffer, (int)length);
    }

    // A process-wide multithreaded apartment lets pool threads call these
    // objects without initializing COM themselves, as the projection does.
    private static int _mtaCookieTaken;
    private static void EnsureMultithreadedApartment()
    {
        if (Interlocked.Exchange(ref _mtaCookieTaken, 1) == 0) _ = CoIncrementMTAUsage(out _);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtrFn(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt32Fn(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBooleanFn(IntPtr self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuidFn(IntPtr self, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAtFn(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddEventFn(IntPtr self, IntPtr handler, out long token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RemoveEventFn(IntPtr self, long token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefCountFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InvokeFn(IntPtr self, IntPtr sender);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateFreeThreadedMarshaler(IntPtr outer, out IntPtr marshaler);
}
