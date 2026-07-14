using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GpuClassifierTests
{
    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", 2048)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 4096)]
    [InlineData("Intel(R) Arc(TM) Graphics", 8192)]
    [InlineData("AMD Radeon(TM) Graphics", 4096)]
    [InlineData("AMD Radeon 780M Graphics", 4096)]
    [InlineData("AMD Radeon 8060S Graphics", 8192)]
    [InlineData("AMD Radeon RX Vega 10 Graphics", 2048)]
    [InlineData("Qualcomm Adreno X1-85 GPU", 16384)]
    public void Classify_KnownIntegratedGpu_ReturnsIntegrated(string name, double memoryTotalMb)
    {
        var gpu = new GpuInfo { Name = name, MemoryTotalMB = memoryTotalMb };

        Assert.Equal(GpuKind.Integrated, GpuClassifier.Classify(gpu));
        Assert.Equal(SysMonIcons.GpuIntegrated, GpuClassifier.GetIcon(gpu));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon Pro W6800")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel Arc B580 Graphics")]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics")]
    [InlineData("AMD Radeon RX 7600M XT")]
    public void Classify_KnownDiscreteGpu_ReturnsDiscrete(string name)
    {
        var gpu = new GpuInfo { Name = name };

        Assert.Equal(GpuKind.Discrete, GpuClassifier.Classify(gpu));
        Assert.Equal(SysMonIcons.GpuDiscrete, GpuClassifier.GetIcon(gpu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mystery GPU")]
    public void Classify_UnknownWithoutMemory_ReturnsUnknown(string? name)
    {
        var gpu = new GpuInfo { Name = name! };

        Assert.Equal(GpuKind.Unknown, GpuClassifier.Classify(gpu));
        Assert.Equal(SysMonIcons.Gpu, GpuClassifier.GetIcon(gpu));
    }

    [Fact]
    public void Classify_UnknownNameWithMemory_UsesDiscreteFallback()
    {
        var gpu = new GpuInfo { Name = "Vendor Accelerator", MemoryTotalMB = 8192 };

        Assert.Equal(GpuKind.Discrete, GpuClassifier.Classify(gpu));
    }

    [Theory]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Remote Display Adapter")]
    [InlineData("Contoso Virtual Display Adapter")]
    [InlineData("VMware SVGA 3D")]
    [InlineData("VirtualBox Graphics Adapter")]
    [InlineData("Parallels Display Adapter")]
    [InlineData("Citrix Indirect Display Adapter")]
    [InlineData("VirtIO GPU")]
    [InlineData("QXL Display Adapter")]
    public void Classify_NonPhysicalDisplayAdapter_IgnoresMemoryFallback(string name)
    {
        var gpu = new GpuInfo { Name = name, MemoryTotalMB = 8192 };

        Assert.Equal(GpuKind.Unknown, GpuClassifier.Classify(gpu));
        Assert.Equal(SysMonIcons.Gpu, GpuClassifier.GetIcon(gpu));
    }
}
