using System.Text.Json;
using System.Text;

namespace ShadowAgents;

/// <summary>Turns detection results into output records (design doc Section 10).</summary>
public class FindingRecordBuilder
{
    private readonly string host;
    private readonly ScanCache cache;
    private readonly DateTimeOffset scanTime;

    // One read of .git/config per project root, not one per finding.
    private readonly Dictionary<string, string> gitRemoteByRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Every id given out in this scan. The watcher's inventory is keyed by id, so two records with one
    // id would make it silently keep only the last one.
    private readonly HashSet<string> issuedIds = new HashSet<string>(StringComparer.Ordinal);

    public FindingRecordBuilder(string host, ScanCache cache, DateTimeOffset scanTime)
    {
        this.host = host;
        this.cache = cache;
        this.scanTime = scanTime;
    }

    public FindingRecord ForAgent(ProjectScanResult projectResult, DetectedAgent agent, string frameworkVersion)
    {
        ProjectRoot root = projectResult.Root;
        string relativeFile = RelativePath(root.RootPath, agent.Evidence.File);
        string id = FindingId.Compute(this.host, root.RootPath, agent.FrameworkKey, relativeFile, agent.Name);

        FindingRecord record = NewRecord(id, FindingStates.Implemented, agent.FrameworkKey, frameworkVersion);
        record.Confidence = agent.Confidence.ToString().ToLowerInvariant();
        record.Agent = DescribeAgent(agent);
        record.Project = DescribeProject(root, ProjectMetadata.ProjectName(root), SkipRules.IsTestPath(relativeFile));
        record.Evidence.Add(NewEvidence(relativeFile, agent.Evidence.Line, agent.Evidence.Snippet, agent.Evidence.Cell));
        return record;
    }

    /// <summary>
    /// An n8n agent: the workflow is the project (Section 7.2). An active workflow is Running.
    /// </summary>
    public FindingRecord ForN8nAgent(string n8nFolder, N8nAgent n8nAgent)
    {
        DetectedAgent agent = n8nAgent.Agent;
        string relativeFile = RelativePath(n8nFolder, agent.Evidence.File);
        string id = FindingId.Compute(this.host, n8nFolder, "n8n", n8nAgent.WorkflowName, agent.Name);

        string state = FindingStates.Implemented;
        if (n8nAgent.IsActive)
        {
            state = FindingStates.Running;
        }
        FindingRecord record = NewRecord(id, state, "n8n", null);
        record.Confidence = agent.Confidence.ToString().ToLowerInvariant();
        record.Agent = DescribeAgent(agent);
        record.Project = DescribeProject(new ProjectRoot { RootPath = n8nFolder }, n8nAgent.WorkflowName, isTest: false);
        record.Evidence.Add(NewEvidence(relativeFile, 0, agent.Evidence.Snippet, null));
        record.Runtime.Running = n8nAgent.IsActive;
        if (n8nAgent.IsActive)
        {
            record.Runtime.LastSeenRunning = this.scanTime;
        }

        return record;
    }

    public FindingRecord ForReferencedFramework(ProjectScanResult projectResult, string frameworkKey, string frameworkVersion)
    {
        ProjectRoot root = projectResult.Root;
        string id = FindingId.Compute(this.host, root.RootPath, frameworkKey, "", "");

        FindingRecord record = NewRecord(id, FindingStates.Referenced, frameworkKey, frameworkVersion);
        record.Project = DescribeProject(root, ProjectMetadata.ProjectName(root), isTest: false);
        foreach (ManifestInfo manifest in root.Manifests)
        {
            foreach (ManifestReference reference in manifest.References)
            {
                if (reference.FrameworkKey != frameworkKey)
                {
                    continue;
                }

                string snippet = reference.PackageName;
                if (reference.Version != null)
                {
                    snippet = reference.PackageName + " " + reference.Version;
                }
                record.Evidence.Add(NewEvidence(RelativePath(root.RootPath, manifest.FilePath), 0, snippet, null));
            }
        }

        return record;
    }

