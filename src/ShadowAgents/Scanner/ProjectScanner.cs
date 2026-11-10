using System.Text;

namespace ShadowAgents;

/// <summary>Everything found in one project root.</summary>
public class ProjectScanResult
{
    public ProjectScanResult(ProjectRoot root)
    {
        Root = root;
    }

    public ProjectRoot Root { get; }
    public List<DetectedAgent> Agents { get; } = new List<DetectedAgent>();

    /// <summary>Framework keys referenced by a manifest or by an import in the project's own code.</summary>
    public SortedSet<string> ReferencedFrameworks { get; } = new SortedSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Stage 3 for one project root (Section 6.4): read each source file (or reuse its cached result),
/// detect agents, then merge results across the project's files.
/// </summary>
public class ProjectScanner
{
    private readonly ScanCache cache;
    private readonly SourceDetector detector;
    private readonly ScanStatistics statistics;

    public ProjectScanner(ScanCache cache, SourceDetector detector, ScanStatistics statistics)
    {
        this.cache = cache;
        this.detector = detector;
        this.statistics = statistics;
    }

    public ProjectScanResult Scan(ProjectRoot root, CancellationToken cancellationToken)
    {
        ProjectScanResult result = new ProjectScanResult(root);
        foreach (ManifestInfo manifest in root.Manifests)
        {
            foreach (ManifestReference reference in manifest.References)
            {
                result.ReferencedFrameworks.Add(reference.FrameworkKey);
            }
        }

        List<FileDetectionResult> fileResults = new List<FileDetectionResult>();
        foreach (string sourceFile in root.SourceFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            FileDetectionResult fileResult = DetectFile(sourceFile);
            if (fileResult != null)
            {
                fileResults.Add(fileResult);
            }
        }

        MergeFileResults(fileResults, result);
        AddCrewAiYamlProfiles(root, result);

        foreach (DetectedAgent agent in result.Agents)
        {
            CapabilityClassifier.Apply(agent);
            AgentConfidence.UpgradeIfAttributesFound(agent);
        }

        return result;
    }

    private FileDetectionResult DetectFile(string filePath)
    {
        FileInfo file = new FileInfo(filePath);
        long fileSize;
        long modifiedTicks;
        try
        {
            if (!file.Exists)
            {
                return null;
            }

            fileSize = file.Length;
            modifiedTicks = file.LastWriteTimeUtc.Ticks;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            this.statistics.FilesUnreadable++;
            return null;
        }

        // Unchanged since last time (same size and modified time): reuse the stored result, read nothing.
        if (this.cache.TryGetFileResult(filePath, fileSize, modifiedTicks, out FileDetectionResult cachedResult))
        {
            this.statistics.FilesFromCache++;
            return cachedResult;
        }

        FileReadOutcome readOutcome = SourceFileReader.TryRead(file, Settings.MaxFileBytes, out string sourceText);
        if (readOutcome == FileReadOutcome.TooLarge)
        {
            this.statistics.FilesSkippedTooLarge++;
            return null;
        }

        if (readOutcome == FileReadOutcome.CloudPlaceholder)
        {
            this.statistics.FilesSkippedCloudPlaceholder++;
            return null;
        }

        if (readOutcome == FileReadOutcome.Unreadable)
        {
            this.statistics.FilesUnreadable++;
            return null;
        }

        this.statistics.FilesRead++;
        this.statistics.BytesRead += fileSize;

        FileDetectionResult result = this.detector.Detect(sourceText, filePath);
        if (result.DetectionCapHit)
        {
            this.statistics.FilesWithDetectionCap++;
        }

        this.cache.StoreFileResult(filePath, fileSize, modifiedTicks, result);
        return result;
    }

    /// <summary>
    /// Section 6.4 step 5: tools defined in one file and models in another still belong to the same
    /// project. Agents whose model variable was not found in their own file are linked here.
    /// </summary>
    private static void MergeFileResults(List<FileDetectionResult> fileResults, ProjectScanResult result)
    {
        Dictionary<string, ModelReference> projectModelAssignments = new Dictionary<string, ModelReference>(StringComparer.Ordinal);
        foreach (FileDetectionResult fileResult in fileResults)
        {
            foreach (string frameworkKey in fileResult.ReferencedFrameworks)
            {
                result.ReferencedFrameworks.Add(frameworkKey);
            }

            foreach (KeyValuePair<string, ModelReference> assignment in fileResult.ModelAssignments)
            {
                if (!projectModelAssignments.ContainsKey(assignment.Key))
                {
                    projectModelAssignments.Add(assignment.Key, assignment.Value);
                }
            }

            result.Agents.AddRange(fileResult.Agents);
        }

        foreach (DetectedAgent agent in result.Agents)
        {
            if (agent.UnresolvedModelVariable == null || agent.Models.Count > 0)
            {
                continue;
            }

            if (projectModelAssignments.TryGetValue(agent.UnresolvedModelVariable, out ModelReference model))
            {
                agent.Models.Add(model);
                agent.UnresolvedModelVariable = null;
            }
        }
    }

