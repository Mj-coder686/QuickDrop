using QuickDrop.Core.Models;

namespace QuickDrop.Network.Transfer;

public sealed record TransferResult(
    bool Success,
    string Message,
    long TotalBytes,
    string RouteDescription);

public sealed class SenderService : IAsyncDisposable
{
    private readonly PairingSession _session;
    private readonly TransferManifest _manifest;
    private readonly Security.EphemeralCertificate _certificate;
    private readonly Discovery.LanAdvertiser _advertiser = new();
    private readonly TaskCompletionSource<QuickDrop.Core.Abstractions.RouteCandidate> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _failedPairingAttempts;
    private bool _disposed;
    private long _totalNetworkBytesSent;

    private SenderService(PairingSession session, TransferManifest manifest, Security.EphemeralCertificate certificate)
    {
        _session = session;
        _manifest = manifest;
        _certificate = certificate;
    }

    public string PairingCode => _session.Code;
    public TransferManifest Manifest => _manifest;
    public Task<QuickDrop.Core.Abstractions.RouteCandidate> Ready => _ready.Task;
    public long TotalNetworkBytesSent => Interlocked.Read(ref _totalNetworkBytesSent);

    public Func<DeviceIdentity, CancellationToken, Task<bool>> ConfirmDeviceAsync { get; set; } = (_, _) => Task.FromResult(false);
    public event Action<TransferProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;

    public static async Task<SenderService> CreateAsync(
        IEnumerable<string> selectedPaths,
        DeviceIdentity? identity = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await QuickDrop.Core.Files.FileManifestBuilder.BuildAsync(selectedPaths, cancellationToken).ConfigureAwait(false);
        return new SenderService(PairingSession.Create(identity), manifest, Security.EphemeralCertificate.Create());
    }

    public async Task<TransferResult> RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remainingLifetime = _session.ExpiresAt - DateTimeOffset.UtcNow;
        if (remainingLifetime <= TimeSpan.Zero)
        {
            throw new TimeoutException("The pairing session has expired.");
        }

        lifetimeSource.CancelAfter(remainingLifetime);
        var token = lifetimeSource.Token;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ready.TrySetResult(new QuickDrop.Core.Abstractions.RouteCandidate(
            QuickDrop.Core.Abstractions.TransferRouteKind.LanDirect,
            _session.SessionId,
            System.Net.IPAddress.Loopback.ToString(),
            port,
            _session.Salt,
            _certificate.Fingerprint,
            _session.Sender));

