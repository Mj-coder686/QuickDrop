using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QuickDrop.Core.Models;
using QuickDrop.Core.Security;
using QuickDrop.Network.Protocol;

namespace QuickDrop.Network.Discovery;

public sealed class LanAdvertiser
{
    public const int DiscoveryPort = 45873;
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.77");

    public async Task RunAsync(
        PairingSession session,
        int tcpPort,
        string certificateFingerprint,
        CancellationToken cancellationToken)
    {
        var advertisement = new DiscoveryAdvertisement(
            FrameProtocol.ProtocolVersion,
            session.SessionId,
            Convert.ToBase64String(session.Salt),
            PairingSecurity.ComputeDiscoveryTag(session.SessionId, session.Salt, session.Code),
            session.ExpiresAt.ToUnixTimeSeconds(),
            tcpPort,
            certificateFingerprint,
            session.Sender);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(advertisement, FrameProtocol.JsonOptions);

        var channels = CreateChannels();

        try
        {
            while (!cancellationToken.IsCancellationRequested && !session.IsExpired)
            {
                foreach (var channel in channels)
                {
                    foreach (var target in channel.Targets)
                    {
                        try
                        {
                            await channel.Client.SendAsync(bytes, target, cancellationToken).ConfigureAwait(false);
                        }
                        catch (SocketException)
                        {
                            // One unavailable adapter must not disable discovery on the others.
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Client.Dispose();
            }
        }
    }

    private static IReadOnlyList<AdvertisementChannel> CreateChannels()
    {
        var channels = new List<AdvertisementChannel>();
        foreach (var binding in LanNetworkInterfaces.GetActiveIPv4Interfaces())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(binding.Address, 0)) { EnableBroadcast = true };
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                udp.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    binding.Address.GetAddressBytes());
                channels.Add(new AdvertisementChannel(
                    udp,
                    [
                        new IPEndPoint(MulticastAddress, DiscoveryPort),
                        new IPEndPoint(binding.BroadcastAddress, DiscoveryPort)
                    ]));
            }
            catch (SocketException)
            {
                // The adapter may disappear between enumeration and binding.
            }
        }

        var loopback = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        channels.Add(new AdvertisementChannel(
            loopback,
            [new IPEndPoint(IPAddress.Loopback, DiscoveryPort)]));

        return channels;
    }

    private sealed record AdvertisementChannel(UdpClient Client, IReadOnlyList<IPEndPoint> Targets);
}
