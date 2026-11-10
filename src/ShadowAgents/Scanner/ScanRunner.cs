using System.Diagnostics;

namespace ShadowAgents;

/// <summary>Lets a scan be stopped from outside (Ctrl+C) and remembers why.</summary>
public class ScanStopSignal : IDisposable
{
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

    public CancellationToken Token
    {
        get { return cancellation.Token; }
    }

    public string Reason { get; private set; }

    public void Stop(string reason)
    {
        if (Reason == null)
        {
            Reason = reason;
        }

        cancellation.Cancel();
    }

    public void Dispose()
    {
        cancellation.Dispose();
    }
}

/// <summary>
/// Runs one scan job through the pipeline of Section 6:
/// Stage 0 list candidates, Stage 1 Installed, Stage 2 Referenced and project roots, Stage 3 Implemented.
/// Stage 4 (Running) is done by the watcher, except for n8n, whose "active" flag is read here.
/// </summary>
public class ScanRunner
{
    public const int ExitSuccess = 0;
    public const int ExitStoppedEarly = 3;

    private readonly ScanJob job;
    private readonly ScanStopSignal stopSignal;
    private readonly TextWriter log;
    private readonly ScanStatistics statistics = new ScanStatistics();

    // Code agents already written in this scan, by AgentLocationKey.
    private readonly HashSet<string> writtenAgentLocations = new HashSet<string>(StringComparer.Ordinal);

    public ScanRunner(ScanJob job, ScanStopSignal stopSignal, TextWriter log)
    {
        this.job = job;
        this.stopSignal = stopSignal;
        this.log = log;
    }

    public int Run(string resourceLimitsDescription)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = Stopwatch.StartNew();
        CancellationToken cancellationToken = this.stopSignal.Token;

        // Stage 0: candidate files, by name only.
        List<string> listingMethods = new List<string>();
        CandidateIndex candidateIndex = BuildCandidateIndex(listingMethods, cancellationToken);
        statistics.DirectoriesVisited = candidateIndex.DirectoriesVisited;
        statistics.DirectoriesInaccessible = candidateIndex.DirectoriesInaccessible;
        statistics.CandidateSourceFiles = candidateIndex.SourceFiles.Count;

        // Stage 2: manifests become project roots.
        List<ManifestInfo> manifests = ReadManifests(candidateIndex.ManifestFiles);
        List<ProjectRoot> projectRoots = ProjectRootResolver.Resolve(candidateIndex, manifests, Settings.MaxFilesPerRoot);
        statistics.ProjectRoots = projectRoots.Count;
        LogProgress("Found " + candidateIndex.SourceFiles.Count + " source files, " + manifests.Count + " manifests, " + projectRoots.Count + " project roots");

