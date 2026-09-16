using QuickDrop.Core.Models;

namespace QuickDrop.Core.Abstractions;

public enum TransferRouteKind
{
    LanDirect,
    InternetPeerToPeer,
    Relay
}

public sealed record RouteCandidate(
    TransferRouteKind Kind,
    Guid SessionId,
    string Host,
    int Port,
    byte[] Salt,
    string CertificateFingerprint,
    DeviceIdentity Sender);

public interface IRouteProvider
{
    Task<RouteCandidate> FindAsync(string pairingCode, TimeSpan timeout, CancellationToken cancellationToken);
}
