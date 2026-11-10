namespace ShadowAgents;

/// <summary>A folder that holds one project: a manifest folder, a git repository root, or a loose folder of scripts.</summary>
public sealed class ProjectRoot
{
    public string RootPath { get; set; }
    public List<ManifestInfo> Manifests { get; } = new List<ManifestInfo>();
    public List<string> SourceFiles { get; } = new List<string>();
    public bool IsGitRepository { get; set; }

    /// <summary>True when the root holds more source files than the per-root cap; the rest were not scanned.</summary>
    public bool SourceFileCapHit { get; set; }

    /// <summary>True for a folder of scripts or notebooks with no manifest and no repository.</summary>
    public bool IsLoose
    {
        get { return Manifests.Count == 0 && !IsGitRepository; }
    }
}

/// <summary>
/// Stage 2: turns candidate files into project roots (Section 6.3).
/// </summary>
/// <remarks>
/// Every folder with a manifest, and every git repository root, is a project root. Nested manifest folders
/// stay separate projects (monorepos). Each source file belongs to its deepest enclosing project root;
/// files with no enclosing root are grouped by their own folder into loose roots.
/// </remarks>
public static class ProjectRootResolver
{
    public static List<ProjectRoot> Resolve(CandidateIndex index, IReadOnlyList<ManifestInfo> manifests, int maxFilesPerRoot)
    {
        Dictionary<string, ProjectRoot> projectRootsByPath = new Dictionary<string, ProjectRoot>(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestInfo manifest in manifests)
        {
            if (manifest.StartsProjectRoot)
            {
                GetOrAddRoot(projectRootsByPath, manifest.DirectoryPath).Manifests.Add(manifest);
            }
        }

        foreach (string gitRoot in index.GitRoots)
        {
            GetOrAddRoot(projectRootsByPath, gitRoot).IsGitRepository = true;
        }

        foreach (ManifestInfo manifest in manifests)
        {
            if (manifest.StartsProjectRoot)
            {
                continue;
            }

            ProjectRoot owningRoot = FindOwningRoot(projectRootsByPath, manifest.DirectoryPath);
            ProjectRoot rootForConfig = owningRoot;
            if (rootForConfig == null)
            {
                rootForConfig = GetOrAddRoot(projectRootsByPath, manifest.DirectoryPath);
            }
            rootForConfig.Manifests.Add(manifest);
        }

        // Loose roots are kept apart so that one loose folder never swallows its subfolders.
        Dictionary<string, ProjectRoot> looseRootsByPath = new Dictionary<string, ProjectRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceFile in index.SourceFiles)
        {
            string folderPath = PathHelpers.FolderOf(sourceFile);
            ProjectRoot owningRoot = FindOwningRoot(projectRootsByPath, folderPath);
            if (owningRoot == null)
            {
                owningRoot = GetOrAddRoot(looseRootsByPath, folderPath);
            }
            owningRoot.SourceFiles.Add(sourceFile);
        }

        List<ProjectRoot> allRoots = new List<ProjectRoot>(projectRootsByPath.Values);
        allRoots.AddRange(looseRootsByPath.Values);

        List<ProjectRoot> projectRoots = new List<ProjectRoot>();
        foreach (ProjectRoot projectRoot in allRoots)
        {
            DropTransitiveLockFileReferences(projectRoot);
            if (!IsWorthScanning(projectRoot))
            {
                continue;
            }

            ApplySourceFileCap(projectRoot, maxFilesPerRoot);
            projectRoots.Add(projectRoot);
        }

        projectRoots.Sort(CompareByPath);
        return projectRoots;
    }

    private static ProjectRoot GetOrAddRoot(Dictionary<string, ProjectRoot> rootsByPath, string rootPath)
    {
        if (!rootsByPath.TryGetValue(rootPath, out ProjectRoot projectRoot))
        {
            projectRoot = new ProjectRoot { RootPath = rootPath };
            rootsByPath.Add(rootPath, projectRoot);
        }

        return projectRoot;
    }

    /// <summary>Walks up from a folder to the nearest folder that is a project root.</summary>
    private static ProjectRoot FindOwningRoot(Dictionary<string, ProjectRoot> projectRootsByPath, string folderPath)
    {
        string currentPath = folderPath;
        while (currentPath != null)
        {
            if (projectRootsByPath.TryGetValue(currentPath, out ProjectRoot projectRoot))
            {
                return projectRoot;
            }

            currentPath = Path.GetDirectoryName(currentPath);
        }

        return null;
    }

    /// <summary>
    /// Lock files list transitive dependencies too. When the folder also has a direct manifest
    /// (pyproject.toml, requirements.txt, ...), trust only the direct one.
    /// </summary>
    private static void DropTransitiveLockFileReferences(ProjectRoot projectRoot)
    {
        bool hasDirectPythonManifest = false;
        foreach (ManifestInfo manifest in projectRoot.Manifests)
        {
            if (manifest.Kind == ManifestKind.PythonManifest)
            {
                hasDirectPythonManifest = true;
                break;
            }
        }

        if (!hasDirectPythonManifest)
        {
            return;
        }

        foreach (ManifestInfo manifest in projectRoot.Manifests)
        {
            if (manifest.Kind == ManifestKind.PythonLockFile)
            {
                manifest.References.Clear();
            }
        }
    }

    /// <summary>A root matters if it has source files to scan or declares an in-scope framework.</summary>
    private static bool IsWorthScanning(ProjectRoot projectRoot)
    {
        if (projectRoot.SourceFiles.Count > 0)
        {
            return true;
        }

        foreach (ManifestInfo manifest in projectRoot.Manifests)
        {
            if (manifest.References.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplySourceFileCap(ProjectRoot projectRoot, int maxFilesPerRoot)
    {
        projectRoot.SourceFiles.Sort(StringComparer.OrdinalIgnoreCase);
        if (projectRoot.SourceFiles.Count <= maxFilesPerRoot)
        {
            return;
        }

        projectRoot.SourceFiles.RemoveRange(maxFilesPerRoot, projectRoot.SourceFiles.Count - maxFilesPerRoot);
        projectRoot.SourceFileCapHit = true;
    }

    private static int CompareByPath(ProjectRoot first, ProjectRoot second)
    {
        return string.Compare(first.RootPath, second.RootPath, StringComparison.OrdinalIgnoreCase);
    }
}
