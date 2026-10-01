// SysMonBroker/Host/DevModeService.cs
//
// DevMode 运行时开关的**决策与副作用**（从 Program.Main / Program.DevModeToggle 提取，P0 盲区 1/3）。
//
// 为什么单独成文件：
//   原 Program.DevModeToggle 把「是否 dev 构建」「marker 是否可写」「开关结果」三层判定
//   与日志文案、返回码揉在一个方法里，而这三层判定中的前两层完全可由布尔输入决定 ——
//   提取为纯函数后，不必起真实文件系统/构建元数据即可验证全部分支组合。
//
// ⚠ 三条文案逐字保留：docs/DevMode.md 与 verify-devmode.ps1 会 grep 这些串，
//   且第三个串（toggle failed）是 marker 缺失时的唯一诊断线索。
//   "—" 是 U+2014 EM DASH，不是 ASCII 连字符，改动即破坏下游 grep。

namespace SysMonBroker.Host;

/// <summary>DevMode 开关的判定结果（纯分类，不含副作用）。</summary>
internal enum DevModeResult
{
    /// <summary>非 dev 构建（无 DevRepoPath 元数据）：门控不存在，开关不可用。</summary>
    NotDevBuild,

    /// <summary>开关已置为 ON（.devmode_on 已写入）。</summary>
    Enabled,

    /// <summary>开关已置为 OFF（.devmode_on 已删除）。</summary>
    Disabled,

    /// <summary>dev 构建但写文件失败（marker 缺失或 IO 异常）。</summary>
    ToggleFailed,
}

/// <summary>
/// DevMode 开关服务：纯决策（<see cref="Classify"/> / <see cref="MessageFor"/> /
/// <see cref="ExitCodeFor"/>）+ 一个薄副作用入口（<see cref="Toggle"/>）。
/// </summary>
/// <remarks>
/// 本类**不持有状态**：运行时开关的真实载体是磁盘上的 .devmode_on 文件
/// （用文件 flag 而非内存变量，使所有 broker 进程共享状态，与旧版 COM 时代设计一致），
/// 由 <see cref="IPC.DevModeVerifier"/> 负责读写。
/// </remarks>
internal static class DevModeService
{
    /// <summary>
    /// 三分支判定顺序即语义：**先判构建类型，再判写文件结果**。
    /// 非 dev 构建时根本不尝试写文件（<paramref name="setOk"/> 无意义）。
    /// </summary>
    /// <param name="isDevBuild">编译期是否内嵌了 DevRepoPath 元数据。</param>
    /// <param name="setOk">运行时开关文件是否写入/删除成功（仅 dev 构建下才会被求值）。</param>
    /// <param name="enable">true = --devmode-on，false = --devmode-off。</param>
    public static DevModeResult Classify(bool isDevBuild, bool setOk, bool enable)
    {
        if (!isDevBuild)
            return DevModeResult.NotDevBuild;
        if (!setOk)
            return DevModeResult.ToggleFailed;

        return enable ? DevModeResult.Enabled : DevModeResult.Disabled;
    }

    /// <summary>结果 → 日志文案（逐字保留，见文件头）。</summary>
    public static string MessageFor(DevModeResult result, bool enable) => result switch
    {
        DevModeResult.NotDevBuild =>
            "DevMode: not a dev build (no DevRepoPath embedded); build with -p:Dev=true",
        DevModeResult.Enabled or DevModeResult.Disabled =>
            $"DevMode: {(enable ? "ON" : "OFF")} — file flag updated, all broker processes will honor it",
        _ =>
            "DevMode: toggle failed (marker missing?); create .devmode_marker in the project dir",
    };

    /// <summary>结果 → 进程退出码：仅「成功设置」返 0，其余（不可用/失败）返 1。</summary>
    public static int ExitCodeFor(DevModeResult result) =>
        result is DevModeResult.Enabled or DevModeResult.Disabled ? 0 : 1;

    /// <summary>
    /// 副作用入口：读构建门控 → 写/删开关文件 → 记录日志 → 返回退出码。
    /// </summary>
    /// <remarks>
    /// ⚠ 短路顺序必须保持：非 dev 构建时**不得调用** SetRuntimeOverride
    /// （原实现是 early-return，此处用 &amp;&amp; 短路复现同一语义）。
    /// </remarks>
    public static int Toggle(bool enable, Action<string> log)
    {
        bool isDevBuild = IPC.DevModeVerifier.IsDevBuild;
        bool setOk = isDevBuild && IPC.DevModeVerifier.SetRuntimeOverride(enable);
        DevModeResult result = Classify(isDevBuild, setOk, enable);

        log(MessageFor(result, enable));
        return ExitCodeFor(result);
    }
}
