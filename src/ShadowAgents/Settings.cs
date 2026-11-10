namespace ShadowAgents;

/// <summary>
/// Every tunable value, hard-coded in one place. Change a value here and rebuild.
/// (These used to be command-line options and a policy file.)
/// </summary>
public static class Settings
{
    // ---- scanner

    /// <summary>Files larger than this are not read (generated or vendored code, not hand-written agents).</summary>
    public const long MaxFileBytes = 1_048_576;

    /// <summary>A project root with more source files than this is only partly scanned (and flagged in the summary).</summary>
    public const int MaxFilesPerRoot = 5_000;

    /// <summary>CPU cap for a scan, as a percent of ONE core (design doc Section 9). 25 = a quarter of one core.</summary>
    public const int ScanCpuPercent = 25;

    /// <summary>Memory cap for a scan, in MB.</summary>
    public const int ScanMemoryMb = 200;

    // ---- detection

    public const int MaxDescriptionChars = 1_000;

    /// <summary>
    /// How far the bracket matcher may scan to find the end of one constructor call.
    /// Guards against hostile input such as thousands of unclosed <c>Agent(</c> tokens.
    /// </summary>
    public const int MaxConstructorScanChars = 16 * 1024;

    /// <summary>How many constructor anchors are examined per file before giving up.</summary>
    public const int MaxConstructorAttemptsPerFile = 256;

    // ---- watcher

    /// <summary>Wait this long with no further changes in a project before scanning it.</summary>
    public const int DebounceSeconds = 60;

    /// <summary>Cron schedule (minute hour day month weekday) for re-checking known projects: 02:00 daily.</summary>
    public const string IncrementalSchedule = "0 2 * * *";

    /// <summary>Cron schedule for a whole-machine scan that catches anything the journal missed: 03:00 on Sundays.</summary>
    public const string FullSchedule = "0 3 * * 0";

    /// <summary>How often to read the change journal. Reading it is cheap: it is a small log, not a folder walk.</summary>
    public const int JournalPollSeconds = 10;

    /// <summary>Postpone scans while the whole machine is busier than this (scans are also postponed on battery).</summary>
    public const int PauseAboveCpuPercent = 80;

    /// <summary>Never postpone a scan longer than this, even on battery or under load.</summary>
    public const int MaxPostponeMinutes = 240;
}
