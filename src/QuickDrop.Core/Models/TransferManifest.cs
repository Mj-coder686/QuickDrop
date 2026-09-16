using System.Text.Json.Serialization;

namespace QuickDrop.Core.Models;

public sealed record TransferManifest(
    Guid TransferId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TransferEntry> Entries)
{
    public long TotalBytes => Entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);
}

public sealed record TransferEntry(
    string Id,
    string RelativePath,
    long Length,
    bool IsDirectory,
    DateTimeOffset LastWriteTimeUtc,
    [property: JsonIgnore] string? SourcePath = null);
