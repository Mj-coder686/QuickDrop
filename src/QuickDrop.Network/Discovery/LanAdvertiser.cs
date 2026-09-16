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

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var targets = new[]
        {
            new IPEndPoint(MulticastAddress, DiscoveryPort),
            new IPEndPoint(IPAddress.Broadcast, DiscoveryPort),
            new IPEndPoint(IPAddress.Loopback, DiscoveryPort)
        };

        while (!cancellationToken.IsCancellationRequested && !session.IsExpired)
        {
            foreach (var target in targets)
            {
                try
                {
                    await udp.SendAsync(bytes, target, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A disabled multicast/broadcast path should not disable the other discovery paths.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken).ConfigureAwait(false);
        }
    }
}
