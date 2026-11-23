// Every test owns one fresh directory. Cleanup never traverses a symbolic link
// or junction, and never removes a parent or a directory owned by another run.
internal sealed class TestDirectory : IDisposable
{
    private readonly string parent;
    private readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public string DirectoryPath { get; }
    private bool disposed;

    public TestDirectory()
    {
        parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local", "tmp"));
        RejectLinkedParents(parent);
        Directory.CreateDirectory(parent);
        RejectLinkedParents(parent);
        DirectoryPath = Path.GetFullPath(Path.Combine(parent, "tests-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(DirectoryPath);
    }

    public void Dispose()
    {
        if (disposed) return;
        // Resolve and verify the exact owned boundary before any recursive work.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(DirectoryPath)), parent, comparison))
            throw new IOException("Test cleanup boundary changed.");
        RejectLinkedParents(parent);
        DeleteOwnedEntry(DirectoryPath);
        disposed = true;
    }

    private void DeleteOwnedEntry(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(DirectoryPath, comparison) && !full.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Test cleanup refused a path outside its directory.");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            // Directory.Delete without recursion removes a directory link itself.
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) DeleteOwnedEntry(child);
            Directory.Delete(full);
        }
        else File.Delete(full);
    }

    private static void RejectLinkedParents(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Test directories cannot be created or cleaned through a link.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
