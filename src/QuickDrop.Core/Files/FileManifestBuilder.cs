using System.Security.Cryptography;
using System.Text;
using QuickDrop.Core.Models;

namespace QuickDrop.Core.Files;

public static class FileManifestBuilder
{
    public static Task<TransferManifest> BuildAsync(IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Build(selectedPaths, cancellationToken), cancellationToken);
    }

    private static TransferManifest Build(IEnumerable<string> selectedPaths, CancellationToken cancellationToken)
    {
        var paths = selectedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("Select at least one file or folder.", nameof(selectedPaths));
        }

        var entries = new List<TransferEntry>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var rootName = UniqueRootName(Path.GetFileName(path), usedRoots);
                entries.Add(CreateFileEntry(path, rootName));
            }
            else if (Directory.Exists(path))
            {
                var rootName = UniqueRootName(new DirectoryInfo(path).Name, usedRoots);
                AddDirectory(path, rootName, entries, cancellationToken);
            }
            else
            {
                throw new FileNotFoundException("A selected item no longer exists.", path);
            }
        }

        return new TransferManifest(Guid.NewGuid(), DateTimeOffset.UtcNow, entries);
    }

    private static void AddDirectory(string rootPath, string rootName, List<TransferEntry> entries, CancellationToken cancellationToken)
    {
        var rootInfo = new DirectoryInfo(rootPath);
        entries.Add(CreateDirectoryEntry(rootName, rootInfo.LastWriteTimeUtc));

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = CombinePortable(rootName, Path.GetRelativePath(rootPath, directory));
            entries.Add(CreateDirectoryEntry(relative, Directory.GetLastWriteTimeUtc(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = CombinePortable(rootName, Path.GetRelativePath(rootPath, file));
            entries.Add(CreateFileEntry(file, relative));
        }
    }

    private static TransferEntry CreateFileEntry(string sourcePath, string relativePath)
    {
        var info = new FileInfo(sourcePath);
        return new TransferEntry(CreateId(relativePath), relativePath, info.Length, false, info.LastWriteTimeUtc, sourcePath);
    }

    private static TransferEntry CreateDirectoryEntry(string relativePath, DateTime lastWriteUtc) =>
        new(CreateId(relativePath + "/"), relativePath, 0, true, lastWriteUtc);

    private static string CreateId(string relativePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/')))).Substring(0, 24);

    private static string CombinePortable(string root, string relative) =>
        $"{root}/{relative.Replace('\\', '/')}";

    private static string UniqueRootName(string requested, HashSet<string> used)
    {
        var safeName = string.IsNullOrWhiteSpace(requested) ? "item" : requested;
        if (used.Add(safeName))
        {
            return safeName;
        }

        var extension = Path.GetExtension(safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
