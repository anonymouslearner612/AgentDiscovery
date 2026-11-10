using Cronos;

namespace ShadowAgents;

/// <summary>What the saved journal position means when the watcher starts.</summary>
public enum JournalStartStatus
{
    /// <summary>Continue from the saved position.</summary>
    Continue,

    /// <summary>No saved position, journal recreated, or journal wrapped: scan the volume again (baseline).</summary>
    BaselineNeeded,

    /// <summary>The volume has no active change journal. Only scheduled scans will cover it.</summary>
    NotAvailable,
}

/// <summary>
/// Reads one NTFS volume's change journal and turns changes into the paths of changed candidate files
/// (manifests and source files), ignoring everything in skipped folders (Section 5, "USN journal reader").
/// </summary>
public class JournalWatcher : IDisposable
{
    private const int MaxRecordsPerPoll = 20_000;
    private const int MaxRememberedFolders = 10_000;

    private readonly NtfsVolume volume;

    // Folder id → path. A journal record names only the file and its parent folder's id.
    private readonly Dictionary<ulong, string> folderPaths = new Dictionary<ulong, string>();

    private JournalWatcher(NtfsVolume volume)
    {
        this.volume = volume;
    }

    public string Drive
    {
        get { return this.volume.DriveLetter + ":"; }
    }

    public static JournalWatcher TryCreate(string drive, out string problem)
    {
        NtfsVolume volume = NtfsVolume.TryOpen(drive, out problem);
        if (volume == null)
        {
            return null;
        }

        return new JournalWatcher(volume);
    }

    /// <summary>
    /// Compares the saved position with the journal. When a baseline is needed, moves the saved position
    /// to "now", so changes made during the baseline scan are still picked up afterwards.
    /// </summary>
    public JournalStartStatus CheckSavedPosition(VolumeJournalState state)
    {
        UsnJournalInfo journal = this.volume.QueryJournal();
        if (journal == null)
        {
            return JournalStartStatus.NotAvailable;
        }

        bool hasSavedPosition = state.JournalId != 0;
        bool journalWasRecreated = state.JournalId != journal.JournalId;
        bool positionWasOverwritten = state.NextUsn < journal.FirstUsn;

        if (hasSavedPosition && !journalWasRecreated && !positionWasOverwritten)
        {
            return JournalStartStatus.Continue;
        }

        state.JournalId = journal.JournalId;
        state.NextUsn = journal.NextUsn;
        return JournalStartStatus.BaselineNeeded;
    }

    /// <summary>
    /// Reads new changes and returns the full paths of changed candidate files. Moves the saved position on.
    /// <paramref name="baselineNeeded"/> becomes true when the journal was reset or wrapped meanwhile.
    /// </summary>
    public List<string> ReadChangedCandidateFiles(VolumeJournalState state, out bool baselineNeeded)
    {
        baselineNeeded = false;
        List<UsnRecord> records = this.volume.ReadChanges(state.JournalId, state.NextUsn, MaxRecordsPerPoll, out long nextUsn, out JournalReadProblem problem);
        if (problem != JournalReadProblem.None)
        {
            // The saved position is useless now. Restart from the journal's current end and rescan the volume.
            baselineNeeded = true;
            UsnJournalInfo journal = this.volume.QueryJournal();
            if (journal != null)
            {
                state.JournalId = journal.JournalId;
                state.NextUsn = journal.NextUsn;
            }

            return new List<string>();
        }

        state.NextUsn = nextUsn;

        HashSet<string> changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UsnRecord record in records)
        {
            bool mightMatter = CandidateFileNames.IsCandidate(record.FileName) || McpClientConfigs.MightBeConfigFileName(record.FileName);
            if (record.IsDirectory || !mightMatter)
            {
                continue; // most changes are to files we do not care about: drop them before any file system call
            }

            string folderPath = GetFolderPath(record.ParentFileReferenceNumber);
            if (folderPath == null)
            {
                continue; // the folder no longer exists; the scheduled full scan will notice
            }

            string filePath = Path.Combine(folderPath, record.FileName);

            // MCP client configurations sit in AppData, .cursor, .vscode, ...: folders that are skipped for code.
            bool isMcpConfig = McpClientConfigs.Identify(filePath) != null;
            if (isMcpConfig || !SkipRules.IsInSkippedFolder(filePath))
            {
                changedFiles.Add(filePath);
            }
        }

