namespace SysMonBroker.IPC;

/// <summary>
/// 命名管道权限代理协议常量 — 权威定义见工作区根 btop4win-broker-ipc.md §2。
/// 服务端由 SysMonBroker(以管理员运行)实现; 客户端为 btop4win 等普通用户进程。
/// </summary>
internal static class BrokerAdminPipe
{
    /// <summary>管道名(协议 §2.1)</summary>
    public const string PipeName = @"\\.\pipe\SysMonBrokerAdmin";

    /// <summary>帧 Magic = 0x534B5042('SKPB', 协议 §2.3)</summary>
    public const uint Magic = 0x534B5042;

    /// <summary>载荷长度上限(协议 §2.3, 0..4096)</summary>
    public const int MaxPayload = 4096;

    /// <summary>服务端建议同时 ≥ 4 个实例(协议 §2.1, 客户端可能重试)</summary>
    public const int InstanceCount = 4;

    /// <summary>管道 ACL(协议 §2.1): 普通用户可读写, 管理员/系统全权</summary>
    public const string PipeSddl = "D:P(A;;GRGW;;;BU)(A;;GA;;;BA)(A;;GA;;;SY)";

    /// <summary>数据目录: %LOCALAPPDATA%\SysMonCmdPal(协议 §2.5)</summary>
    public const string DataDirName = "SysMonCmdPal";

    /// <summary>白名单文件(协议 §2.5): 每行一个小写 hex SHA256, 客户端自助写入</summary>
    public const string WhitelistFileName = "registered_hashes.txt";

    /// <summary>命令表(协议 §2.4)</summary>
    public enum Cmd : uint
    {
        Auth = 1,          // 载荷: [pid u32][sha256 hex 64B] — 必须为第一条
        Ping = 2,          // 无载荷
        Terminate = 3,     // 载荷: [pid u32] — 以管理员权限 TerminateProcess(pid, 1)
        ServiceControl = 4, // 载荷: [name UTF-8 NUL][cmd u32] — 协议保留
    }

    // 常用 Win32 错误码(协议 §2.7 错误语义)
    public const uint ErrorAccessDenied = 5;      // 未鉴权 / hash 不在白名单 / 权限不足
    public const uint ErrorNotSupported = 50;     // 功能不支持(如 SERVICE_CONTROL 占位)
    public const uint ErrorInvalidParameter = 87; // 载荷非法
    public const uint ErrorNotFound = 1168;       // 目标进程不存在
}
