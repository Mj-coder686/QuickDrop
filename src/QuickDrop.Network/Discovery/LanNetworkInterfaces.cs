using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuickDrop.Network.Discovery;

internal sealed record LanInterfaceBinding(IPAddress Address, IPAddress BroadcastAddress, string Name);

internal static class LanNetworkInterfaces
{
    public static IReadOnlyList<LanInterfaceBinding> GetActiveIPv4Interfaces()
    {
        var bindings = new List<LanInterfaceBinding>();
        var seen = new HashSet<IPAddress>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        address.Equals(IPAddress.Any) ||
                        !seen.Add(address))
                    {
                        continue;
                    }

                    bindings.Add(new LanInterfaceBinding(
                        address,
                        CalculateBroadcastAddress(address, unicast.PrefixLength),
                        networkInterface.Name));
                }
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while the list is being enumerated.
            }
        }

        return bindings;
    }

    public static IPAddress CalculateBroadcastAddress(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses have a directed broadcast address.", nameof(address));
        }

        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        var addressValue = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var broadcast = addressValue | ~mask;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, broadcast);
        return new IPAddress(bytes);
    }
}
