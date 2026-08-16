using SysMonBroker.Logging;

namespace SysMonBroker.IPC;

/// <summary>
/// AUTH 白名单读取(协议 §2.5)。
/// 文件: %LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt, 每行一个小写 hex SHA256。
/// 每次 AUTH 重新读文件(支持热更新, 无需重启); 文件不存在视为白名单为空。
/// 文件由客户端自助写入(如 btop4win.exe --register-broker), 服务端只读。
/// </summary>
internal static class BrokerHashWhitelist
{
    /// <summary>校验 hash 是否在白名单中。任何 IO 失败均视为不在白名单(安全默认)。</summary>
    public static bool Contains(string sha256Hex)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrokerAdminPipe.DataDirName,
                BrokerAdminPipe.WhitelistFileName);

            if (!File.Exists(path)) return false;

            // 协议 §2.5: 逐行比对 (OrdinalIgnoreCase); 每次 AUTH 重新读文件
            foreach (string line in File.ReadLines(path))
            {
                if (string.Equals(line.Trim(), sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log($"Whitelist read failed: {ex.Message}");
            return false;
        }
    }

    private static void Log(string msg) => BrokerLogger.Log(msg);
}
