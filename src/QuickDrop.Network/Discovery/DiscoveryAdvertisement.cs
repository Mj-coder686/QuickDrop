using QuickDrop.Core.Models;

namespace QuickDrop.Network.Discovery;

public sealed record DiscoveryAdvertisement(
    int ProtocolVersion,
    Guid SessionId,
    string Salt,
    string PairingTag,
    long ExpiresAtUnixSeconds,
    int TcpPort,
    string CertificateFingerprint,
    DeviceIdentity Sender);
