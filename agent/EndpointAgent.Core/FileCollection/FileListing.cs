namespace EndpointAgent.Core.FileCollection;

/// <summary>One entry in a browse listing: a file or folder under a chosen root.</summary>
/// <param name="Path">
/// Relative path including the root key, forward-slashed — e.g. "Desktop",
/// "Desktop/notes", "Downloads/report.pdf". The same form the collection's
/// SelectedPaths and the archive entry names use.
/// </param>
/// <param name="IsDirectory">True for a folder; folder sizes are summed by the browser from their files.</param>
/// <param name="SizeBytes">File size in bytes; 0 for a folder.</param>
public sealed record FileEntry(string Path, bool IsDirectory, long SizeBytes);

/// <summary>The listing the agent sends for a browse.</summary>
/// <param name="Truncated">True when the entry cap cut the listing short.</param>
/// <param name="Entries">Every file and folder found, flat; the browser builds the tree from the paths.</param>
public sealed record FileListingResult(bool Truncated, IReadOnlyList<FileEntry> Entries);

/// <summary>
/// Enumerates a Windows user's chosen folders into a flat, bounded list of file and
/// folder entries with sizes, for the operator to browse. In-process only (no shell,
/// ADR-0005); reparse points are skipped so a junction cannot walk out of the profile,
/// unreadable items are skipped, and enumeration stops at the entry cap.
/// </summary>
public static class FileTreeEnumerator
{
    public static FileListingResult Enumerate(IReadOnlyList<ResolvedFolder> folders, int maxEntries)
    {
        var entries = new List<FileEntry>();
        var truncated = false;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var folder in folders)
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            // The root itself, so it shows as a top-level, selectable node.
            entries.Add(new FileEntry(folder.Key, IsDirectory: true, SizeBytes: 0));

            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFileSystemEntries(folder.Path, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in found)
            {
                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    break;
                }

                bool isDir;
                long size = 0;
                try
                {
                    isDir = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
                    if (!isDir)
                    {
                        size = new FileInfo(path).Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var relative = System.IO.Path.GetRelativePath(folder.Path, path).Replace('\\', '/');
                entries.Add(new FileEntry($"{folder.Key}/{relative}", isDir, isDir ? 0 : size));
            }

            if (truncated)
            {
                break;
            }
        }

        return new FileListingResult(truncated, entries);
    }

    /// <summary>
    /// Whether an archive entry at <paramref name="entryPath"/> (e.g. "Desktop/a/b.txt")
    /// is covered by a selection: either it is selected exactly, or it sits under a
    /// selected folder. An empty selection covers everything.
    /// </summary>
    public static bool IsSelected(string entryPath, IReadOnlyCollection<string>? selected)
    {
        if (selected is null || selected.Count == 0)
        {
            return true;
        }

        foreach (var sel in selected)
        {
            if (string.Equals(entryPath, sel, StringComparison.OrdinalIgnoreCase)
                || entryPath.StartsWith(sel + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
