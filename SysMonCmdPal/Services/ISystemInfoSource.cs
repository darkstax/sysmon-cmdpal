// Copyright (c) 2026 SysMonCmdPal
// T1-1: 采集源注册制抽象 — SystemInfoService 不再逐调用硬编码各 monitor，
// 而是持有 ISystemInfoSource 有序列表，Refresh() 按注册顺序遍历调用。

namespace SysMonCmdPal;

/// <summary>
/// 系统信息采集源。实现者把自己的指标写入共享 <see cref="SystemSnapshot"/>，
/// 由 <c>SystemInfoService.Refresh()</c> 按注册顺序逐个调用（ref 传入，直接写字段）。
/// 语义约束（与重构前的直调代码一致）：
/// <list type="bullet">
/// <item>实现应自带与重构前相同的内部异常吞噬语义（哨兵值/回退写入不变）；</item>
/// <item>服务层另有兜底 try/catch：向外抛出的异常只跳过本源，不影响其他源执行；</item>
/// <item>不得依赖其他源写入的字段，除非依赖注册顺序（默认注册表保持原采集顺序）。</item>
/// </list>
/// </summary>
internal interface ISystemInfoSource
{
    /// <summary>把本源采集结果写入 <paramref name="snapshot"/> 的对应字段。</summary>
    void ReadInto(ref SystemSnapshot snapshot);
}
