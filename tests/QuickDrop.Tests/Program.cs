using System.Security.Cryptography;
using QuickDrop.Core.Abstractions;
using QuickDrop.Core.Files;
using QuickDrop.Core.Models;
using QuickDrop.Core.Security;
using QuickDrop.Network.Discovery;
using QuickDrop.Network.Protocol;
using QuickDrop.Network.Transfer;

namespace QuickDrop.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Tests =
    [
        ("Pairing codes and proofs", TestPairingSecurityAsync),
        ("Safe destination paths", TestSafePathsAsync),
        ("File and folder manifest", TestManifestAsync),
        ("Framed protocol", TestFrameProtocolAsync),
        ("Progress calculations", TestProgressAsync),
        ("Per-interface directed broadcasts", TestDirectedBroadcastsAsync),
        ("LAN discovery", TestLanDiscoveryAsync),
        ("TLS transfer, SHA-256, folders, and resume", TestEndToEndResumeAsync),
        ("Integrity failure retries automatically", TestIntegrityRetryAsync)
    ];

    public static async Task<int> Main()
    {
        var failed = 0;
        Console.WriteLine($"QuickDrop test runner — {Tests.Count} tests\n");
        foreach (var test in Tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL  {test.Name}\n      {exception}");
            }
        }

        Console.WriteLine($"\nResult: {Tests.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestPairingSecurityAsync()
    {
        var codes = Enumerable.Range(0, 250).Select(_ => PairingSecurity.GenerateCode()).ToArray();
        Check.True(codes.All(code => code.Length == 6 && code.All(char.IsAsciiDigit)), "Codes must be six digits.");
        Check.True(codes.Distinct(StringComparer.Ordinal).Count() > 235, "Codes do not appear random enough.");

        var sessionId = Guid.NewGuid();
        var salt = PairingSecurity.CreateSalt();
        var nonce = RandomNumberGenerator.GetBytes(24);
        var proof = PairingSecurity.ComputePairProof(sessionId, salt, "123456", nonce, "receiver-a");
        var same = PairingSecurity.ComputePairProof(sessionId, salt, "123456", nonce, "receiver-a");
        var wrong = PairingSecurity.ComputePairProof(sessionId, salt, "654321", nonce, "receiver-a");
        Check.True(PairingSecurity.FixedTimeEqualsHex(proof, same), "Matching proofs should compare equal.");
        Check.False(PairingSecurity.FixedTimeEqualsHex(proof, wrong), "Wrong code must not validate.");
        return Task.CompletedTask;
    }

    private static Task TestSafePathsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuickDrop-SafePath-" + Guid.NewGuid().ToString("N"));
        var valid = SafePath.ResolveUnderRoot(root, "folder/file.bin");
        Check.True(valid.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "Valid path should stay under root.");
        Check.Throws<InvalidDataException>(() => SafePath.ResolveUnderRoot(root, "../escape.bin"));
        Check.Throws<InvalidDataException>(() => SafePath.ResolveUnderRoot(root, "C:/escape.bin"));
        Check.Throws<InvalidDataException>(() => SafePath.ResolveUnderRoot(root, "folder/../../escape.bin"));
        return Task.CompletedTask;
    }

    private static async Task TestManifestAsync()
    {
        var root = CreateTestRoot("manifest");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Course Project")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "empty"));
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "hello").ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(folder, "nested", "data.bin"), [1, 2, 3, 4]).ConfigureAwait(false);

            var manifest = await FileManifestBuilder.BuildAsync([folder]).ConfigureAwait(false);
            Check.Equal(5, manifest.Entries.Count, "Manifest should contain root, two child folders, and two files.");
            Check.Equal(9L, manifest.TotalBytes, "Manifest byte total is wrong.");
            Check.True(manifest.Entries.All(entry => !entry.RelativePath.Contains('\\')), "Wire paths must use forward slashes.");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static async Task TestFrameProtocolAsync()
    {
        await using var stream = new MemoryStream();
        await FrameProtocol.WriteControlAsync(stream, "sample", new FileAckMessage("id", true, "ok"), CancellationToken.None).ConfigureAwait(false);
        await FrameProtocol.WriteDataAsync(stream, new byte[] { 7, 8, 9 }, CancellationToken.None).ConfigureAwait(false);
        stream.Position = 0;

        var control = await FrameProtocol.ReadControlAsync(stream, CancellationToken.None).ConfigureAwait(false);
        Check.Equal("sample", control.Type, "Control type changed during framing.");
        Check.True(control.GetPayload<FileAckMessage>().Success, "Control payload changed during framing.");

        var header = await FrameProtocol.ReadHeaderAsync(stream, CancellationToken.None).ConfigureAwait(false);
        await using var copied = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await FrameProtocol.ReadDataBodyAsync(stream, header, copied, hash, new byte[32], CancellationToken.None).ConfigureAwait(false);
        Check.SequenceEqual(new byte[] { 7, 8, 9 }, copied.ToArray(), "Data payload changed during framing.");
    }

    private static Task TestProgressAsync()
    {
        var tracker = new ProgressTracker(1_000);
        var progress = tracker.Add(250, 250, "file.bin");
        Check.InRange(progress.Fraction, 0.249, 0.251, "Progress fraction is wrong.");
        Check.True(progress.BytesPerSecond > 0, "Speed must be positive after bytes are transferred.");
        Check.True(progress.EstimatedRemaining is not null, "ETA should be available after transfer begins.");
        return Task.CompletedTask;
    }

    private static Task TestDirectedBroadcastsAsync()
    {
        Check.Equal(
            "10.219.255.255",
            LanNetworkInterfaces.CalculateBroadcastAddress(System.Net.IPAddress.Parse("10.219.40.11"), 16).ToString(),
            "The Wi-Fi /16 directed broadcast is wrong.");
        Check.Equal(
            "192.168.11.255",
            LanNetworkInterfaces.CalculateBroadcastAddress(System.Net.IPAddress.Parse("192.168.11.1"), 24).ToString(),
            "The virtual adapter /24 directed broadcast is wrong.");
        Check.Equal(
            "10.20.30.40",
            LanNetworkInterfaces.CalculateBroadcastAddress(System.Net.IPAddress.Parse("10.20.30.40"), 32).ToString(),
            "A /32 address should remain unchanged.");
        Check.Throws<ArgumentOutOfRangeException>(() =>
            LanNetworkInterfaces.CalculateBroadcastAddress(System.Net.IPAddress.Parse("10.0.0.1"), 33));
        return Task.CompletedTask;
    }

    private static async Task TestLanDiscoveryAsync()
    {
        var root = CreateTestRoot("discovery");
        try
        {
            var file = Path.Combine(root, "tiny.txt");
            await File.WriteAllTextAsync(file, "discovery").ConfigureAwait(false);
            await using var sender = await SenderService.CreateAsync([file], new DeviceIdentity("TEST-SENDER", "Windows", "sender-discovery")).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var runTask = sender.RunAsync(cancellation.Token);
            _ = await sender.Ready.ConfigureAwait(false);
            var found = await new LanRouteProvider().FindAsync(sender.PairingCode, TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
            Check.Equal("TEST-SENDER", found.Sender.Name, "Discovery returned the wrong sender.");
            Check.Equal(TransferRouteKind.LanDirect, found.Kind, "Discovery returned the wrong route type.");
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => runTask).ConfigureAwait(false);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static async Task TestEndToEndResumeAsync()
    {
        var root = CreateTestRoot("e2e");
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "Virtual Machine Demo")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(sourceRoot, "disk")).FullName;
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty-folder"));
            var largeFile = Path.Combine(nested, "vm-image.bin");
            var noteFile = Path.Combine(sourceRoot, "README.txt");
            await WritePatternFileAsync(largeFile, 7 * 1024 * 1024 + 321).ConfigureAwait(false);
            await File.WriteAllTextAsync(noteFile, "QuickDrop integration test").ConfigureAwait(false);

            var destination = Directory.CreateDirectory(Path.Combine(root, "received")).FullName;
            await using var sender = await SenderService.CreateAsync(
                [sourceRoot],
                new DeviceIdentity("SENDER-LAPTOP", "Windows", "sender-e2e")).ConfigureAwait(false);
            sender.ConfirmDeviceAsync = (device, _) => Task.FromResult(device.Name == "RECEIVER-LAPTOP");

            var largeEntry = sender.Manifest.Entries.Single(entry => entry.RelativePath.EndsWith("vm-image.bin", StringComparison.Ordinal));
            var resumeBytes = 2 * 1024 * 1024 + 123;
            var partialPath = SafePath.ResolveUnderRoot(destination, largeEntry.RelativePath) + ".qdpart";
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
            await CopyPrefixAsync(largeFile, partialPath, resumeBytes).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var senderTask = sender.RunAsync(timeout.Token);
            var route = await sender.Ready.ConfigureAwait(false);
            var receiver = new ReceiverService(
                new StaticRouteProvider(route),
                new DeviceIdentity("RECEIVER-LAPTOP", "Windows", "receiver-e2e"));
            var progressEvents = new List<TransferProgress>();
            receiver.ProgressChanged += progressEvents.Add;

            TransferResult result;
            try
            {
                result = await receiver.ReceiveAsync(sender.PairingCode, destination, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception receiverException) when (senderTask.IsCompleted)
            {
                throw new AggregateException("Receiver failed after sender stopped.", receiverException, senderTask.Exception ?? new Exception("Sender stopped without an exception."));
            }
            var senderResult = await senderTask.ConfigureAwait(false);
            Check.True(result.Success && senderResult.Success, "End-to-end transfer did not complete.");

            var receivedLarge = Path.Combine(destination, new DirectoryInfo(sourceRoot).Name, "disk", "vm-image.bin");
            var receivedNote = Path.Combine(destination, new DirectoryInfo(sourceRoot).Name, "README.txt");
            Check.True(File.Exists(receivedLarge), "Nested large file was not received.");
            Check.True(File.Exists(receivedNote), "Text file was not received.");
            Check.True(Directory.Exists(Path.Combine(destination, new DirectoryInfo(sourceRoot).Name, "empty-folder")), "Empty folder was not preserved.");
            Check.Equal(await HashFileAsync(largeFile).ConfigureAwait(false), await HashFileAsync(receivedLarge).ConfigureAwait(false), "SHA-256 differs after transfer.");
            Check.Equal(await File.ReadAllTextAsync(noteFile).ConfigureAwait(false), await File.ReadAllTextAsync(receivedNote).ConfigureAwait(false), "Text file differs after transfer.");
            Check.Equal(sender.Manifest.TotalBytes - resumeBytes, sender.TotalNetworkBytesSent, "Sender did not honor the resume offset.");
            Check.True(progressEvents.Count > 0 && progressEvents[^1].Fraction == 1, "Receiver did not report completion progress.");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static async Task TestIntegrityRetryAsync()
    {
        var root = CreateTestRoot("retry");
        try
        {
            var source = Path.Combine(root, "retry.bin");
            await WritePatternFileAsync(source, 3 * 1024 * 1024 + 17).ConfigureAwait(false);
            var destination = Directory.CreateDirectory(Path.Combine(root, "received")).FullName;
            await using var sender = await SenderService.CreateAsync(
                [source],
                new DeviceIdentity("RETRY-SENDER", "Windows", "sender-retry")).ConfigureAwait(false);
            sender.ConfirmDeviceAsync = (_, _) => Task.FromResult(true);

            var entry = sender.Manifest.Entries.Single(item => !item.IsDirectory);
            var corruptBytes = 512 * 1024;
            var partialPath = SafePath.ResolveUnderRoot(destination, entry.RelativePath) + ".qdpart";
            await File.WriteAllBytesAsync(partialPath, new byte[corruptBytes]).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var senderTask = sender.RunAsync(timeout.Token);
            var route = await sender.Ready.ConfigureAwait(false);
            var receiver = new ReceiverService(new StaticRouteProvider(route));
            var result = await receiver.ReceiveAsync(sender.PairingCode, destination, timeout.Token).ConfigureAwait(false);
            await senderTask.ConfigureAwait(false);

            var received = Path.Combine(destination, Path.GetFileName(source));
            Check.True(result.Success && File.Exists(received), "Automatic retry did not produce the final file.");
            Check.Equal(await HashFileAsync(source).ConfigureAwait(false), await HashFileAsync(received).ConfigureAwait(false), "Retried file hash differs.");
            var expectedSent = (entry.Length - corruptBytes) + entry.Length;
            Check.Equal(expectedSent, sender.TotalNetworkBytesSent, "Integrity failure did not trigger exactly one full retry.");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static async Task WritePatternFileAsync(string path, int length)
    {
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)((i * 31 + 17) % 251);
        }

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
        var remaining = length;
        while (remaining > 0)
        {
            var count = Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static async Task CopyPrefixAsync(string source, string destination, int length)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static string CreateTestRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"QuickDrop-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("QuickDrop-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a path outside the QuickDrop test sandbox.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }
    }

    private sealed class StaticRouteProvider(RouteCandidate route) : IRouteProvider
    {
        public Task<RouteCandidate> FindAsync(string pairingCode, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(route);
        }
    }
}

internal static class Check
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void False(bool value, string message) => True(!value, message);

    public static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message);
    }

    public static void InRange(double value, double minimum, double maximum, string message)
    {
        if (value < minimum || value > maximum) throw new InvalidOperationException(message);
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