        string cachePath = this.job.CachePath;
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            cachePath = null; // in-memory cache for this run only
        }

        using ScanCache cache = ScanCache.Open(cachePath);
        using FindingWriter writer = FindingWriter.Create(this.job.OutputPath);
        FindingRecordBuilder recordBuilder = new FindingRecordBuilder(Environment.MachineName, cache, startedAt);

        // Stage 1: installed packages.
        WriteInstalledInventory(candidateIndex, recordBuilder, writer);

        // Stage 3: agents in each project root.
        ProjectScanner projectScanner = new ProjectScanner(cache, new SourceDetector(), statistics);

        foreach (ProjectRoot projectRoot in projectRoots)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            LogProgress("Scanning " + projectRoot.RootPath + " (" + projectRoot.SourceFiles.Count + " files)");
            if (projectRoot.SourceFileCapHit)
            {
                statistics.RootsOverFileCap++;
            }

            ProjectScanResult projectResult = projectScanner.Scan(projectRoot, cancellationToken);
            WriteProjectFindings(projectResult, candidateIndex.InstalledPackages, recordBuilder, writer);
        }

        WriteN8nAgents(candidateIndex, recordBuilder, writer);

        if (cancellationToken.IsCancellationRequested)
        {
            statistics.StoppedEarly = true;
            statistics.StopReason = this.stopSignal.Reason;
        }

        SummaryRecord summary = BuildSummary(startedAt, stopwatch.ElapsedMilliseconds, resourceLimitsDescription, listingMethods);
        writer.Write(summary);

        // Flush first, so the JSON lines appear on screen before the closing message below.
        writer.Flush();
        PrintClosingMessage(stopwatch.Elapsed);

        if (statistics.StoppedEarly)
        {
            return ExitStoppedEarly;
        }

        return ExitSuccess;
    }

    // ------------------------------------------------------------ Stage 0

    private CandidateIndex BuildCandidateIndex(List<string> listingMethods, CancellationToken cancellationToken)
    {
        DirectoryCandidateWalker walker = new DirectoryCandidateWalker();
        CandidateIndex index = new CandidateIndex();

        foreach (string root in this.job.Roots)
        {
            LogProgress("Listing candidate files under " + root);
            walker.WalkInto(root, index, cancellationToken);
            listingMethods.Add(root + ": folder-walk");
        }

        List<string> volumes = ResolveVolumes(this.job.Volumes);
        foreach (string volume in volumes)
        {
            LogProgress("Listing candidate files on volume " + volume);
            string listingMethod = IndexVolume(volume, walker, index, cancellationToken);
            listingMethods.Add(volume + ": " + listingMethod);
        }

        if (volumes.Count > 0)
        {
            index.KeepOnlyProfileOrProjectSources();
        }

        index.RemoveDuplicates();
        return index;
    }

    /// <summary>
    /// Uses the NTFS master file table when possible. Falls back to walking folders for non-NTFS
    /// volumes or when not running as administrator (Section 8, "Volumes the journal can't cover").
    /// </summary>
    private static string IndexVolume(string volume, DirectoryCandidateWalker walker, CandidateIndex index, CancellationToken cancellationToken)
    {
        string volumeRoot = volume.TrimEnd('\\') + "\\";

        NtfsVolume ntfsVolume = NtfsVolume.TryOpen(volume, out string problem);
        if (ntfsVolume == null)
        {
            walker.WalkInto(volumeRoot, index, cancellationToken);
            return "folder-walk (" + problem + ")";
        }

        using (ntfsVolume)
        {
            NtfsCandidateIndexer indexer = new NtfsCandidateIndexer();
            indexer.IndexVolume(ntfsVolume, index, cancellationToken);
        }

        return "master-file-table";
    }

    /// <summary>"auto" means every fixed drive. Removable and network drives are scanned only when named.</summary>
    public static List<string> ResolveVolumes(List<string> requestedVolumes)
    {
        List<string> volumes = new List<string>();
        foreach (string requested in requestedVolumes)
        {
            if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                AddVolume(volumes, requested.TrimEnd('\\', '/').ToUpperInvariant());
                continue;
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    AddVolume(volumes, drive.Name.TrimEnd('\\').ToUpperInvariant());
                }
            }
        }

        return volumes;
    }

    private static void AddVolume(List<string> volumes, string volume)
    {
        if (!volume.EndsWith(":", StringComparison.Ordinal))
        {
            volume = volume + ":";
        }

        if (!volumes.Contains(volume))
        {
            volumes.Add(volume);
        }
    }

    // ------------------------------------------------------------ Stages 1 and 2

    private List<ManifestInfo> ReadManifests(List<string> manifestFiles)
    {
        List<ManifestInfo> manifests = new List<ManifestInfo>();
        foreach (string manifestFile in manifestFiles)
        {
            FileInfo file = new FileInfo(manifestFile);
            FileReadOutcome readOutcome;
            string manifestText;
            try
            {
                readOutcome = SourceFileReader.TryRead(file, Settings.MaxFileBytes, out manifestText);
            }
            catch (FileNotFoundException)
            {
                continue; // deleted since it was listed
            }

            if (readOutcome != FileReadOutcome.Read)
            {
                continue;
            }

            statistics.ManifestsRead++;
            manifests.Add(ManifestReader.Read(manifestFile, manifestText));
        }

        return manifests;
    }

    /// <summary>
    /// One Installed record per framework per environment. When several packages of a framework are
    /// installed (langchain, langchain_core, ...), the one with the shortest name, usually the main
    /// package, gives the version.
    /// </summary>
    private void WriteInstalledInventory(CandidateIndex candidateIndex, FindingRecordBuilder recordBuilder, FindingWriter writer)
    {
        Dictionary<string, InstalledPackage> mainPackageByKey = new Dictionary<string, InstalledPackage>();
        foreach (InstalledPackage package in candidateIndex.InstalledPackages)
        {
            string key = package.EnvironmentPath.ToLowerInvariant() + "|" + package.FrameworkKey;
            bool isMainPackage = !mainPackageByKey.TryGetValue(key, out InstalledPackage current)
                || package.PackageName.Length < current.PackageName.Length;
            if (isMainPackage)
            {
                mainPackageByKey[key] = package;
            }
        }

        foreach (InstalledPackage package in mainPackageByKey.Values)
        {
            writer.Write(recordBuilder.ForInstalledPackage(package));
            statistics.InventoryRecords++;
        }

        // A local n8n install is an installed agent platform.
        foreach (string n8nDatabase in candidateIndex.N8nDatabases)
        {
            string n8nFolder = PathHelpers.FolderOf(n8nDatabase);
            writer.Write(recordBuilder.ForInstalledPackage(new InstalledPackage("n8n", "n8n", null, n8nFolder, PackageEcosystem.Npm)));
            statistics.InventoryRecords++;
        }
    }

    // ------------------------------------------------------------ Stage 3 output

    private void WriteProjectFindings(
        ProjectScanResult projectResult,
        List<InstalledPackage> installedPackages,
        FindingRecordBuilder recordBuilder,
        FindingWriter writer)
    {
        foreach (string frameworkKey in projectResult.ReferencedFrameworks)
        {
            string version = FindFrameworkVersion(projectResult.Root, frameworkKey, installedPackages);
            writer.Write(recordBuilder.ForReferencedFramework(projectResult, frameworkKey, version));
            statistics.InventoryRecords++;
        }

        foreach (DetectedAgent agent in projectResult.Agents)
        {
            if (!writtenAgentLocations.Add(AgentLocationKey(projectResult.Root, agent)))
            {
                continue; // the same agent at the same place was already written
            }

            string version = FindFrameworkVersion(projectResult.Root, agent.FrameworkKey, installedPackages);
            writer.Write(recordBuilder.ForAgent(projectResult, agent, version));
            statistics.AgentsFound++;
        }
    }

    /// <summary>
    /// What makes a code agent the same agent: its project, framework, name and exact place in a file.
    /// Two different agents that only share a name are kept (they get different ids from the record builder).
    /// </summary>
    private static string AgentLocationKey(ProjectRoot root, DetectedAgent agent)
    {
        Evidence evidence = agent.Evidence;
        return string.Join("|", root.RootPath, agent.FrameworkKey, agent.Name, evidence.File, evidence.Line, evidence.Cell).ToLowerInvariant();
    }

    private void WriteN8nAgents(CandidateIndex candidateIndex, FindingRecordBuilder recordBuilder, FindingWriter writer)
    {
        foreach (string n8nDatabase in candidateIndex.N8nDatabases)
        {
            LogProgress("Reading n8n workflows from " + n8nDatabase);
            string n8nFolder = PathHelpers.FolderOf(n8nDatabase);
            foreach (N8nAgent n8nAgent in N8nDatabaseReader.ReadAgents(n8nDatabase))
            {
                writer.Write(recordBuilder.ForN8nAgent(n8nFolder, n8nAgent));
                statistics.AgentsFound++;
            }
        }
    }

    /// <summary>The version declared in the project's manifests, or else the one installed in an environment inside the project.</summary>
    private static string FindFrameworkVersion(ProjectRoot root, string frameworkKey, List<InstalledPackage> installedPackages)
    {
        foreach (ManifestInfo manifest in root.Manifests)
        {
            foreach (ManifestReference reference in manifest.References)
            {
                if (reference.FrameworkKey == frameworkKey && reference.Version != null)
                {
                    return reference.Version;
                }
            }
        }

        string rootPrefix = root.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (InstalledPackage package in installedPackages)
        {
            bool isInsideProject = package.EnvironmentPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            if (isInsideProject && package.FrameworkKey == frameworkKey && package.Version != null)
            {
                return package.Version;
            }
        }

        return null;
    }

    private SummaryRecord BuildSummary(DateTimeOffset startedAt, long durationMs, string resourceLimitsDescription, List<string> listingMethods)
    {
        return new SummaryRecord
        {
            Host = Environment.MachineName,
            Roots = this.job.Roots,
            Volumes = this.job.Volumes,
            ListingMethods = listingMethods,
            StartedAt = startedAt,
            DurationMs = durationMs,
            DirectoriesVisited = statistics.DirectoriesVisited,
            DirectoriesInaccessible = statistics.DirectoriesInaccessible,
            ManifestsRead = statistics.ManifestsRead,
            CandidateSourceFiles = statistics.CandidateSourceFiles,
            ProjectRoots = statistics.ProjectRoots,
            RootsOverFileCap = statistics.RootsOverFileCap,
            FilesRead = statistics.FilesRead,
            BytesRead = statistics.BytesRead,
            FilesFromCache = statistics.FilesFromCache,
            FilesSkippedTooLarge = statistics.FilesSkippedTooLarge,
            FilesSkippedCloudPlaceholder = statistics.FilesSkippedCloudPlaceholder,
            FilesUnreadable = statistics.FilesUnreadable,
            FilesWithDetectionCap = statistics.FilesWithDetectionCap,
            AgentsFound = statistics.AgentsFound,
            InventoryRecords = statistics.InventoryRecords,
            StoppedEarly = statistics.StoppedEarly,
            StopReason = statistics.StopReason,
            ResourceLimits = resourceLimitsDescription,
        };
    }

    /// <summary>
    /// Always printed at the end of a scan (on standard error, so it never mixes into the JSON output):
    /// how long it took, what it found, and where the results are.
    /// </summary>
    private void PrintClosingMessage(TimeSpan timeTaken)
    {
        this.log.WriteLine();
        this.log.WriteLine("Scan finished in " + FormatDuration(timeTaken) + ".");
        this.log.WriteLine("  Agents found:      " + statistics.AgentsFound);
        this.log.WriteLine("  Other findings:    " + statistics.InventoryRecords + " (installed or referenced frameworks)");
        this.log.WriteLine("  Files read:        " + statistics.FilesRead + " (" + statistics.FilesFromCache + " more were unchanged and came from the cache)");

        if (statistics.StoppedEarly)
        {
            this.log.WriteLine("  Stopped early:     " + statistics.StopReason + " (results are partial)");
        }

        if (string.IsNullOrWhiteSpace(this.job.OutputPath))
        {
            this.log.WriteLine("  Results:           printed above as JSON lines");
        }
        else
        {
            this.log.WriteLine("  Results saved to:  " + Path.GetFullPath(this.job.OutputPath));
        }
    }

    /// <summary>"0.4 seconds", "12.3 seconds" or "3 min 05 s".</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return duration.TotalSeconds.ToString("F1") + " seconds";
        }

        return (int)duration.TotalMinutes + " min " + duration.Seconds.ToString("00") + " s";
    }

    private void LogProgress(string message)
    {
        this.log.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
    }
}
