using System.Runtime.InteropServices;

namespace QuickDrop.Core.Models;

public sealed record DeviceIdentity(string Name, string Platform, string InstanceId)
{
    public static DeviceIdentity Current { get; } = new(
        Environment.MachineName,
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.OSDescription,
        Guid.NewGuid().ToString("N"));
}
