using System.Diagnostics;
using System.Text.Json;

namespace ShadowAgents;

/// <summary>The result of one scan process.</summary>
public class ScanOutcome
{
    public int ExitCode { get; set; }
    public List<FindingRecord> Findings { get; } = new List<FindingRecord>();
    public SummaryRecord Summary { get; set; }
    public TimeSpan Duration { get; set; }
    public string ErrorOutput { get; set; } = "";

    /// <summary>
    /// True when the findings are complete, so missing findings really mean "removed".
    /// A scan that crashed or stopped early must not delete anything from the inventory.
    /// </summary>
    public bool IsComplete
    {
        get { return ExitCode == 0 && Summary != null && !Summary.StoppedEarly; }
    }
}

/// <summary>
/// Starts "ShadowAgents scan" with a job file and reads its JSON lines output (Section 5, "Watcher-to-scanner
/// contract"). The scanner runs as a separate process, so a crash while parsing cannot take down the
/// watcher, and all its memory is released when it exits.
/// </summary>
public class ScannerLauncher
{
    private readonly string workFolder;

    public ScannerLauncher(string workFolder)
    {
        this.workFolder = workFolder;
    }

    public ScanOutcome Run(ScanJob job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(this.workFolder);
        string jobPath = Path.Combine(this.workFolder, "scan-job.json");
        string outputPath = Path.Combine(this.workFolder, "scan-output.jsonl");
        job.OutputPath = outputPath;
        job.SaveToFile(jobPath);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        // The scanner caps its own memory with a Job Object (see ResourceLimiter).
        ProcessStartInfo startInfo = CreateStartInfo(jobPath);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ScanOutcome outcome = new ScanOutcome();
        using (Process process = new Process())
        {
            process.StartInfo = startInfo;
            process.Start();

            // Read error output in the background so the scanner never blocks on a full pipe.
            Task<string> errorOutput = process.StandardError.ReadToEndAsync();
            WaitForExit(process, cancellationToken);

            outcome.ExitCode = -1;
            if (process.HasExited)
            {
                outcome.ExitCode = process.ExitCode;
            }

            outcome.ErrorOutput = "";
            if (errorOutput.Wait(TimeSpan.FromSeconds(5)))
            {
                outcome.ErrorOutput = errorOutput.Result;
            }
        }

        outcome.Duration = stopwatch.Elapsed;
        ReadOutput(outputPath, outcome);
        return outcome;
    }

    /// <summary>
    /// Starts this same program again as "ShadowAgents scan --job job.json".
    /// </summary>
    private ProcessStartInfo CreateStartInfo(string jobPath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        startInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;

        // The job path is quoted because it may contain spaces (for example C:\Users\Jane Doe\...).
        startInfo.Arguments = "scan --job \"" + jobPath + "\"";
        return startInfo;
    }

    private static void WaitForExit(Process process, CancellationToken cancellationToken)
    {
        while (!process.WaitForExit(500))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The watcher is shutting down. The scanner can be killed safely: its cache keeps the progress.
                process.Kill();
                process.WaitForExit();
                return;
            }
        }
    }

    private static void ReadOutput(string outputPath, ScanOutcome outcome)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        foreach (string line in File.ReadLines(outputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (line.Contains("\"recordType\":\"summary\"", StringComparison.Ordinal))
                {
                    outcome.Summary = JsonSerializer.Deserialize<SummaryRecord>(line, ContractJson.LineOptions);
                    continue;
                }

                FindingRecord finding = JsonSerializer.Deserialize<FindingRecord>(line, ContractJson.LineOptions);
                if (finding != null)
                {
                    outcome.Findings.Add(finding);
                }
            }
            catch (JsonException)
            {
                // A half-written last line (scanner killed) is skipped.
            }
        }
    }
}

/// <summary>A scan the watcher wants to run, waiting in its queue.</summary>
public class ScanRequest
{
    /// <summary>Whole drives to scan (baseline or scheduled full scan). Empty for project scans.</summary>
    public List<string> Volumes { get; } = new List<string>();

    /// <summary>Project folders to scan (after file changes, a new process, or the incremental schedule).</summary>
    public List<string> Roots { get; } = new List<string>();

    /// <summary>Why: "baseline", "full-schedule", "incremental-schedule", "file-change" or "process-start".</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the request was queued, so scans postponed on battery are not postponed forever.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    public bool IsWholeVolume
    {
        get { return Volumes.Count > 0; }
    }
}

/// <summary>
/// Scan cost telemetry (plan step 7): one JSON line per scan with its reason, duration and work done,
/// so the soak test can check the budgets in Section 9.
/// </summary>
public class TelemetryLog
{
    private class TelemetryEntry
    {
        public DateTimeOffset At { get; set; }
        public string Reason { get; set; } = "";
        public List<string> Volumes { get; set; } = new List<string>();
        public int RootCount { get; set; }
        public int ExitCode { get; set; }
        public long DurationMs { get; set; }
        public int FilesRead { get; set; }
        public long BytesRead { get; set; }
        public int FilesFromCache { get; set; }
        public int AgentsFound { get; set; }
        public bool StoppedEarly { get; set; }
        public List<string> ListingMethods { get; set; } = new List<string>();
        public int ChangesSent { get; set; }
    }

    private readonly string path;

    public TelemetryLog(string path)
    {
        this.path = path;
    }

    public void Record(ScanRequest request, ScanOutcome outcome, int changesSent, DateTimeOffset now)
    {
        TelemetryEntry entry = new TelemetryEntry
        {
            At = now,
            Reason = request.Reason,
            Volumes = request.Volumes,
            RootCount = request.Roots.Count,
            ExitCode = outcome.ExitCode,
            DurationMs = (long)outcome.Duration.TotalMilliseconds,
            ChangesSent = changesSent,
        };

        SummaryRecord summary = outcome.Summary;
        if (summary != null)
        {
            entry.FilesRead = summary.FilesRead;
            entry.BytesRead = summary.BytesRead;
            entry.FilesFromCache = summary.FilesFromCache;
            entry.AgentsFound = summary.AgentsFound;
            entry.StoppedEarly = summary.StoppedEarly;
            entry.ListingMethods = summary.ListingMethods;
        }

        File.AppendAllText(this.path, JsonSerializer.Serialize(entry, ContractJson.LineOptions) + Environment.NewLine);
    }
}
