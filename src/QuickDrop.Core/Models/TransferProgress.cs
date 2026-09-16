namespace QuickDrop.Core.Models;

public sealed record TransferProgress(
    long TransferredBytes,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    string CurrentFile,
    string State)
{
    public double Fraction => TotalBytes == 0 ? 1 : Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1);
}

public sealed class ProgressTracker
{
    private readonly long _totalBytes;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private long _transferredBytes;
    private long _measuredBytes;

    public ProgressTracker(long totalBytes, long initialBytes = 0)
    {
        _totalBytes = Math.Max(0, totalBytes);
        _transferredBytes = Math.Clamp(initialBytes, 0, _totalBytes);
    }

    public TransferProgress Add(long networkBytes, long logicalTransferred, string currentFile, string state = "Transferring")
    {
        _measuredBytes += Math.Max(0, networkBytes);
        _transferredBytes = Math.Clamp(logicalTransferred, 0, _totalBytes);
        var elapsed = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
        var speed = _measuredBytes / elapsed;
        TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds((_totalBytes - _transferredBytes) / speed) : null;
        return new TransferProgress(_transferredBytes, _totalBytes, speed, remaining, currentFile, state);
    }

    public TransferProgress Snapshot(string currentFile, string state) => Add(0, _transferredBytes, currentFile, state);
}
