// Copyright (c) 2026 SysMonCmdPal
// GPU Adapter 枚举器 — 通过 DXGI COM interop 获取 GPU LUID + 名称映射
// D3DKMT 和 PDH reader 共用此映射

using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>
/// GPU adapter 信息 (LUID + 名称 + 厂商/设备 id + 独立显存字节数)。
/// </summary>
/// <param name="DedicatedVideoMemoryBytes">
/// DXGI 报告的独立显存（<c>DXGI_ADAPTER_DESC1.DedicatedVideoMemory</c>，偏移 126 处已 marshal，
/// 此前被 record 丢弃）。T2-3 把它作为 <b>辅助</b>独显信号交给 GpuIdentityService；
/// T2-2（t5）复用**同一个** adapter 枚举出口读取 QueryVideoMemoryInfo，不得另建第二条 DXGI
/// P/Invoke 链。注意：出货 trim 产物禁用 Built-in COM，整个枚举器会返回空表
/// ⇒ 任何判据都不得硬依赖该字段（GpuIdentityService 仅当其为可选佐证）。
/// </param>
internal sealed record GpuAdapterInfo(
    uint LuidLow, int LuidHigh, string Name, uint VendorId, bool IsSoftware,
    uint DeviceId, ulong DedicatedVideoMemoryBytes);

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
        int softwareSkipped = 0;
        int emptyNameSkipped = 0;

        try
        {
            var factory = CreateFactory();
            if (factory == null)
            {
                // 原行为保持：直接返回上次缓存（可能为 null → 空表），**不**覆写 _cached/_cacheTime，
                // 因此下一个刷新周期仍会重试建工厂。此处只补日志。
                lock (_lock)
                {
                    LogState($"adapters=n/a (factory unavailable); returning cached={_cached?.Count ?? -1}");
                    return _cached ?? result;
                }
            }
            else
            {
                for (uint i = 0; i < 16; i++)
                {
                    try
                    {
                        factory.EnumAdapters1(i, out var adapter);
                        adapter.GetDesc1(out var desc);
                        string name = desc.Description.TrimEnd('\0');
                        bool isSoftware = (desc.Flags & 0x2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
                        if (isSoftware) softwareSkipped++;
                        else if (string.IsNullOrEmpty(name)) emptyNameSkipped++;
                        else
                        {
                            result.Add(new GpuAdapterInfo(
                                desc.LuidLowPart, desc.LuidHighPart,
                                name, desc.VendorId, false,
                                desc.DeviceId, (ulong)desc.DedicatedVideoMemory));
                        }
                        Marshal.ReleaseComObject(adapter);
                    }
                    catch (Exception ex)
                    {
                        // 原为裸 catch { break; }：把「枚举正常结束」与「中途抛异常」
                        // 混成同一种结果，且与「工厂不可用」无法区分。
                        LogState($"EnumAdapters1[{i}] stopped: {ex.GetType().Name} hr=0x{Hr(ex):X8}");
                        break; // No more adapters (or enumeration failed)
                    }
                }
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            // 原为裸 catch { }
            LogState($"enumeration threw {ex.GetType().Name} hr=0x{Hr(ex):X8}");
        }

        lock (_lock)
        {
            _cached = result;
            _cacheTime = DateTime.UtcNow;
        }

        // 出口计数指纹（T2-1 必做项）：此前「DXGI 返回 0 个」与「返回 N 个但全被筛掉」
        // 在日志里长得一模一样，永远无法区分。
        string names = result.Count == 0 ? "-" : string.Join(
            ", ", result.ConvertAll(a => $"{a.Name}[0x{a.VendorId:X4}:0x{a.DeviceId:X4}:{a.DedicatedVideoMemoryBytes / (1024 * 1024)}MB]"));
        LogState($"adapters={result.Count}: {names}" +
                 $" softwareSkipped={softwareSkipped} emptyNameSkipped={emptyNameSkipped}");
        return result;
    }

    // Exception.HResult 是所有异常都有的普通属性，零 COM interop 依赖
    // （不用 Marshal.GetHRForException —— 它走 interop 层，正是本任务要避开的开关面）。
    private static int Hr(Exception ex) => ex.HResult;

    /// <summary>仅状态跃变时打点（本方法在 1s 刷新链上会被反复调用，避免刷爆轮转日志）。</summary>
    private static void LogState(string message)
    {
        lock (_lock)
        {
            if (string.Equals(_lastFingerprint, message, StringComparison.Ordinal)) return;
            _lastFingerprint = message;
        }
        SensorLogger.ForceLog($"[GPU-DXGI] {message}");
    }

    private static string _lastFingerprint = "";

    // ---- DXGI COM interop (minimal) ----

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private static IDXGIFactory1? CreateFactory()
    {
        try
        {
            var iid = IID_IDXGIFactory1;
            int hr = CreateDXGIFactory1(ref iid, out var factory);
            if (hr != 0)
            {
                LogState($"CreateDXGIFactory1 hr=0x{hr:X8}");
                return null;
            }
            return factory;
        }
        catch (Exception ex)
        {
            // 原为裸 catch { return null; } —— trim 宿主下 built-in COM marshalling 被裁，
            // 这里会抛 TypeLoadException/NotSupportedException；不打出来就与「枚举为空」同形。
            LogState($"CreateDXGIFactory1 threw {ex.GetType().Name} hr=0x{Hr(ex):X8}");
            return null;
        }
    }

    // ---- vtable 槽位对照（t12 附带发现的静态确证，裁判 = Windows SDK 10.0.26100 shared/dxgi.h）----
    //   IDXGIFactory1 : 0 QueryInterface 1 AddRef 2 Release 3 SetPrivateData 4 SetPrivateDataInterface
    //                   5 GetPrivateData 6 GetParent 7 EnumAdapters 8 MakeWindowAssociation
    //                   9 GetWindowAssociation 10 CreateSwapChain 11 CreateSoftwareAdapter
    //                   12 EnumAdapters1 13 IsCurrent
    //   IDXGIAdapter1 : 0..6 同上(IUnknown+IDevice) 7 EnumOutputs 8 GetDesc
    //                   9 CheckInterfaceSupport 10 GetDesc1
    // 下面两个 [ComImport] 声明与 SDK 权威 Vtbl **逐槽一致**（CreateSoftwareAdapter@11 是真实成员，
    // EnumAdapters1@12），故 t12 报告的「EnumAdapters1 槽位错位」疑点对本仓声明不成立，未改声明。
    // 活体侧证（本机 unverifiable）：本机为无头/vGPU 会话，连已知可用的经典 COM 路径也 0 adapter，
    // 因此槽位正确性只能静态确证；真机（物理 dGPU + 交互式桌面）复测列 t5/t7。

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