        return new List<string>(changedFiles);
    }

    public void Dispose()
    {
        this.volume.Dispose();
    }

    private string GetFolderPath(ulong folderId)
    {
        if (folderPaths.TryGetValue(folderId, out string rememberedPath))
        {
            return rememberedPath;
        }

        if (folderPaths.Count >= MaxRememberedFolders)
        {
            folderPaths.Clear(); // keep memory flat; paths are cheap to look up again
        }

        string path = this.volume.GetPathById(folderId);
        folderPaths[folderId] = path;
        return path;
    }
}

/// <summary>
/// Groups file changes per project root and waits until a root has been quiet for the debounce time
/// before asking for a scan (Section 5, "Change batcher"). A git checkout or an editor's save storm
/// then causes one scan instead of hundreds.
/// </summary>
public class ChangeBatcher
{
    private readonly Dictionary<string, DateTimeOffset> lastChangeByRoot = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    public int PendingCount
    {
        get { return lastChangeByRoot.Count; }
    }

    /// <summary>Records a change in <paramref name="projectRoot"/>. Each new change restarts that root's wait.</summary>
    public void AddChange(string projectRoot, DateTimeOffset changeTime)
    {
        lastChangeByRoot[projectRoot] = changeTime;
    }

    /// <summary>Returns, and forgets, the roots that have had no change for <paramref name="debounce"/>.</summary>
    public List<string> TakeQuietRoots(DateTimeOffset now, TimeSpan debounce)
    {
        List<string> quietRoots = new List<string>();
        foreach (KeyValuePair<string, DateTimeOffset> entry in lastChangeByRoot)
        {
            if (now - entry.Value >= debounce)
            {
                quietRoots.Add(entry.Key);
            }
        }

        foreach (string quietRoot in quietRoots)
        {
            lastChangeByRoot.Remove(quietRoot);
        }

        return quietRoots;
    }
}

/// <summary>
/// Finds the project a changed file belongs to, by walking up from the file to the nearest folder that
/// holds a manifest (pyproject.toml, package.json, ...) or a .git folder. A file with neither is a loose
/// script: its own folder is the root.
/// </summary>
public static class ProjectRootFinder
{
    private const int MaxLevelsUp = 12;

    public static string FindRootFor(string filePath)
    {
        string fileFolder = Path.GetDirectoryName(filePath);
        if (fileFolder == null)
        {
            return filePath;
        }

        string folder = fileFolder;
        for (int level = 0; level < MaxLevelsUp && folder != null; level++)
        {
            // Stop at the drive root and at the user profile folder itself: neither is a project.
            if (Path.GetPathRoot(folder) == folder || IsUserProfileFolder(folder))
            {
                break;
            }

            if (IsProjectFolder(folder))
            {
                return folder;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return fileFolder;
    }

    private static bool IsProjectFolder(string folder)
    {
        try
        {
            foreach (FileSystemInfo entry in FolderListing.ListEntries(folder))
            {
                bool isGitFolder = entry is DirectoryInfo && entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase);
                bool isManifest = entry is FileInfo && CandidateFileNames.IsManifest(entry.Name);
                if (isGitFolder || isManifest)
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>True for C:\Users\alice itself (not for folders below it).</summary>
    private static bool IsUserProfileFolder(string folder)
    {
        string[] segments = folder.Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// One cron schedule from Settings, such as "0 2 * * *" (every day at 02:00 local time).
/// Uses the Cronos library to work out the next run time.
/// </summary>
public class ScanSchedule
{
    private readonly CronExpression expression;
    private DateTimeOffset nextRun;

    public ScanSchedule(string cronText, DateTimeOffset now)
    {
        CronText = cronText;
        expression = CronExpression.Parse(cronText);
        nextRun = ComputeNextRun(now);
    }

    public string CronText { get; }

    public DateTimeOffset NextRun
    {
        get { return nextRun; }
    }

    /// <summary>True once the next run time has passed. Moves the next run time on, so it fires once.</summary>
    public bool TakeIfDue(DateTimeOffset now)
    {
        if (now < nextRun)
        {
            return false;
        }

        nextRun = ComputeNextRun(now);
        return true;
    }

    private DateTimeOffset ComputeNextRun(DateTimeOffset after)
    {
        Nullable<DateTime> next = expression.GetNextOccurrence(after.UtcDateTime, TimeZoneInfo.Local);
        if (next == null)
        {
            return DateTimeOffset.MaxValue;
        }

        return new DateTimeOffset(next.Value, TimeSpan.Zero);
    }
}
