// Copyright (c) 2026 SysMonCmdPal

using System.Text.RegularExpressions;

namespace SysMonCmdPal;

internal enum GpuKind
{
    Unknown,
    Integrated,
    Discrete,
}

internal static partial class GpuClassifier
{
    private static readonly string[] UnknownNameMarkers =
    [
        "MICROSOFT BASIC DISPLAY",
        "REMOTE DISPLAY",
        "INDIRECT DISPLAY",
        "VIRTUAL DISPLAY",
        "VIRTUAL GRAPHICS",
        "VIRTUAL GPU",
        "VMWARE SVGA",
        "HYPER-V VIDEO",
        "VIRTUALBOX",
        "PARALLELS",
        "CITRIX",
        "VIRTIO GPU",
        "QXL",
        "BOCHS",
    ];

    private static readonly string[] DiscreteNameMarkers =
    [
        "NVIDIA",
        "GEFORCE",
        "QUADRO",
        "TESLA",
        "TITAN",
        "RADEON RX",
        "RADEON PRO",
        "RADEON VII",
        "FIREPRO",
    ];

    internal static GpuKind Classify(GpuInfo gpu)
    {
        string name = NormalizeName(gpu.Name);

        if (ContainsAny(name, UnknownNameMarkers))
            return GpuKind.Unknown;

        if (name.Length > 0 && IsIntegratedName(name))
            return GpuKind.Integrated;

        if (name.Length > 0 && IsDiscreteName(name))
            return GpuKind.Discrete;

        return gpu.MemoryTotalMB > 0 ? GpuKind.Discrete : GpuKind.Unknown;
    }

    internal static string GetIcon(GpuInfo gpu) => Classify(gpu) switch
    {
        GpuKind.Integrated => SysMonIcons.GpuIntegrated,
        GpuKind.Discrete => SysMonIcons.GpuDiscrete,
        _ => SysMonIcons.Gpu,
    };

    private static bool IsIntegratedName(string name)
    {
        if (name.Contains("INTEGRATED", StringComparison.Ordinal) ||
            name.Contains("ADRENO", StringComparison.Ordinal))
        {
            return true;
        }

        if (name.Contains("INTEL", StringComparison.Ordinal) &&
            name.Contains("GRAPHICS", StringComparison.Ordinal) &&
            !IntelDiscreteArcRegex().IsMatch(name))
        {
            return true;
        }

        return name.Contains("RADEON GRAPHICS", StringComparison.Ordinal) ||
            AmdIntegratedGraphicsRegex().IsMatch(name);
    }

    private static bool IsDiscreteName(string name) =>
        IntelDiscreteArcRegex().IsMatch(name) || ContainsAny(name, DiscreteNameMarkers);

    private static string NormalizeName(string? name) => (name ?? string.Empty)
        .Trim()
        .ToUpperInvariant()
        .Replace("(R)", string.Empty, StringComparison.Ordinal)
        .Replace("(TM)", string.Empty, StringComparison.Ordinal);

    private static bool ContainsAny(string value, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (value.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"\bARC\s+(?:(?:A|B)\d{3}|PRO)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IntelDiscreteArcRegex();

    [GeneratedRegex(@"\bRADEON\s+(?:(?:RX\s+)?VEGA\s+\d+|\d{3,4}[MS])\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmdIntegratedGraphicsRegex();
}
