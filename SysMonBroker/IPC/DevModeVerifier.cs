using System.Reflection;

namespace SysMonBroker.IPC;

/// <summary>
/// DevMode 门控(精简版, 继承 v2.2/v2.3 验证过的编译期路径门控模式, 去掉 SSH 签名环节)。
///
/// 安全模型:
///   release 构建(不带 -p:Dev): AssemblyMetadata 无 DevRepoPath → IsDevModeActive() 恒 false
///   dev 构建(-p:Dev=true): 内嵌编译时项目目录路径 → 该路径下须存在 .devmode_marker
///   攻击者拿到 dev build: 不知道内嵌路径 → 无法创建正确位置的 marker → DevMode 不可激活
///
/// 用途: dev 构建下 AUTH 放行任意 hash(管道服务端), 便于本地联调与自动化测试;
/// 新架构白名单为客户端自助注册(--register-broker), DevMode 不再需要签名后门。
///
/// 激活条件(全部满足):
///   1. 二进制是 -p:Dev=true 编译(含 DevRepoPath 元数据)
///   2. 编译时注入的路径下存在 .devmode_marker 文件
///   3. 运行时开关文件 .devmode_on 存在(--devmode-on 创建, --devmode-off 删除)
/// </summary>
public static class DevModeVerifier
{
    private const string MarkerFileName = ".devmode_marker";
    private const string RuntimeFlagFileName = ".devmode_on";

    // 编译期注入: -p:Dev=true 时 csproj 写入 AssemblyMetadata("DevRepoPath", 项目目录)
    // release 构建无此 attribute → null → DevMode 永远禁用
    private static readonly string? DevRepoPath = Assembly
        .GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => string.Equals(a.Key, "DevRepoPath", StringComparison.Ordinal))
        ?.Value;

    /// <summary>是否 dev 构建(编译期内嵌了路径元数据)。</summary>
    public static bool IsDevBuild => !string.IsNullOrEmpty(DevRepoPath);

    /// <summary>
    /// 运行时开关(--devmode-on/--devmode-off 调用)。
    /// 仅 dev build + marker 文件存在时允许设置。
    /// 写文件 flag 而非内存变量, 使所有 broker 进程共享状态。
    /// </summary>
    public static bool SetRuntimeOverride(bool enabled)
    {
        if (string.IsNullOrEmpty(DevRepoPath)) return false;
        if (!File.Exists(Path.Combine(DevRepoPath, MarkerFileName))) return false;

        try
        {
            if (enabled)
                File.WriteAllText(Path.Combine(DevRepoPath, RuntimeFlagFileName), DateTime.UtcNow.ToString("o"));
            else if (File.Exists(Path.Combine(DevRepoPath, RuntimeFlagFileName)))
                File.Delete(Path.Combine(DevRepoPath, RuntimeFlagFileName));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DevMode 是否激活(dev 构建 + marker + 运行时开关三者同时满足)。</summary>
    public static bool IsDevModeActive()
    {
        if (string.IsNullOrEmpty(DevRepoPath)) return false;

        string markerPath = Path.Combine(DevRepoPath, MarkerFileName);
        if (!File.Exists(markerPath)) return false;

        return File.Exists(Path.Combine(DevRepoPath, RuntimeFlagFileName));
    }
}
