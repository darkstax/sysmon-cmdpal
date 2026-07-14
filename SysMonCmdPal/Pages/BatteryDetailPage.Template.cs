// Copyright (c) 2026 SysMonCmdPal

namespace SysMonCmdPal;

internal sealed partial class BatteryDetailPage
{
    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "电池",
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
}