    public FindingRecord ForInstalledPackage(InstalledPackage package)
    {
        string id = FindingId.Compute(this.host, package.EnvironmentPath, package.FrameworkKey, "", "");
        FindingRecord record = NewRecord(id, FindingStates.Installed, package.FrameworkKey, package.Version);
        record.Environment = new EnvironmentRecord
        {
            Path = package.EnvironmentPath,
            Ecosystem = package.Ecosystem.ToString(),
        };

        string snippet = package.PackageName;
        if (package.Version != null)
        {
            snippet = package.PackageName + " " + package.Version;
        }
        record.Evidence.Add(NewEvidence(package.PackageName, 0, snippet, null));
        return record;
    }

    /// <summary>
    /// Returns <paramref name="id"/> the first time it is asked for, and a different, stable id each
    /// time after that, so no two records in one scan share an id.
    /// </summary>
    private string TakeUniqueId(string id)
    {
        if (issuedIds.Add(id))
        {
            return id;
        }

        int occurrence = 2;
        while (true)
        {
            string repeatId = FindingId.ForRepeat(id, occurrence);
            if (issuedIds.Add(repeatId))
            {
                return repeatId;
            }

            occurrence++;
        }
    }

    private FindingRecord NewRecord(string computedId, string state, string frameworkKey, string frameworkVersion)
    {
        // Made unique before the first-seen lookup below, so each record gets its own first-seen date.
        string id = TakeUniqueId(computedId);
        FrameworkInfo framework = FrameworkCatalog.Describe(frameworkKey);
        return new FindingRecord
        {
            Id = id,
            State = state,
            Framework = new FrameworkRecord
            {
                Key = framework.Key,
                Display = framework.DisplayName,
                Language = framework.Language.ToString().ToLowerInvariant(),
                Version = frameworkVersion,
            },
            FirstSeen = this.cache.GetOrRecordFirstSeen(id, this.scanTime),
            LastSeen = this.scanTime,
        };
    }

    private static AgentRecord DescribeAgent(DetectedAgent agent)
    {
        AgentRecord agentRecord = new AgentRecord
        {
            Name = agent.Name,
            Description = agent.Description,
            Tools = agent.Tools,
            Capabilities = agent.Capabilities,
            RiskFlags = agent.RiskFlags,
        };

        foreach (ModelReference model in agent.Models)
        {
            agentRecord.Models.Add(new ModelRecord { Id = model.Id, Provider = model.Provider, Source = model.Source });
        }

        return agentRecord;
    }

    private ProjectRecord DescribeProject(ProjectRoot root, string projectName, bool isTest)
    {
        if (!gitRemoteByRoot.TryGetValue(root.RootPath, out string gitRemote))
        {
            gitRemote = ProjectMetadata.GitRemote(root.RootPath);
            gitRemoteByRoot[root.RootPath] = gitRemote;
        }

        return new ProjectRecord
        {
            Root = root.RootPath,
            Name = projectName,
            Manifest = ProjectMetadata.MainManifestName(root),
            GitRemote = gitRemote,
            Owner = ProjectMetadata.Owner(root.RootPath),
            IsTest = isTest,
        };
    }

    private static EvidenceRecord NewEvidence(string file, int line, string snippet, Nullable<int> cell)
    {
        return new EvidenceRecord { File = file, Line = line, Snippet = snippet, Cell = cell };
    }

    /// <summary>Evidence paths are relative to the project root, with forward slashes ("src/crew.py").</summary>
    private static string RelativePath(string rootPath, string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        return PathHelpers.RelativeTo(rootPath, filePath).Replace('\\', '/');
    }
}

/// <summary>Project fields of a finding: name, main manifest, git remote and owner.</summary>
public static class ProjectMetadata
{
    private const int MaxParentFoldersToSearch = 32;

    public static string ProjectName(ProjectRoot root)
    {
        foreach (ManifestInfo manifest in root.Manifests)
        {
            if (!string.IsNullOrWhiteSpace(manifest.ProjectName))
            {
                return manifest.ProjectName;
            }
        }

        string folderName = Path.GetFileName(root.RootPath.TrimEnd(Path.DirectorySeparatorChar));
        if (folderName.Length > 0)
        {
            return folderName;
        }

        return root.RootPath;
    }

