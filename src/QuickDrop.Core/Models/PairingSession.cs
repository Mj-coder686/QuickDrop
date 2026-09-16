namespace QuickDrop.Core.Models;

public sealed record PairingSession(
    Guid SessionId,
    string Code,
    byte[] Salt,
    DateTimeOffset ExpiresAt,
    DeviceIdentity Sender)
{
    public static PairingSession Create(DeviceIdentity? sender = null, TimeSpan? lifetime = null) => new(
        Guid.NewGuid(),
        Security.PairingSecurity.GenerateCode(),
        Security.PairingSecurity.CreateSalt(),
        DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
        sender ?? DeviceIdentity.Current);

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
