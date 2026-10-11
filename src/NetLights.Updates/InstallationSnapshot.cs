namespace NetLights.Updates;

/// <summary>A bounded copy of installed files, kept outside the installer target directory.</summary>
public sealed class InstallationSnapshot : IDisposable
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private const int MaxFiles = 100;
    private readonly string _installationDirectory;
    private readonly string _backupDirectory;
    private readonly string[] _relativeFiles;
    private readonly HashSet<string> _relativeDirectories;
    private bool _restorationFailed;

    private InstallationSnapshot(
        string installationDirectory,
        string backupDirectory,
        string[] relativeFiles,
        IEnumerable<string> relativeDirectories)
    {
        _installationDirectory = installationDirectory;
        _backupDirectory = backupDirectory;
        _relativeFiles = relativeFiles;
        _relativeDirectories = new HashSet<string>(relativeDirectories, StringComparer.OrdinalIgnoreCase);
    }

    public static InstallationSnapshot Create(string executablePath, string updateRoot)
    {
        string executable = Path.GetFullPath(executablePath);
        if (!UpdatePolicy.IsSafeRestartPath(executable))
            throw new InvalidOperationException("Installed application was not found.");
        string installationDirectory = Path.GetDirectoryName(executable)!;
        EnsureSafeDirectoryPath(installationDirectory);
        if (!File.Exists(Path.Combine(installationDirectory, "unins000.exe")))
            throw new InvalidOperationException("Installation cannot be identified.");

        string root = Path.GetFullPath(updateRoot);
        // Staging cleanup only owns GUID directories directly under updateRoot.
        string backupDirectory = Path.Combine(root, "rollback", Guid.NewGuid().ToString("N"));
        if (UpdatePolicy.IsInsideRoot(installationDirectory, backupDirectory))
            throw new InvalidDataException("Backup cannot be inside the installation.");
        string[] files = EnumerateRegularFiles(installationDirectory);
        if (files.Length is 0 or > MaxFiles)
            throw new InvalidDataException("Installation file count exceeds the backup limit.");
        string[] directories = EnumerateDirectories(installationDirectory);
        long total = 0;
        foreach (string relative in files)
        {
            total = checked(total + new FileInfo(Path.Combine(installationDirectory, relative)).Length);
            if (total > MaxBytes)
                throw new InvalidDataException("Installation size exceeds the backup limit.");
        }

        Directory.CreateDirectory(backupDirectory);
        try
        {
            foreach (string relative in files)
            {
                string destination = Path.Combine(backupDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(installationDirectory, relative), destination);
            }
            return new InstallationSnapshot(installationDirectory, backupDirectory, files, directories);
        }
        catch
        {
            Directory.Delete(backupDirectory, recursive: true);
            throw;
        }
    }

    public bool Restore()
    {
        try
        {
            EnsureSafeDirectoryPath(_installationDirectory);
            EnsureSafeDirectoryPath(_backupDirectory);
            var originalFiles = new HashSet<string>(_relativeFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string relative in EnumerateRegularFiles(_installationDirectory))
            {
                if (originalFiles.Contains(relative))
                    continue;

                string addedFile = Path.Combine(_installationDirectory, relative);
                EnsureSafeDirectoryPath(_installationDirectory);
                EnsureNoLinkedAncestor(_installationDirectory, addedFile);
                File.Delete(addedFile);
            }

            foreach (string relative in _relativeFiles)
            {
                string destination = Path.Combine(_installationDirectory, relative);
                string source = Path.Combine(_backupDirectory, relative);
                EnsureSafeDirectoryPath(_installationDirectory);
                EnsureNoLinkedAncestor(_installationDirectory, destination);
                EnsureSafeDirectoryPath(_backupDirectory);
                EnsureNoLinkedAncestor(_backupDirectory, source);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            EnsureSafeDirectoryPath(_installationDirectory);
            RemoveEmptyDirectories(_installationDirectory, _relativeDirectories);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _restorationFailed = true;
            return false;
        }
    }

    private static string[] EnumerateDirectories(string root)
    {
        EnsureSafeDirectoryPath(root);
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            EnsureSafeDirectoryPath(root);
            foreach (string entry in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Installation contains a linked directory.");
                directories.Add(Path.GetRelativePath(root, entry));
                pending.Push(entry);
            }
        }
        return directories.ToArray();
    }

    private static void RemoveEmptyDirectories(string root, HashSet<string> originalDirectories)
    {
        EnsureSafeDirectoryPath(root);
        string[] directories = EnumerateDirectories(root)
            .Select(relative => Path.Combine(root, relative))
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (string directory in directories)
        {
            EnsureSafeDirectoryPath(root);
            EnsureNoLinkedAncestor(root, directory);
            string relative = Path.GetRelativePath(root, directory);
            if (!originalDirectories.Contains(relative) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    public void KeepForRecovery() => _restorationFailed = true;

    private static string[] EnumerateRegularFiles(string root)
    {
        EnsureSafeDirectoryPath(root);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            EnsureSafeDirectoryPath(root);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Installation contains a linked file or directory.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                {
                    files.Add(Path.GetRelativePath(root, entry));
                    if (files.Count > MaxFiles)
                        throw new InvalidDataException("Installation file count exceeds the backup limit.");
                }
            }
        }
        return files.ToArray();
    }

    private static void EnsureSafeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new IOException("Installation directory is missing.");

        DirectoryInfo? current = new(fullPath);
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation path contains a linked directory.");
            current = current.Parent;
        }
    }

    private static void EnsureNoLinkedAncestor(string root, string destination)
    {
        EnsureSafeDirectoryPath(root);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetFullPath(destination);
        while (current is not null && (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)
            || UpdatePolicy.IsInsideRoot(fullRoot, current)))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation target contains a linked path.");
            current = Path.GetDirectoryName(current);
        }
    }

    public void Dispose()
    {
        if (_restorationFailed) return;
        try
        {
            EnsureSafeDirectoryPath(_backupDirectory);
            string[] files = EnumerateRegularFiles(_backupDirectory);
            string[] directories = EnumerateDirectories(_backupDirectory)
                .Select(relative => Path.Combine(_backupDirectory, relative))
                .OrderByDescending(path => path.Length)
                .ToArray();
            foreach (string file in files)
            {
                string path = Path.Combine(_backupDirectory, file);
                EnsureSafeDirectoryPath(_backupDirectory);
                EnsureNoLinkedAncestor(_backupDirectory, path);
                File.Delete(path);
            }
            foreach (string directory in directories)
            {
                EnsureSafeDirectoryPath(_backupDirectory);
                EnsureNoLinkedAncestor(_backupDirectory, directory);
                Directory.Delete(directory);
            }
            EnsureSafeDirectoryPath(Path.GetDirectoryName(_backupDirectory)!);
            Directory.Delete(_backupDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Preserve the snapshot if its path cannot be proven safe to remove.
        }
    }
}
