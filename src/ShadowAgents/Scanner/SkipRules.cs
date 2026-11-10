namespace ShadowAgents;

/// <summary>
/// Folders that are never searched for agents (Section 6.6).
/// </summary>
/// <remarks>
/// Dependency folders (<c>node_modules</c>, <c>site-packages</c>, <c>.nuget</c>) and Python
/// environments (<c>.venv</c>, <c>venv</c>, conda) are handled separately: their package names are listed
/// for the Installed state, but nothing inside them is read.
/// </remarks>
public static class SkipRules
{
    private static readonly HashSet<string> SkippedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Build output, version control and tool caches (from the design doc).
        "__pycache__", ".git", ".hg", ".svn", ".tox", ".nox", "dist", "build", "target", "bin", "obj",
        ".next", ".nuxt", ".cache", ".turbo", ".idea", ".vs", ".vscode", ".pytest_cache", ".mypy_cache",
        ".ruff_cache", "$Recycle.Bin", "System Volume Information",

        // Package caches and editor extension folders in the user profile. They hold thousands of
        // JS and Python files that ship with tools, never hand-written agents.
        ".npm", ".yarn", ".pnpm-store", ".bun", ".cargo", ".rustup", ".gradle", ".m2", ".android",
        ".vscode-insiders", ".vscode-server", ".cursor", ".windsurf", ".dotnet", ".pyenv",
    };

    // Per-user installed applications and app data, the user-profile version of "Program Files".
    private static readonly string[] SkippedPathEndings =
    {
        @"\AppData\Local\Temp",
        @"\AppData\Local\Programs",
        @"\AppData\Local\Microsoft",
        @"\AppData\Local\Packages",
        @"\AppData\Local\Google",
        @"\AppData\Local\JetBrains",
        @"\AppData\Roaming\Microsoft",
        @"\AppData\Roaming\Code",
        @"\AppData\Roaming\Cursor",
    };

    private static readonly HashSet<string> DependencyFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "site-packages", "dist-packages", ".nuget",
    };

    public static bool ShouldSkipDirectory(string directoryPath, string directoryName)
    {
        if (SkippedFolderNames.Contains(directoryName))
        {
            return true;
        }

        // Installed applications are a separate discovery stream.
        if (IsDirectlyUnderDriveRoot(directoryPath)
            && (directoryName.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || directoryName.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string pathEnding in SkippedPathEndings)
        {
            if (directoryPath.EndsWith(pathEnding, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return IsEditorLocalHistory(directoryPath);
    }

    /// <summary>
    /// VS Code-based editors (Antigravity, Windsurf, Trae, VSCodium, ...) keep a copy of every saved
    /// version of a file in %APPDATA%\&lt;editor&gt;\User\History, under random names like "4A1q.py".
    /// Scanning it reports the same agent many times over as separate, unnamed projects.
    /// </summary>
    private static bool IsEditorLocalHistory(string directoryPath)
    {
        return directoryPath.EndsWith(@"\User\History", StringComparison.OrdinalIgnoreCase)
            && directoryPath.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when any folder on the path of <paramref name="filePath"/> would be skipped, or is a dependency
    /// folder. Used by the watcher to ignore changes it does not care about.
    /// </summary>
    public static bool IsInSkippedFolder(string filePath)
    {
        string folderPath = Path.GetDirectoryName(filePath);
        while (folderPath != null)
        {
            string folderName = Path.GetFileName(folderPath);
            if (folderName.Length == 0)
            {
                break; // reached the drive root
            }

            if (IsDependencyFolderName(folderName) || ShouldSkipDirectory(folderPath, folderName))
            {
                return true;
            }

            folderPath = Path.GetDirectoryName(folderPath);
        }

        return false;
    }

    public static bool IsDependencyFolderName(string folderName)
    {
        return DependencyFolderNames.Contains(folderName);
    }

    /// <summary>True for paths like C:\Users\alice\..., but not C:\Users\Public or C:\Users\Default.</summary>
    public static bool IsUnderUserProfile(string path)
    {
        string[] segments = path.Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string userName = segments[2];
        return !userName.Equals("Public", StringComparison.OrdinalIgnoreCase)
            && !userName.Equals("Default", StringComparison.OrdinalIgnoreCase)
            && !userName.Equals("All Users", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test code is still scanned but flagged (Section 6.6): it shows someone is building agents.
    /// </summary>
    public static bool IsTestPath(string relativePath)
    {
        string[] segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("test", StringComparison.OrdinalIgnoreCase)
                || segments[index].Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segments[index].Equals("__tests__", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (segments.Length == 0)
        {
            return false;
        }

        string fileName = segments[segments.Length - 1];
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.EndsWith("_test", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.EndsWith(".spec", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.EndsWith("Tests", StringComparison.Ordinal);
    }

    private static bool IsDirectlyUnderDriveRoot(string directoryPath)
    {
        string parentPath = Path.GetDirectoryName(directoryPath);
        return parentPath != null && Path.GetPathRoot(directoryPath) == parentPath;
    }
}

/// <summary>Lists the entries directly inside one folder.</summary>
public static class FolderListing
{
    /// <summary>
    /// Files and folders directly inside <paramref name="folderPath"/>. Junctions and symbolic links are left
    /// out: following them causes loops and double counting. Hidden entries are kept (".git" is often hidden).
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the folder cannot be read.
    /// </summary>
    public static List<FileSystemInfo> ListEntries(string folderPath)
    {
        List<FileSystemInfo> entries = new List<FileSystemInfo>();
        foreach (FileSystemInfo entry in new DirectoryInfo(folderPath).EnumerateFileSystemInfos())
        {
            bool isLinkOrDevice = (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0;
            if (!isLinkOrDevice)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>The folders directly inside <paramref name="folderPath"/>, without junctions and symbolic links.</summary>
    public static List<DirectoryInfo> ListFolders(string folderPath)
    {
        List<DirectoryInfo> folders = new List<DirectoryInfo>();
        foreach (FileSystemInfo entry in ListEntries(folderPath))
        {
            DirectoryInfo folder = entry as DirectoryInfo;
            if (folder != null)
            {
                folders.Add(folder);
            }
        }

        return folders;
    }
}
