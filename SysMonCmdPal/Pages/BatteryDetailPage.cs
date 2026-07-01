// Copyright (c) 2026 SysMonCmdPal
// 电池详情页 — FormContent + AdaptiveCards
// 实时区（每秒）：电量/状态/剩余时间/省电模式
// 健康区（30天缓存）：设计容量/满充容量/健康度/型号/制造商/循环次数

using System.Timers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BatteryDetailPage : ContentPage
{
    private System.Timers.Timer? _refreshTimer;
    private readonly FormContent _form = new();

    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "🔋 电池",
          "size": "Large",
          "weight": "Bolder",
          "spacing": "Medium"
        },
        {
          "type": "TextBlock",
          "text": "${batteryModel}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None"
        },
        {
          "type": "Container",
          "separator": true,
          "spacing": "Medium",
          "items": [
            {
              "type": "TextBlock",
              "text": "${batteryPercent}",
              "size": "Large",
              "weight": "Bolder",
              "horizontalAlignment": "Center",
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "当前电量",
              "size": "Small",
              "isSubtle": true,
              "horizontalAlignment": "Center",
              "spacing": "None"
            }
          ]
        },
        {
          "type": "ColumnSet",
          "separator": true,
          "spacing": "Medium",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${batteryStatus}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "状态",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${batteryRemaining}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "剩余时间",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${batterySaver}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "省电模式",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "text": "${powerInfo}",
          "size": "Medium",
          "weight": "Bolder",
          "horizontalAlignment": "Center",
          "separator": true,
          "spacing": "Medium",
          "$when": "${isDual != \"true\"}"
        },
        {
          "type": "TextBlock",
          "text": "${powerLabel}",
          "size": "Small",
          "isSubtle": true,
          "horizontalAlignment": "Center",
          "spacing": "None",
          "$when": "${isDual != \"true\"}"
        },
        {
          "type": "ColumnSet",
          "separator": true,
          "spacing": "Medium",
          "$when": "${isDual == \"true\"}",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${dualSystemPower}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "系统功耗",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${dualInjectPower}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "注入功率",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${dualBatteryPower}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "电池输出",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "text": "${dualVoltage}",
          "size": "Small",
          "isSubtle": true,
          "horizontalAlignment": "Center",
          "spacing": "None",
          "$when": "${isDual == \"true\"}"
        },
        {
          "type": "Container",
          "separator": true,
          "spacing": "Medium",
          "items": [
            {
              "type": "TextBlock",
              "text": "电池健康度",
              "size": "Medium",
              "weight": "Bolder",
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "${healthInfo}",
              "size": "Small",
              "wrap": true,
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "${capacityInfo}",
              "size": "Small",
              "isSubtle": true,
              "wrap": true,
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "${lastUpdated}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None"
            }
          ]
        }
      ]
    }
    """;

    public BatteryDetailPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Battery.PageTitle");
        Name = Loc.Get("Dock.Battery");
        _form.TemplateJson = Template;
        _form.DataJson = """{"batteryModel":"—","batteryPercent":"—","batteryStatus":"—","batteryRemaining":"—","batterySaver":"—","powerInfo":"—","powerLabel":"充放电功率 / 电压","isDual":false,"dualSystemPower":"—","dualInjectPower":"—","dualBatteryPower":"—","dualVoltage":"","healthInfo":"加载中…","capacityInfo":"","lastUpdated":""}""";
    }

    public void StartTimer()
    {
        if (_refreshTimer != null) return;
        ThreadPool.QueueUserWorkItem(_ => Update());
        _refreshTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _refreshTimer.Elapsed += (_, _) => Update();
        _refreshTimer.Start();
    }

    public override IContent[] GetContent()
    {
        StartTimer();
        return [_form];
    }

    private void Update()
    {
        try
        {
            var info = SystemInfoService.Instance.Current;

            // 无电池
            if (info.BatteryPercent < 0)
            {
                _form.DataJson = JsonHelper.ToJson(new Dictionary<string, string>
                {
                    ["batteryModel"] = "—",
                    ["batteryPercent"] = "—",
                    ["batteryStatus"] = "无电池",
                    ["batteryRemaining"] = "—",
                    ["batterySaver"] = "—",
                    ["powerInfo"] = "—",
                    ["powerLabel"] = "充放电功率 / 电压",
                    ["isDual"] = "false",
                    ["dualSystemPower"] = "—",
                    ["dualInjectPower"] = "—",
                    ["dualBatteryPower"] = "—",
                    ["dualVoltage"] = "",
                    ["healthInfo"] = "未检测到电池（台式机或虚拟机）",
                    ["capacityInfo"] = "",
                    ["lastUpdated"] = "",
                });
                return;
            }

            // 剩余时间格式化
            string remaining = info.BatteryLifeSeconds > 0
                ? $"{info.BatteryLifeSeconds / 3600}h {(info.BatteryLifeSeconds % 3600) / 60}m"
                : "—";

            // 实时充放电功率 + 电压（WMI root\wmi\BatteryStatus）
            var live = BatteryQueryService.Instance.GetStatus();
            string powerInfo = "—";
            string powerLabel = "充放电功率 / 电压";
            bool isDual = false;
            string dualSystemPower = "—";
            string dualInjectPower = "—";
            string dualBatteryPower = "—";
            string dualVoltage = "";

            if (live is { IsValid: true } b)
            {
                string volt = b.VoltageMv > 0 ? $"{b.VoltageMv / 1000.0:F2} V" : "";
                string voltSuffix = volt.Length > 0 ? $" · {volt}" : "";

                if (b.IsDraining && b.PowerOnline)
                {
                    // 双重供电：三栏模板
                    isDual = true;
                    var sysPower = SystemPowerReader.Read();
                    dualVoltage = volt;

                    if (sysPower.IsValid)
                    {
                        double sysW = sysPower.Power;
                        // 注入功率 = WMI ChargeRate（标称值，适配器试图输入的功率）
                        double injectW = b.ChargeRateMw > 0 ? b.ChargeRateMw / 1000.0 : 0;
                        // 电池输出 = 系统功耗 - 注入功率（差值就是电池补的）
                        double batteryW = sysW - injectW;

                        dualSystemPower = $"{sysW:F1} W";
                        dualInjectPower = injectW > 0 ? $"{injectW:F1} W" : "—";
                        dualBatteryPower = batteryW > 0 ? $"{batteryW:F1} W" : "—";
                    }
                }
                else if (b.Charging && b.ChargeRateMw > 0)
                {
                    string power = b.ChargeRateMw / 1000.0 >= 1 ? $"{b.ChargeRateMw / 1000.0:F1} W" : $"{b.ChargeRateMw} mW";
                    powerInfo = $"{power}{voltSuffix}";
                }
                else if (b.Discharging && b.DischargeRateMw > 0)
                {
                    string power = b.DischargeRateMw / 1000.0 >= 1 ? $"{b.DischargeRateMw / 1000.0:F1} W" : $"{b.DischargeRateMw} mW";
                    powerInfo = $"{power}{voltSuffix}";
                }
                else if (b.IsDraining)
                {
                    powerInfo = voltSuffix.TrimStart(' ', '·');
                }
                else
                {
                    powerLabel = "系统功率 / 电池电压";
                    var sysPower = SystemPowerReader.Read();
                    powerInfo = sysPower.IsValid
                        ? $"{sysPower.Power:F1} W{voltSuffix}"
                        : voltSuffix.TrimStart(' ', '·');
                }
            }
            else
            {
                var sysPower = SystemPowerReader.Read();
                powerInfo = sysPower.IsValid ? $"系统功耗 {sysPower.Power:F1} W" : "—";
            }

            // 电池健康信息（30天缓存，首次后台生成）
            var report = BatteryReportService.Instance.Get();

            string healthInfo;
            string capacityInfo;
            string lastUpdated;
            string batteryModel;

            if (report is { } r && r.DesignCapacityMWh > 0)
            {
                string healthEmoji = r.HealthPercent >= 80 ? "✅" : r.HealthPercent >= 60 ? "⚠️" : "🔴";
                healthInfo = $"{healthEmoji} 健康度 {r.HealthPercent:F1}%";

                capacityInfo = $"设计容量 {r.DesignCapacityMWh:N0} mWh · 满充容量 {r.FullChargeCapacityMWh:N0} mWh";
                if (r.CycleCount > 0)
                    capacityInfo += $" · 循环 {r.CycleCount} 次";

                batteryModel = string.IsNullOrEmpty(r.Manufacturer)
                    ? (string.IsNullOrEmpty(r.Name) ? "电池" : r.Name)
                    : $"{r.Manufacturer} {r.Name}".Trim();
                lastUpdated = r.LastRun > DateTime.MinValue
                    ? $"上次更新: {r.LastRun:yyyy-MM-dd}（每 30 天刷新）"
                    : "";
            }
            else
            {
                healthInfo = "⏳ 正在生成电池报告…";
                capacityInfo = "";
                lastUpdated = "";
                batteryModel = "电池";
            }

            // PD/USB-C 充电检测
            bool isPd = PdChargerDetector.IsUsbCEnvironment;
            string statusText = DockFormat.BatteryStatusText(info.BatteryStatus);
            if (isPd && info.BatteryStatus is "charging" or "dual" or "full")
                statusText += " (USB-C)";

            if (isPd && !batteryModel.Contains("USB-C"))
                batteryModel += " · USB-C PD";

            var data = new Dictionary<string, string>
            {
                ["batteryModel"] = batteryModel,
                ["batteryPercent"] = $"{info.BatteryPercent:F0}%",
                ["batteryStatus"] = statusText,
                ["batteryRemaining"] = remaining,
                ["batterySaver"] = info.BatterySaverOn ? "开启" : "关闭",
                ["powerInfo"] = powerInfo,
                ["powerLabel"] = powerLabel,
                ["isDual"] = isDual ? "true" : "false",
                ["dualSystemPower"] = dualSystemPower,
                ["dualInjectPower"] = dualInjectPower,
                ["dualBatteryPower"] = dualBatteryPower,
                ["dualVoltage"] = dualVoltage,
                ["healthInfo"] = healthInfo,
                ["capacityInfo"] = capacityInfo,
                ["lastUpdated"] = lastUpdated,
            };

            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BatteryDetailPage] Update failed: {ex.Message}");
        }
    }
}
