using System;
using LibreHardwareMonitor.Hardware;

class LhmTest
{
    static void Main()
    {
        Console.WriteLine("Testing LHM 0.9.6...");
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
            };
            Console.WriteLine("Computer created, opening...");
            computer.Open();
            Console.WriteLine($"Open OK! Hardware count: {computer.Hardware.Count}");
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                Console.WriteLine($"  {hw.HardwareType}: {hw.Name}");
                foreach (var s in hw.Sensors)
                    if (s.Value.HasValue)
                        Console.WriteLine($"    {s.Name}: {s.Value:F1} {s.SensorType}");
            }
            computer.Close();
            Console.WriteLine("SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
