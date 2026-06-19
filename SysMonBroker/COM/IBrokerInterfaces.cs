// SysMonBroker/COM/IBrokerInterfaces.cs
// COM 接口定义 — Broker (C#) 和 btop4win (C++) 共享的契约
// 修改时必须同时更新两端！
//
// CLSID/GUID 约定:
//   BrokerService:     {7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C3}
//   IBrokerService:    {7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C4}
//   IBrokerProcessService: {7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C5}
//   IBrokerSensorService:  {7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C6}

using System.Runtime.InteropServices;

namespace SysMonBroker.COM;

// ---- GUIDs (C++ 侧用 DEFINE_GUID 匹配) ----

public static class BrokerGuids
{
    public const string BrokerServiceClsid = "7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C3";
    public const string IBrokerServiceIid = "7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C4";
    public const string IBrokerProcessServiceIid = "7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C5";
    public const string IBrokerSensorServiceIid = "7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C6";
}

// ---- COM 结构体 (BSTR 字段, 跨 COM 边界安全) ----

/// <summary>进程信息条目 — 对应 btop4win 的 proc_info 核心字段</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BrokerProcessEntry
{
    public uint Pid;
    public uint ParentPid;
    public uint Threads;

    [MarshalAs(UnmanagedType.BStr)]
    public string Name;           // 可执行文件名 (e.g. "chrome.exe")

    [MarshalAs(UnmanagedType.BStr)]
    public string CommandLine;    // 完整命令行

    [MarshalAs(UnmanagedType.BStr)]
    public string UserName;       // 进程所有者

    public long PrivateMemoryBytes;   // 私有内存 (Working Set Private)
    public double CpuPercent;         // CPU% (delta since last query)
    public long CreationTime;         // FILETIME (100ns intervals since 1601)
    public long KernelTime;           // 累计内核时间 (100ns)
    public long UserTime;             // 累计用户时间 (100ns)
    public long IoReadBytes;          // 累计 IO 读取字节
    public long IoWriteBytes;         // 累计 IO 写入字节
}

/// <summary>传感器信息条目 — 对应 Broker 的 SensorEntry</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BrokerSensorEntry
{
    public int CategoryTag;       // 0=CpuTemp, 1=CpuLoad, ..., 16=StorageLoad

    [MarshalAs(UnmanagedType.BStr)]
    public string Name;           // 传感器名称

    public double Value;          // 当前值

    [MarshalAs(UnmanagedType.BStr)]
    public string Unit;           // 单位 (°C, %, MHz, W, V, MB, RPM)
}

// ---- COM 接口 ----

/// <summary>Broker 主服务接口 — 入口点</summary>
[ComVisible(true)]
[Guid(BrokerGuids.IBrokerServiceIid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IBrokerService
{
    /// <summary>获取进程采集服务</summary>
    [return: MarshalAs(UnmanagedType.Interface)]
    IBrokerProcessService GetProcessService();

    /// <summary>获取传感器数据服务</summary>
    [return: MarshalAs(UnmanagedType.Interface)]
    IBrokerSensorService GetSensorService();

    /// <summary>Broker 进程是否存活</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsAlive();

    /// <summary>Broker 版本字符串</summary>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetVersion();
}

/// <summary>进程采集服务 — btop4win 核心需求</summary>
[ComVisible(true)]
[Guid(BrokerGuids.IBrokerProcessServiceIid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IBrokerProcessService
{
    /// <summary>获取当前所有进程快照（管理员权限，完整数据）</summary>
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_RECORD)]
    BrokerProcessEntry[] GetProcesses();

    /// <summary>获取进程数量</summary>
    int GetProcessCount();

    /// <summary>刷新进程数据（内部每 2s 自动刷新，此方法强制立即刷新）</summary>
    void Refresh();

    /// <summary>结束指定进程（管理员级 SE_DEBUG, 代理 btop4win 的 kill 请求）</summary>
    /// <param name="pid">目标 PID</param>
    /// <param name="exitCode">进程退出码</param>
    /// <returns>Win32 错误码。0=成功, 非零=GetLastError()</returns>
    int KillProcess(uint pid, uint exitCode);

    /// <summary>客户端身份认证（白名单机制：校验 btop.exe 文件 SHA256）
    /// 必须在调用 GetProcesses/KillProcess 之前完成认证。</summary>
    /// <param name="clientPid">调用方进程 ID（Broker 用于打开进程验证 exe 路径）</param>
    /// <param name="exeHashHex">调用方 exe 的 SHA256 哈希（64 字符 hex 字符串）</param>
    /// <returns>0=认证成功, 1=哈希不匹配(未授权), 2=进程不存在, 3=其他错误</returns>
    int Authenticate(uint clientPid, [MarshalAs(UnmanagedType.BStr)] string exeHashHex);
}

/// <summary>传感器数据服务</summary>
[ComVisible(true)]
[Guid(BrokerGuids.IBrokerSensorServiceIid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IBrokerSensorService
{
    /// <summary>CPU 温度 (°C, -1=不可用)</summary>
    double GetCpuTemperature();

    /// <summary>CPU 温度数据源标签</summary>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetCpuSource();

    /// <summary>GPU 数量</summary>
    int GetGpuCount();

    /// <summary>指定 GPU 的名称</summary>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetGpuName(int index);

    /// <summary>指定 GPU 的温度 (°C)</summary>
    double GetGpuTemperature(int index);

    /// <summary>指定 GPU 的使用率 (%)</summary>
    double GetGpuUsage(int index);

    /// <summary>全量 LHM 传感器</summary>
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_RECORD)]
    BrokerSensorEntry[] GetAllSensors();
}
