// Copyright (c) 2026 SysMonCmdPal
// GPU Adapter 枚举器 — 通过 DXGI COM interop 获取 GPU LUID + 名称映射
// D3DKMT 和 PDH reader 共用此映射

using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>GPU adapter 信息 (LUID + 名称)</summary>
internal sealed record GpuAdapterInfo(uint LuidLow, int LuidHigh, string Name, uint VendorId, bool IsSoftware);

/// <summary>通过 DXGI 枚举 GPU adapter (用户态，不需要管理员)</summary>
internal static class GpuAdapterEnumerator
{
    private static List<GpuAdapterInfo>? _cached;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static readonly object _lock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>枚举所有非软件 GPU adapter (缓存 30s)</summary>
    public static List<GpuAdapterInfo> GetAdapters()
    {
        lock (_lock)
        {
            if (_cached != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                return _cached;
        }

        var result = new List<GpuAdapterInfo>();
        try
        {
            var factory = CreateFactory();
            if (factory == null) return _cached ?? result;

            for (uint i = 0; i < 16; i++)
            {
                try
                {
                    factory.EnumAdapters1(i, out var adapter);
                    adapter.GetDesc1(out var desc);
                    string name = desc.Description.TrimEnd('\0');
                    bool isSoftware = (desc.Flags & 0x2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
                    if (!isSoftware && !string.IsNullOrEmpty(name))
                    {
                        result.Add(new GpuAdapterInfo(
                            desc.LuidLowPart, desc.LuidHighPart,
                            name, desc.VendorId, false));
                    }
                    Marshal.ReleaseComObject(adapter);
                }
                catch { break; } // No more adapters
            }
            Marshal.ReleaseComObject(factory);
        }
        catch { }

        lock (_lock)
        {
            _cached = result;
            _cacheTime = DateTime.UtcNow;
        }
        return result;
    }

    // ---- DXGI COM interop (minimal) ----

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private static IDXGIFactory1? CreateFactory()
    {
        try
        {
            var iid = IID_IDXGIFactory1;
            CreateDXGIFactory1(ref iid, out var factory);
            return factory;
        }
        catch { return null; }
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        void SetPrivateData(ref Guid n, uint sz, IntPtr d);
        void SetPrivateDataInterface(ref Guid n, IntPtr u);
        void GetPrivateData(ref Guid n, ref uint sz, IntPtr d);
        void GetParent(ref Guid riid, out IntPtr pp);
        void EnumAdapters(uint a, out IntPtr pp);
        void MakeWindowAssociation(IntPtr w, uint f);
        void GetWindowAssociation(out IntPtr w);
        void CreateSwapChain(IntPtr d, IntPtr desc, out IntPtr pp);
        void CreateSoftwareAdapter(IntPtr s, out IntPtr pp);
        void EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        void SetPrivateData(ref Guid n, uint sz, IntPtr d);
        void SetPrivateDataInterface(ref Guid n, IntPtr u);
        void GetPrivateData(ref Guid n, ref uint sz, IntPtr d);
        void GetParent(ref Guid riid, out IntPtr pp);
        void EnumOutputs(uint o, out IntPtr pp);
        void GetDesc(out IntPtr pDesc);
        int CheckInterfaceSupport(ref Guid n, out long v);
        void GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }
}