        using var advertisementSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        var advertisementTask = _advertiser.RunAsync(_session, port, _certificate.Fingerprint, advertisementSource.Token);
        StatusChanged?.Invoke("Waiting for a receiver");

        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                try
                {
                    if (await HandleClientAsync(client, token).ConfigureAwait(false))
                    {
                        StatusChanged?.Invoke("Transfer complete");
                        return new TransferResult(true, "Transfer complete", _manifest.TotalBytes, "LAN direct");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or InvalidDataException)
                {
                    StatusChanged?.Invoke($"Connection interrupted: {exception.Message}. Waiting for retry");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The pairing session expired before the transfer completed.");
        }
        finally
        {
            listener.Stop();
            advertisementSource.Cancel();
            try
            {
                await advertisementTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<bool> HandleClientAsync(System.Net.Sockets.TcpClient client, CancellationToken cancellationToken)
    {
        await using var ssl = new System.Net.Security.SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsServerAsync(_certificate.CreateServerOptions(), cancellationToken).ConfigureAwait(false);

        var pairControl = await Protocol.FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
        EnsureType(pairControl, Protocol.MessageTypes.PairRequest);
        var request = pairControl.GetPayload<Protocol.PairRequestMessage>();

        if (!ValidatePairRequest(request))
        {
            _failedPairingAttempts++;
            await Protocol.FrameProtocol.WriteControlAsync(
                ssl,
                Protocol.MessageTypes.PairDecision,
                new Protocol.PairDecisionMessage(false, "Pairing code is invalid or expired."),
                cancellationToken).ConfigureAwait(false);
            if (_failedPairingAttempts >= 5)
            {
                throw new UnauthorizedAccessException("Too many invalid pairing attempts. Start a new session.");
            }

            return false;
        }

        StatusChanged?.Invoke($"Confirmation required for {request.Receiver.Name}");
        var accepted = await ConfirmDeviceAsync(request.Receiver, cancellationToken).ConfigureAwait(false);
        await Protocol.FrameProtocol.WriteControlAsync(
            ssl,
            Protocol.MessageTypes.PairDecision,
            new Protocol.PairDecisionMessage(accepted, accepted ? "Accepted" : "Rejected by sender"),
            cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            StatusChanged?.Invoke("Receiver rejected");
            return false;
        }

        await Protocol.FrameProtocol.WriteControlAsync(
            ssl,
            Protocol.MessageTypes.Manifest,
            new Protocol.ManifestMessage(_manifest),
            cancellationToken).ConfigureAwait(false);

        var resumeControl = await Protocol.FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
        EnsureType(resumeControl, Protocol.MessageTypes.ResumeMap);
        var resumeMap = resumeControl.GetPayload<Protocol.ResumeMapMessage>().Offsets;

        var tracker = new ProgressTracker(_manifest.TotalBytes);
        long completedBytes = 0;
        var buffer = new byte[Protocol.FrameProtocol.ChunkSize];

        foreach (var entry in _manifest.Entries.Where(item => !item.IsDirectory))
        {
            var requestedOffset = resumeMap.TryGetValue(entry.Id, out var value)
                ? Math.Clamp(value, 0, entry.Length)
                : 0;
            var sent = false;
            for (var attempt = 0; attempt < 2 && !sent; attempt++)
            {
                var offset = attempt == 0 ? requestedOffset : 0;
                await Protocol.FrameProtocol.WriteControlAsync(
                    ssl,
                    Protocol.MessageTypes.FileStart,
                    new Protocol.FileStartMessage(entry.Id, entry.RelativePath, entry.Length, offset, entry.LastWriteTimeUtc),
                    cancellationToken).ConfigureAwait(false);

                var hash = await SendFileAsync(ssl, entry, offset, completedBytes, tracker, buffer, cancellationToken).ConfigureAwait(false);
                await Protocol.FrameProtocol.WriteControlAsync(
                    ssl,
                    Protocol.MessageTypes.FileEnd,
                    new Protocol.FileEndMessage(entry.Id, hash),
                    cancellationToken).ConfigureAwait(false);

                var ackControl = await Protocol.FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
                EnsureType(ackControl, Protocol.MessageTypes.FileAck);
                var ack = ackControl.GetPayload<Protocol.FileAckMessage>();
                if (!string.Equals(ack.FileId, entry.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Receiver acknowledged a different file.");
                }

                sent = ack.Success;
                if (!sent && attempt == 1)
                {
                    throw new InvalidDataException($"Integrity check failed twice for {entry.RelativePath}: {ack.Message}");
                }
            }

            completedBytes += entry.Length;
        }

        await Protocol.FrameProtocol.WriteControlAsync(
            ssl,
            Protocol.MessageTypes.TransferComplete,
            new Protocol.TransferCompleteMessage(true, "All files verified"),
            cancellationToken).ConfigureAwait(false);
        await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
        ProgressChanged?.Invoke(tracker.Add(0, _manifest.TotalBytes, string.Empty, "Completed"));
        return true;
    }

    private async Task<string> SendFileAsync(
        Stream destination,
        TransferEntry entry,
        long offset,
        long completedBytes,
        ProgressTracker tracker,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (entry.SourcePath is null)
        {
            throw new InvalidDataException("The local source path is missing.");
        }

        await using var source = new FileStream(
            entry.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != entry.Length)
        {
            throw new IOException($"The source file changed size: {entry.RelativePath}");
        }

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long hashed = 0;
        while (hashed < offset)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - hashed)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Source file ended before the resume position.");
            }

            hash.AppendData(buffer, 0, read);
            hashed += read;
        }

        long position = offset;
        while (position < entry.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Length - position)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Source file changed during transfer.");
            }

            hash.AppendData(buffer, 0, read);
            await Protocol.FrameProtocol.WriteDataAsync(destination, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _totalNetworkBytesSent, read);
            position += read;
            ProgressChanged?.Invoke(tracker.Add(read, completedBytes + position, entry.RelativePath));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private bool ValidatePairRequest(Protocol.PairRequestMessage request)
    {
        if (request.ProtocolVersion != Protocol.FrameProtocol.ProtocolVersion ||
            request.SessionId != _session.SessionId ||
            _session.IsExpired)
        {
            return false;
        }

        byte[] nonce;
        try
        {
            nonce = Convert.FromBase64String(request.ClientNonce);
        }
        catch (FormatException)
        {
            return false;
        }

        if (nonce.Length != 24)
        {
            return false;
        }

        var expected = QuickDrop.Core.Security.PairingSecurity.ComputePairProof(
            _session.SessionId,
            _session.Salt,
            _session.Code,
            nonce,
            request.Receiver.InstanceId);
        return QuickDrop.Core.Security.PairingSecurity.FixedTimeEqualsHex(expected, request.Proof);
    }

    private static void EnsureType(Protocol.IncomingControl control, string expected)
    {
        if (!string.Equals(control.Type, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected '{expected}', received '{control.Type}'.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _certificate.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}
