using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuickDrop.Network.Security;

public sealed class EphemeralCertificate : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    private EphemeralCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    public static EphemeralCertificate Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=QuickDrop temporary session", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(15));
        // Windows SChannel cannot use an EphemeralKeySet certificate as a TLS server credential.
        // Without PersistKeySet, this user-scoped temporary key is removed when the certificate is disposed.
        var certificate = new X509Certificate2(
            created.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        created.Dispose();
        return new EphemeralCertificate(certificate);
    }

    public SslServerAuthenticationOptions CreateServerOptions() => new()
    {
        ServerCertificate = Certificate,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateRequired = false,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };

    public static SslClientAuthenticationOptions CreateClientOptions(string expectedFingerprint) => new()
    {
        TargetHost = "quickdrop-session",
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase)
    };

    public void Dispose() => Certificate.Dispose();
}
