using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuickDrop.Core.Abstractions;
using QuickDrop.Core.Files;
using QuickDrop.Core.Models;
using QuickDrop.Core.Security;
using QuickDrop.Network.Protocol;
using QuickDrop.Network.Security;

namespace QuickDrop.Network.Transfer;

public sealed class ReceiverService
{
    private readonly IRouteProvider _routeProvider;
    private readonly DeviceIdentity _identity;

    public ReceiverService(IRouteProvider routeProvider, DeviceIdentity? identity = null)
    {
        _routeProvider = routeProvider;
        _identity = identity ?? DeviceIdentity.Current;
    }

    public event Action<TransferProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;
    public event Action<DeviceIdentity>? SenderFound;

    public async Task<TransferResult> ReceiveAsync(
        string pairingCode,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        StatusChanged?.Invoke("Searching the local network");
        var route = await _routeProvider.FindAsync(pairingCode, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        SenderFound?.Invoke(route.Sender);
        StatusChanged?.Invoke($"Found {route.Sender.Name}; requesting confirmation");

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(route.Host, route.Port, cancellationToken).ConfigureAwait(false);
        await using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(EphemeralCertificate.CreateClientOptions(route.CertificateFingerprint), cancellationToken).ConfigureAwait(false);

        var nonce = RandomNumberGenerator.GetBytes(24);
        var proof = PairingSecurity.ComputePairProof(route.SessionId, route.Salt, pairingCode, nonce, _identity.InstanceId);
        await FrameProtocol.WriteControlAsync(
            ssl,
            MessageTypes.PairRequest,
            new PairRequestMessage(
                FrameProtocol.ProtocolVersion,
                route.SessionId,
                _identity,
                Convert.ToBase64String(nonce),
                proof,
                new[] { "files", "folders", "resume", "sha256" }),
            cancellationToken).ConfigureAwait(false);

        var decisionControl = await FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
        EnsureType(decisionControl, MessageTypes.PairDecision);
        var decision = decisionControl.GetPayload<PairDecisionMessage>();
        if (!decision.Accepted)
        {
            throw new UnauthorizedAccessException(decision.Message);
        }

        StatusChanged?.Invoke("Device confirmed; preparing files");
        var manifestControl = await FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
        EnsureType(manifestControl, MessageTypes.Manifest);
        var manifest = manifestControl.GetPayload<ManifestMessage>().Manifest;
        ValidateManifest(manifest, destinationRoot);

        foreach (var directory in manifest.Entries.Where(entry => entry.IsDirectory))
        {
            Directory.CreateDirectory(SafePath.ResolveUnderRoot(destinationRoot, directory.RelativePath));
        }

        var offsets = BuildResumeMap(manifest, destinationRoot);
        await FrameProtocol.WriteControlAsync(
            ssl,
            MessageTypes.ResumeMap,
            new ResumeMapMessage(offsets),
            cancellationToken).ConfigureAwait(false);

        var countedByFile = offsets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        long logicalTransferred = offsets.Values.Sum();
        var tracker = new ProgressTracker(manifest.TotalBytes, logicalTransferred);
        var buffer = new byte[FrameProtocol.ChunkSize];

        while (true)
        {
            var control = await FrameProtocol.ReadControlAsync(ssl, cancellationToken).ConfigureAwait(false);
            if (string.Equals(control.Type, MessageTypes.TransferComplete, StringComparison.Ordinal))
            {
                var complete = control.GetPayload<TransferCompleteMessage>();
                ProgressChanged?.Invoke(tracker.Add(0, manifest.TotalBytes, string.Empty, "Completed"));
                StatusChanged?.Invoke(complete.Message);
                return new TransferResult(complete.Success, complete.Message, manifest.TotalBytes, RouteLabel(route.Kind));
            }

            EnsureType(control, MessageTypes.FileStart);
            var start = control.GetPayload<FileStartMessage>();
            var entry = manifest.Entries.SingleOrDefault(item => !item.IsDirectory && item.Id == start.FileId)
                ?? throw new InvalidDataException("Sender referenced a file outside the manifest.");
            if (entry.RelativePath != start.RelativePath || entry.Length != start.Length || start.Offset < 0 || start.Offset > entry.Length)
            {
                throw new InvalidDataException("Sender file metadata does not match the manifest.");
            }

            countedByFile.TryGetValue(entry.Id, out var alreadyCounted);
            if (start.Offset == 0 && alreadyCounted > 0)
            {
                logicalTransferred -= alreadyCounted;
                countedByFile[entry.Id] = 0;
            }
            else if (start.Offset != alreadyCounted)
            {
                throw new InvalidDataException("Sender selected an unexpected resume position.");
            }

            var received = await ReceiveFileAsync(
                ssl,
                destinationRoot,
                entry,
                start,
                buffer,
                bytes =>
                {
                    logicalTransferred += bytes;
                    countedByFile[entry.Id] = countedByFile.GetValueOrDefault(entry.Id) + bytes;
                    ProgressChanged?.Invoke(tracker.Add(bytes, logicalTransferred, entry.RelativePath));
                },
                cancellationToken).ConfigureAwait(false);

            if (!received.Success)
            {
                logicalTransferred -= countedByFile.GetValueOrDefault(entry.Id);
                countedByFile[entry.Id] = 0;
            }

            await FrameProtocol.WriteControlAsync(
                ssl,
                MessageTypes.FileAck,
                new FileAckMessage(entry.Id, received.Success, received.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Success, string Message)> ReceiveFileAsync(
        Stream source,
        string destinationRoot,
        TransferEntry entry,
        FileStartMessage start,
        byte[] buffer,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        var requestedPath = SafePath.ResolveUnderRoot(destinationRoot, entry.RelativePath);
        var partPath = requestedPath + ".qdpart";
        Directory.CreateDirectory(Path.GetDirectoryName(partPath) ?? destinationRoot);
        string actualHash;

        await using (var output = new FileStream(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (start.Offset == 0)
            {
                output.SetLength(0);
            }
            else if (output.Length != start.Offset)
            {
                throw new InvalidDataException("Local partial file length changed before resume.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output.Position = 0;
            long prefixRead = 0;
            while (prefixRead < start.Offset)
            {
                var read = await output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, start.Offset - prefixRead)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Partial file ended before the resume position.");
                }

                hash.AppendData(buffer, 0, read);
                prefixRead += read;
            }

            output.Position = start.Offset;
            long remaining = entry.Length - start.Offset;
            while (remaining > 0)
            {
                var header = await FrameProtocol.ReadHeaderAsync(source, cancellationToken).ConfigureAwait(false);
                if (header.Type != FrameType.Data || header.Length > remaining)
                {
                    throw new InvalidDataException("Invalid file data frame length.");
                }

                await FrameProtocol.ReadDataBodyAsync(source, header, output, hash, buffer, cancellationToken).ConfigureAwait(false);
                remaining -= header.Length;
                reportBytes(header.Length);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hash.GetHashAndReset());
        }

        var endControl = await FrameProtocol.ReadControlAsync(source, cancellationToken).ConfigureAwait(false);
        EnsureType(endControl, MessageTypes.FileEnd);
        var end = endControl.GetPayload<FileEndMessage>();
        var valid = end.FileId == entry.Id && PairingSecurity.FixedTimeEqualsHex(actualHash, end.Sha256);
        if (!valid)
        {
            File.Delete(partPath);
            return (false, "SHA-256 mismatch; requesting one automatic retry");
        }

        var finalPath = SafePath.GetUniqueFinalPath(requestedPath);
        File.Move(partPath, finalPath);
        File.SetLastWriteTimeUtc(finalPath, entry.LastWriteTimeUtc.UtcDateTime);
        return (true, "SHA-256 verified");
    }

    private static Dictionary<string, long> BuildResumeMap(TransferManifest manifest, string destinationRoot)
    {
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries.Where(item => !item.IsDirectory))
        {
            var partPath = SafePath.ResolveUnderRoot(destinationRoot, entry.RelativePath) + ".qdpart";
            var length = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (length < 0 || length > entry.Length)
            {
                using var reset = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                length = 0;
            }

            offsets[entry.Id] = length;
        }

        return offsets;
    }

    private static void ValidateManifest(TransferManifest manifest, string destinationRoot)
    {
        if (manifest.Entries.Count > 100_000 || manifest.Entries.Any(entry => entry.Length < 0))
        {
            throw new InvalidDataException("Transfer manifest exceeds safety limits.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            _ = SafePath.ResolveUnderRoot(destinationRoot, entry.RelativePath);
            if (!ids.Add(entry.Id))
            {
                throw new InvalidDataException("Transfer manifest contains duplicate file identifiers.");
            }
        }
    }

    private static string RouteLabel(TransferRouteKind kind) => kind switch
    {
        TransferRouteKind.LanDirect => "LAN direct",
        TransferRouteKind.InternetPeerToPeer => "Internet P2P",
        _ => "Relay"
    };

    private static void EnsureType(IncomingControl control, string expected)
    {
        if (!string.Equals(control.Type, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected '{expected}', received '{control.Type}'.");
        }
    }
}