    /// <summary>
    /// CrewAI scaffolds keep role, goal, backstory and llm in <c>config/agents.yaml</c>. Fill code agents
    /// named after the YAML key (the <c>@agent</c> method name), and report YAML-only agents too.
    /// </summary>
    private void AddCrewAiYamlProfiles(ProjectRoot root, ProjectScanResult result)
    {
        if (!result.ReferencedFrameworks.Contains("crewai"))
        {
            return;
        }

        foreach (ManifestInfo manifest in root.Manifests)
        {
            if (!manifest.FileName.Equals("agents.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string yamlText;
            try
            {
                yamlText = File.ReadAllText(manifest.FilePath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (CrewAiYamlAgent yamlAgent in CrewAiConfigReader.ReadAgents(yamlText))
            {
                ApplyYamlAgent(manifest.FilePath, yamlText, yamlAgent, result);
            }
        }
    }

    private void ApplyYamlAgent(string yamlPath, string yamlText, CrewAiYamlAgent yamlAgent, ProjectScanResult result)
    {
        List<string> parts = new List<string>();
        foreach (string part in new[] { yamlAgent.Role, yamlAgent.Goal, yamlAgent.Backstory })
        {
            if (part != null)
            {
                parts.Add(part);
            }
        }

        string description = SecretFilter.DropIfSecret(TextCleanup.CollapseWhitespace(string.Join(" ", parts), Settings.MaxDescriptionChars));

        ModelReference model = null;
        if (yamlAgent.Llm != null)
        {
            model = ModelProviders.FromModelString(yamlAgent.Llm, null);
        }

        DetectedAgent codeAgent = FindCrewAiAgentNamed(result.Agents, yamlAgent.Key);
        if (codeAgent != null)
        {
            if (codeAgent.Description == null || codeAgent.Description == "<dynamic>")
            {
                codeAgent.Description = description;
            }

            if (model != null && codeAgent.Models.Count == 0)
            {
                codeAgent.Models.Add(model);
            }

            return;
        }

        DetectedAgent yamlOnlyAgent = new DetectedAgent
        {
            FrameworkKey = "crewai",
            Name = yamlAgent.Key,
            Description = description,
            Confidence = Confidence.Medium,
            Evidence = new Evidence(yamlPath, FindKeyLine(yamlText, yamlAgent.Key), TextCleanup.MakeSnippet(yamlAgent.Key + ":")),
        };

        if (model != null)
        {
            yamlOnlyAgent.Models.Add(model);
        }

        result.Agents.Add(yamlOnlyAgent);
    }

    private static DetectedAgent FindCrewAiAgentNamed(List<DetectedAgent> agents, string agentName)
    {
        foreach (DetectedAgent agent in agents)
        {
            if (agent.FrameworkKey == "crewai" && agent.Name == agentName)
            {
                return agent;
            }
        }

        return null;
    }

    private static int FindKeyLine(string yamlText, string key)
    {
        string[] lines = yamlText.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lines[lineIndex].StartsWith(key + ":", StringComparison.Ordinal))
            {
                return lineIndex + 1;
            }
        }

        return 1;
    }
}

public enum FileReadOutcome
{
    Read,
    TooLarge,

    /// <summary>A cloud-only file (OneDrive Files On-Demand). Reading it would download it.</summary>
    CloudPlaceholder,

    /// <summary>Locked, deleted or access denied.</summary>
    Unreadable,
}

/// <summary>Reads source files without disturbing the user: shared access, size cap, no cloud downloads.</summary>
public static class SourceFileReader
{
    // Not named in the FileAttributes enum. Set on cloud files whose data is not on the disk.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static FileReadOutcome TryRead(FileInfo file, long maxFileBytes, out string text)
    {
        text = "";
        if (file.Length > maxFileBytes)
        {
            return FileReadOutcome.TooLarge;
        }

        if ((file.Attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0)
        {
            return FileReadOutcome.CloudPlaceholder;
        }

        try
        {
            // FileShare.ReadWrite | Delete: never block an editor or a running program that has the file open.
            using FileStream stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);

            // Invalid UTF-8 becomes U+FFFD instead of throwing.
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return FileReadOutcome.Read;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return FileReadOutcome.Unreadable;
        }
    }
}

/// <summary>Counters for the summary record: how much work a scan did and whether it stopped early.</summary>
public sealed class ScanStatistics
{
    public int DirectoriesVisited { get; set; }
    public int DirectoriesInaccessible { get; set; }
    public int CandidateSourceFiles { get; set; }
    public int ManifestsRead { get; set; }
    public int ProjectRoots { get; set; }
    public int RootsOverFileCap { get; set; }
    public int FilesRead { get; set; }
    public long BytesRead { get; set; }
    public int FilesFromCache { get; set; }
    public int FilesSkippedTooLarge { get; set; }
    public int FilesSkippedCloudPlaceholder { get; set; }
    public int FilesUnreadable { get; set; }
    public int FilesWithDetectionCap { get; set; }
    public int AgentsFound { get; set; }
    public int InventoryRecords { get; set; }
    public bool StoppedEarly { get; set; }
    public string StopReason { get; set; }
}