    /// <summary>The manifest that declares a framework, preferring the main project file over lock files and configs.</summary>
    public static string MainManifestName(ProjectRoot root)
    {
        ManifestInfo bestManifest = null;
        foreach (ManifestInfo manifest in root.Manifests)
        {
            if (!manifest.StartsProjectRoot)
            {
                continue;
            }

            bool isBetter = bestManifest == null
                || (manifest.References.Count > 0 && bestManifest.References.Count == 0)
                || (manifest.Kind != ManifestKind.PythonLockFile && bestManifest.Kind == ManifestKind.PythonLockFile);
            if (isBetter)
            {
                bestManifest = manifest;
            }
        }

        if (bestManifest == null)
        {
            return null;
        }

        return bestManifest.FileName;
    }

    /// <summary>
    /// The "origin" remote from the nearest enclosing repository, with any credentials removed
    /// (https://user:token@host/... becomes https://host/...).
    /// </summary>
    public static string GitRemote(string rootPath)
    {
        string gitConfigPath = FindGitConfig(rootPath);
        if (gitConfigPath == null)
        {
            return null;
        }

        string[] configLines;
        try
        {
            configLines = File.ReadAllLines(gitConfigPath);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }

        bool inOriginSection = false;
        foreach (string rawLine in configLines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inOriginSection = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inOriginSection && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                int equalsIndex = line.IndexOf('=');
                if (equalsIndex > 0)
                {
                    return RemoveCredentials(line.Substring(equalsIndex + 1).Trim());
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Owner from the user profile in the path (C:\Users\alice\... gives "alice").
    /// Open question 6 in the design doc: file owner versus profile path is still to be decided.
    /// </summary>
    public static string Owner(string rootPath)
    {
        // Only "X:\Users\<name>" directly under the drive root is a user profile.
        string[] segments = rootPath.Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        bool isUnderUserProfile = segments.Length >= 3 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase);
        if (isUnderUserProfile)
        {
            return segments[2];
        }

        return null;
    }

    private static string FindGitConfig(string rootPath)
    {
        string currentPath = rootPath;
        for (int depth = 0; depth < MaxParentFoldersToSearch && currentPath != null; depth++)
        {
            string gitConfigPath = Path.Combine(currentPath, ".git", "config");
            if (File.Exists(gitConfigPath))
            {
                return gitConfigPath;
            }

            currentPath = Path.GetDirectoryName(currentPath);
        }

        return null;
    }

    private static string RemoveCredentials(string remoteUrl)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri remoteUri) || string.IsNullOrEmpty(remoteUri.UserInfo))
        {
            return remoteUrl;
        }

        UriBuilder cleanUri = new UriBuilder(remoteUri) { UserName = "", Password = "" };
        return cleanUri.Uri.ToString();
    }
}

/// <summary>Writes records as JSON lines (one JSON object per line) to standard output or a results file.</summary>
public class FindingWriter : IDisposable
{
    private readonly TextWriter output;

    private FindingWriter(TextWriter output)
    {
        this.output = output;
    }

    public static FindingWriter Create(string outputPath)
    {
        UTF8Encoding utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return new FindingWriter(new StreamWriter(Console.OpenStandardOutput(), utf8WithoutBom));
        }

        string outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (outputFolder != null)
        {
            Directory.CreateDirectory(outputFolder);
        }

        return new FindingWriter(new StreamWriter(outputPath, append: false, utf8WithoutBom));
    }

    public void Write(FindingRecord record)
    {
        this.output.WriteLine(JsonSerializer.Serialize(record, ContractJson.LineOptions));
    }

    public void Write(SummaryRecord record)
    {
        this.output.WriteLine(JsonSerializer.Serialize(record, ContractJson.LineOptions));
    }

    /// <summary>Makes sure everything written so far has reached the screen or the file.</summary>
    public void Flush()
    {
        this.output.Flush();
    }

    public void Dispose()
    {
        this.output.Flush();
        this.output.Dispose();
    }
}
