using System.Runtime.Versioning;

namespace ShadowAgents;

/// <summary>A framework package found in an environment: the Installed state.</summary>
public class InstalledPackage
{
    public InstalledPackage(string frameworkKey, string packageName, string version, string environmentPath, PackageEcosystem ecosystem)
    {
        FrameworkKey = frameworkKey;
        PackageName = packageName;
        Version = version;
        EnvironmentPath = environmentPath;
        Ecosystem = ecosystem;
    }

    public string FrameworkKey { get; }
    public string PackageName { get; }
    public string Version { get; }
    public string EnvironmentPath { get; }
    public PackageEcosystem Ecosystem { get; }
}

/// <summary>
/// The output of Stage 0: candidate file paths, found by name without opening files.
/// Filled either by <see cref="DirectoryCandidateWalker"/> (folders, non-NTFS volumes) or by
/// <see cref="NtfsCandidateIndexer"/> (whole NTFS volumes, through the master file table).
/// </summary>
public class CandidateIndex
{
    public List<string> ManifestFiles { get; } = new List<string>();
    public List<string> SourceFiles { get; } = new List<string>();
    public List<InstalledPackage> InstalledPackages { get; } = new List<InstalledPackage>();

    /// <summary>Folders that contain a <c>.git</c> folder: repository roots.</summary>
    public HashSet<string> GitRoots { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local n8n databases (<c>.n8n\database.sqlite</c>).</summary>
    public List<string> N8nDatabases { get; } = new List<string>();

    public int DirectoriesVisited { get; set; }
    public int DirectoriesInaccessible { get; set; }

    /// <summary>Adds a file if its name makes it a candidate (manifest, source file or n8n database).</summary>
    public void AddFileIfCandidate(string directoryPath, string fileName)
    {
        string filePath = Path.Combine(directoryPath, fileName);
        if (CandidateFileNames.IsManifest(fileName))
        {
            ManifestFiles.Add(filePath);
        }

        if (CandidateFileNames.IsSourceFile(fileName))
        {
            SourceFiles.Add(filePath);
        }

        bool isN8nDatabase = fileName.Equals("database.sqlite", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(directoryPath).Equals(".n8n", StringComparison.OrdinalIgnoreCase);
        if (isN8nDatabase)
        {
            N8nDatabases.Add(filePath);
        }
    }

    /// <summary>
    /// For machine-wide scans (Section 6.1): keep a loose source file only when it is under a user profile
    /// (where shadow scripts and notebooks live) or inside a project (a manifest folder or git repository).
    /// This drops the many .py and .js files that ship inside installed tools.
    /// </summary>
    public void KeepOnlyProfileOrProjectSources()
    {
        HashSet<string> projectFolders = new HashSet<string>(GitRoots, StringComparer.OrdinalIgnoreCase);
        foreach (string manifestFile in ManifestFiles)
        {
            string manifestFolder = Path.GetDirectoryName(manifestFile);
            if (manifestFolder != null)
            {
                projectFolders.Add(manifestFolder);
            }
        }

        List<string> keptSources = new List<string>();
        foreach (string sourceFile in SourceFiles)
        {
            if (SkipRules.IsUnderUserProfile(sourceFile) || HasAncestorIn(sourceFile, projectFolders))
            {
                keptSources.Add(sourceFile);
            }
        }

        SourceFiles.Clear();
        SourceFiles.AddRange(keptSources);
    }

    /// <summary>Removes duplicates, which happen when a given root lies on a volume that is also scanned.</summary>
    public void RemoveDuplicates()
    {
        RemoveDuplicateStrings(ManifestFiles);
        RemoveDuplicateStrings(SourceFiles);
        RemoveDuplicateStrings(N8nDatabases);
    }

    private static bool HasAncestorIn(string filePath, HashSet<string> folders)
    {
        string folder = Path.GetDirectoryName(filePath);
        while (folder != null)
        {
            if (folders.Contains(folder))
            {
                return true;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return false;
    }

    private static void RemoveDuplicateStrings(List<string> paths)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> unique = new List<string>();
        foreach (string path in paths)
        {
            if (seen.Add(path))
            {
                unique.Add(path);
            }
        }

        paths.Clear();
        paths.AddRange(unique);
    }
}

/// <summary>Which file names are candidates (Section 6.1).</summary>
public static class CandidateFileNames
{
    private static readonly HashSet<string> ManifestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pyproject.toml", "Pipfile", "Pipfile.lock", "poetry.lock", "uv.lock", "setup.py", "setup.cfg",
        "environment.yml", "environment.yaml", "package.json", "packages.config", "Directory.Packages.props",
        "langgraph.json", "crew.yaml", "agents.yaml", "tasks.yaml",
    };

    public static bool IsManifest(string fileName)
    {
        if (ManifestNames.Contains(fileName))
        {
            return true;
        }

        bool isRequirementsFile = fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        return isRequirementsFile
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSourceFile(string fileName)
    {
        return SourceDetector.IsSupportedSourceFile(fileName);
    }

    /// <summary>A file the watcher should react to: a manifest or a source file.</summary>
    public static bool IsCandidate(string fileName)
    {
        return IsManifest(fileName) || IsSourceFile(fileName);
    }
}

/// <summary>
/// Stage 0 by walking folders (names only). Used for explicitly given folders, and for volumes the
/// master file table cannot cover: non-NTFS drives, or when the scanner is not running as administrator.
/// </summary>
public class DirectoryCandidateWalker
{
    /// <summary>Walks every folder below <paramref name="root"/> and adds what it finds to <paramref name="index"/>.</summary>
    public void WalkInto(string root, CandidateIndex index, CancellationToken cancellationToken)
    {
        Stack<string> pendingDirectories = new Stack<string>();
        pendingDirectories.Push(Path.GetFullPath(root));

        while (pendingDirectories.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            string directoryPath = pendingDirectories.Pop();
            VisitDirectory(directoryPath, index, pendingDirectories);
        }
    }

    private void VisitDirectory(string directoryPath, CandidateIndex index, Stack<string> pendingDirectories)
    {
        List<string> fileNames;
        List<string> subdirectoryNames;
        if (!TryListDirectory(directoryPath, out fileNames, out subdirectoryNames))
        {
            index.DirectoriesInaccessible++;
            return;
        }

        index.DirectoriesVisited++;

        if (IsPythonEnvironment(fileNames, subdirectoryNames))
        {
            ReadPythonEnvironment(directoryPath, subdirectoryNames, index, pendingDirectories);
            return;
        }

        if (ContainsName(subdirectoryNames, ".git"))
        {
            index.GitRoots.Add(directoryPath);
        }

        foreach (string fileName in fileNames)
        {
            index.AddFileIfCandidate(directoryPath, fileName);
        }

        foreach (string subdirectoryName in subdirectoryNames)
        {
            string subdirectoryPath = Path.Combine(directoryPath, subdirectoryName);
            if (SkipRules.IsDependencyFolderName(subdirectoryName))
            {
                InstalledPackageReader.ReadDependencyFolder(subdirectoryPath, subdirectoryName, index.InstalledPackages);
                continue;
            }

            if (!SkipRules.ShouldSkipDirectory(subdirectoryPath, subdirectoryName))
            {
                pendingDirectories.Push(subdirectoryPath);
            }
        }
    }

    /// <summary>
    /// A virtual environment (has <c>pyvenv.cfg</c>), a Python install (<c>python.exe</c> next to <c>Lib</c>)
    /// or a conda environment (has <c>conda-meta</c>). None of their files are project code.
    /// </summary>
    private static bool IsPythonEnvironment(List<string> fileNames, List<string> subdirectoryNames)
    {
        if (ContainsName(fileNames, "pyvenv.cfg") || ContainsName(subdirectoryNames, "conda-meta"))
        {
            return true;
        }

        return ContainsName(fileNames, "python.exe") && ContainsName(subdirectoryNames, "Lib");
    }

    private static void ReadPythonEnvironment(
        string environmentPath,
        List<string> subdirectoryNames,
        CandidateIndex index,
        Stack<string> pendingDirectories)
    {
        foreach (string sitePackagesPath in InstalledPackageReader.FindSitePackagesFolders(environmentPath))
        {
            InstalledPackageReader.ReadSitePackages(sitePackagesPath, index.InstalledPackages);
        }

        // A conda base install keeps its other environments under "envs". Visit those as environments too.
        if (!ContainsName(subdirectoryNames, "envs"))
        {
            return;
        }

        string environmentsPath = Path.Combine(environmentPath, "envs");
        if (TryListDirectory(environmentsPath, out List<string> environmentFileNames, out List<string> environmentNames))
        {
            foreach (string environmentName in environmentNames)
            {
                pendingDirectories.Push(Path.Combine(environmentsPath, environmentName));
            }
        }
    }

    private static bool ContainsName(List<string> names, string wantedName)
    {
        foreach (string name in names)
        {
            if (name.Equals(wantedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryListDirectory(string directoryPath, out List<string> fileNames, out List<string> subdirectoryNames)
    {
        fileNames = new List<string>();
        subdirectoryNames = new List<string>();
        try
        {
            foreach (FileSystemInfo entry in FolderListing.ListEntries(directoryPath))
            {
                if (entry is DirectoryInfo)
                {
                    subdirectoryNames.Add(entry.Name);
                }
                else
                {
                    fileNames.Add(entry.Name);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>
/// Stage 0 for a whole NTFS volume (Section 6.1): lists every file and folder name through the master
/// file table, without walking folders or opening files, and keeps only candidates.
/// </summary>
/// <remarks>
/// The master file table gives each entry a name, an id, and the id of its parent folder. We keep all
/// folders (id → parent, name) so we can rebuild full paths, plus only the files whose names matter.
/// Paths and skip decisions are computed lazily, and only for folders that hold a candidate.
/// </remarks>
public class NtfsCandidateIndexer
{
    // Per-volume state. Cleared at the start of each volume.
    private readonly Dictionary<ulong, FolderEntry> folders = new Dictionary<ulong, FolderEntry>();
    private readonly List<FolderEntry> interestingFiles = new List<FolderEntry>();
    private readonly Dictionary<ulong, string> folderPaths = new Dictionary<ulong, string>();
    private readonly Dictionary<ulong, bool> skippedFolders = new Dictionary<ulong, bool>();
    private readonly Dictionary<ulong, bool> foldersInEnvironment = new Dictionary<ulong, bool>();
    private readonly HashSet<ulong> pythonEnvironmentFolders = new HashSet<ulong>();
    private string volumeRootPath = "";

    /// <summary>A name and the id of the folder it is in. Used for both folders and files.</summary>
    private sealed class FolderEntry
    {
        public FolderEntry(ulong parentId, string name)
        {
            ParentId = parentId;
            Name = name;
        }

        public ulong ParentId { get; }
        public string Name { get; }
    }

    public void IndexVolume(NtfsVolume volume, CandidateIndex index, CancellationToken cancellationToken)
    {
        ClearVolumeState();
        volumeRootPath = volume.RootPath;

        volume.EnumerateAllEntries(Remember, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        FindPythonEnvironments();
        AddFolderCandidates(index);
        AddFileCandidates(index);
        index.DirectoriesVisited += this.folders.Count;

        // Release the memory before the next volume or the scanning stage.
        ClearVolumeState();
    }

    private void ClearVolumeState()
    {
        this.folders.Clear();
        interestingFiles.Clear();
        folderPaths.Clear();
        skippedFolders.Clear();
        foldersInEnvironment.Clear();
        pythonEnvironmentFolders.Clear();
    }

    private void Remember(UsnRecord record)
    {
        if (record.IsDirectory)
        {
            this.folders[record.FileReferenceNumber] = new FolderEntry(record.ParentFileReferenceNumber, record.FileName);
            return;
        }

        bool isInteresting = CandidateFileNames.IsCandidate(record.FileName)
            || record.FileName.Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase)
            || record.FileName.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || record.FileName.Equals("database.sqlite", StringComparison.OrdinalIgnoreCase);
        if (isInteresting)
        {
            interestingFiles.Add(new FolderEntry(record.ParentFileReferenceNumber, record.FileName));
        }
    }

    /// <summary>Python environments are folders with pyvenv.cfg, a conda-meta folder, or python.exe next to Lib.</summary>
    private void FindPythonEnvironments()
    {
        HashSet<ulong> foldersWithLib = new HashSet<ulong>();
        foreach (FolderEntry folder in this.folders.Values)
        {
            if (folder.Name.Equals("conda-meta", StringComparison.OrdinalIgnoreCase))
            {
                pythonEnvironmentFolders.Add(folder.ParentId);
            }
            else if (folder.Name.Equals("Lib", StringComparison.OrdinalIgnoreCase))
            {
                foldersWithLib.Add(folder.ParentId);
            }
        }

        foreach (FolderEntry file in interestingFiles)
        {
            bool isVirtualEnvironment = file.Name.Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase);
            bool isPythonInstall = file.Name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) && foldersWithLib.Contains(file.ParentId);
            if (isVirtualEnvironment || isPythonInstall)
            {
                pythonEnvironmentFolders.Add(file.ParentId);
            }
        }
    }

    /// <summary>Git repositories and dependency folders (for the Installed state) are found by folder name.</summary>
    private void AddFolderCandidates(CandidateIndex index)
    {
        foreach (KeyValuePair<ulong, FolderEntry> folder in this.folders)
        {
            string name = folder.Value.Name;
            bool isGitFolder = name.Equals(".git", StringComparison.OrdinalIgnoreCase);
            bool isDependencyFolder = SkipRules.IsDependencyFolderName(name);
            if (!isGitFolder && !isDependencyFolder)
            {
                continue;
            }

            // A node_modules inside another node_modules, or inside a skipped folder, does not count.
            if (IsSkippedFolder(folder.Value.ParentId))
            {
                continue;
            }

            if (isGitFolder)
            {
                string repositoryPath = GetFolderPath(folder.Value.ParentId);
                if (repositoryPath != null)
                {
                    index.GitRoots.Add(repositoryPath);
                }

                continue;
            }

            string dependencyFolderPath = GetFolderPath(folder.Key);
            if (dependencyFolderPath != null)
            {
                InstalledPackageReader.ReadDependencyFolder(dependencyFolderPath, name, index.InstalledPackages);
            }
        }
    }

    private void AddFileCandidates(CandidateIndex index)
    {
        foreach (FolderEntry file in interestingFiles)
        {
            if (IsSkippedFolder(file.ParentId) || IsInPythonEnvironment(file.ParentId))
            {
                continue;
            }

            string folderPath = GetFolderPath(file.ParentId);
            if (folderPath != null)
            {
                index.AddFileIfCandidate(folderPath, file.Name);
            }
        }
    }

    /// <summary>
    /// True when the folder, or any folder above it, is skipped by the rules or is a dependency folder.
    /// Results are remembered, so each folder is decided only once.
    /// </summary>
    private bool IsSkippedFolder(ulong folderId)
    {
        if (UsnRecord.IsVolumeRoot(folderId))
        {
            return false;
        }

        if (skippedFolders.TryGetValue(folderId, out bool rememberedAnswer))
        {
            return rememberedAnswer;
        }

        bool isSkipped;
        if (!this.folders.TryGetValue(folderId, out FolderEntry folder))
        {
            isSkipped = true; // not a normal folder (NTFS metadata or deleted): ignore
        }
        else if (SkipRules.IsDependencyFolderName(folder.Name) || IsSkippedFolder(folder.ParentId))
        {
            isSkipped = true;
        }
        else
        {
            string folderPath = GetFolderPath(folderId);
            isSkipped = folderPath == null || SkipRules.ShouldSkipDirectory(folderPath, folder.Name);
        }

        skippedFolders[folderId] = isSkipped;
        return isSkipped;
    }

    private bool IsInPythonEnvironment(ulong folderId)
    {
        if (UsnRecord.IsVolumeRoot(folderId))
        {
            return false;
        }

        if (foldersInEnvironment.TryGetValue(folderId, out bool rememberedAnswer))
        {
            return rememberedAnswer;
        }

        bool isInEnvironment;
        if (pythonEnvironmentFolders.Contains(folderId))
        {
            isInEnvironment = true;
        }
        else if (this.folders.TryGetValue(folderId, out FolderEntry folder))
        {
            isInEnvironment = IsInPythonEnvironment(folder.ParentId);
        }
        else
        {
            isInEnvironment = false;
        }

        foldersInEnvironment[folderId] = isInEnvironment;
        return isInEnvironment;
    }

    /// <summary>Rebuilds a folder's full path from the chain of parent ids, e.g. "C:\Users\alice\code".</summary>
    private string GetFolderPath(ulong folderId)
    {
        if (UsnRecord.IsVolumeRoot(folderId))
        {
            return volumeRootPath;
        }

        if (folderPaths.TryGetValue(folderId, out string rememberedPath))
        {
            return rememberedPath;
        }

        string path = null;
        if (this.folders.TryGetValue(folderId, out FolderEntry folder) && folder.ParentId != folderId)
        {
            string parentPath = GetFolderPath(folder.ParentId);
            if (parentPath != null)
            {
                path = Path.Combine(parentPath, folder.Name);
            }
        }

        folderPaths[folderId] = path;
        return path;
    }
}
