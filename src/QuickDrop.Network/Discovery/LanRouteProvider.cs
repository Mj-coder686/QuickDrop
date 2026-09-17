using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QuickDrop.Core.Abstractions;
using QuickDrop.Core.Security;
using QuickDrop.Network.Protocol;

namespace QuickDrop.Network.Discovery;

public sealed class LanRouteProvider : IRouteProvider
{
    public async Task<RouteCandidate> FindAsync(string pairingCode, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (pairingCode.Length != 6 || !pairingCode.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Pairing code must contain exactly six digits.", nameof(pairingCode));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var udp = CreateListener();

        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
                DiscoveryAdvertisement? advertisement;
                try
                {
                    advertisement = JsonSerializer.Deserialize<DiscoveryAdvertisement>(result.Buffer, FrameProtocol.JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (advertisement is null ||
                    advertisement.ProtocolVersion != FrameProtocol.ProtocolVersion ||
                    advertisement.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    continue;
                }

                byte[] salt;
                try
                {
                    salt = Convert.FromBase64String(advertisement.Salt);
                }
                catch (FormatException)
                {
                    continue;
                }

                var expectedTag = PairingSecurity.ComputeDiscoveryTag(advertisement.SessionId, salt, pairingCode);
                if (!PairingSecurity.FixedTimeEqualsHex(expectedTag, advertisement.PairingTag))
                {
                    continue;
                }

                return new RouteCandidate(
                    TransferRouteKind.LanDirect,
                    advertisement.SessionId,
                    result.RemoteEndPoint.Address.ToString(),
                    advertisement.TcpPort,
                    salt,
                    advertisement.CertificateFingerprint,
                    advertisement.Sender);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No sender with that code was found on the local network.");
        }
    }

    private static UdpClient CreateListener()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanAdvertiser.DiscoveryPort));
        var joinedInterface = false;
        foreach (var binding in LanNetworkInterfaces.GetActiveIPv4Interfaces())
        {
            try
            {
                udp.JoinMulticastGroup(LanAdvertiser.MulticastAddress, binding.Address);
                joinedInterface = true;
            }
            catch (SocketException)
            {
                // Keep joining on the remaining adapters; broadcast remains available.
            }
        }

        if (!joinedInterface)
        {
            try
            {
                udp.JoinMulticastGroup(LanAdvertiser.MulticastAddress);
            }
            catch (SocketException)
            {
                // Broadcast and loopback discovery remain available.
            }
        }

        return udp;
    }
}
