using QuickDrop.Core.Models;

namespace QuickDrop.Network.Protocol;

public static class MessageTypes
{
    public const string PairRequest = "pair-request";
    public const string PairDecision = "pair-decision";
    public const string Manifest = "manifest";
    public const string ResumeMap = "resume-map";
    public const string FileStart = "file-start";
    public const string FileEnd = "file-end";
    public const string FileAck = "file-ack";
    public const string TransferComplete = "transfer-complete";
    public const string Cancel = "cancel";
}

public sealed record PairRequestMessage(
    int ProtocolVersion,
    Guid SessionId,
    DeviceIdentity Receiver,
    string ClientNonce,
    string Proof,
    IReadOnlyList<string> Capabilities);

public sealed record PairDecisionMessage(bool Accepted, string Message);

public sealed record ManifestMessage(TransferManifest Manifest);

public sealed record ResumeMapMessage(IReadOnlyDictionary<string, long> Offsets);

public sealed record FileStartMessage(
    string FileId,
    string RelativePath,
    long Length,
    long Offset,
    DateTimeOffset LastWriteTimeUtc);

public sealed record FileEndMessage(string FileId, string Sha256);

public sealed record FileAckMessage(string FileId, bool Success, string Message);

public sealed record TransferCompleteMessage(bool Success, string Message);
