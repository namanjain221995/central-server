using EndpointAgent.Core.FileCollection;

namespace EndpointAgent.Core.Tests.FileCollection;

/// <summary>
/// The browse enumerator: it lists files and folders with sizes under a root, caps the
/// listing, and the selection predicate the collection uses to include only chosen entries.
/// </summary>
public sealed class FileTreeEnumeratorTests
{
    [Fact]
    public void Enumerate_lists_files_and_folders_with_sizes_under_the_root_key()
    {
        var temp = Directory.CreateTempSubdirectory("epp-enum-");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "a.txt"), "hello"); // 5 bytes
            Directory.CreateDirectory(Path.Combine(temp.FullName, "sub"));
            File.WriteAllText(Path.Combine(temp.FullName, "sub", "b.txt"), "world!"); // 6 bytes

            var result = FileTreeEnumerator.Enumerate([new ResolvedFolder("Desktop", temp.FullName)], maxEntries: 100);

            result.Truncated.ShouldBeFalse();
            var byPath = result.Entries.ToDictionary(e => e.Path);

            byPath["Desktop"].IsDirectory.ShouldBeTrue();
            byPath["Desktop/a.txt"].IsDirectory.ShouldBeFalse();
            byPath["Desktop/a.txt"].SizeBytes.ShouldBe(5);
            byPath["Desktop/sub"].IsDirectory.ShouldBeTrue();
            byPath["Desktop/sub/b.txt"].SizeBytes.ShouldBe(6);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Enumerate_stops_at_the_entry_cap_and_reports_truncation()
    {
        var temp = Directory.CreateTempSubdirectory("epp-enum-");
        try
        {
            for (var i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(temp.FullName, $"f{i}.txt"), "x");
            }

            // 1 root node + a couple of files, then it must cut off.
            var result = FileTreeEnumerator.Enumerate([new ResolvedFolder("Downloads", temp.FullName)], maxEntries: 3);

            result.Truncated.ShouldBeTrue();
            result.Entries.Count.ShouldBe(3);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    // No selection means the whole folder.
    [InlineData("Desktop/a.txt", null, true)]
    // Exact file match.
    [InlineData("Desktop/a.txt", "Desktop/a.txt", true)]
    // Under a selected folder.
    [InlineData("Desktop/sub/b.txt", "Desktop/sub", true)]
    // A sibling whose name only shares a prefix is NOT under the folder.
    [InlineData("Desktop/substitute.txt", "Desktop/sub", false)]
    // Not selected at all.
    [InlineData("Downloads/c.txt", "Desktop/a.txt", false)]
    public void IsSelected_covers_exact_files_and_folder_subtrees(string entry, string? selection, bool expected)
    {
        string[]? selected = selection is null ? null : [selection];
        FileTreeEnumerator.IsSelected(entry, selected).ShouldBe(expected);
    }
}
