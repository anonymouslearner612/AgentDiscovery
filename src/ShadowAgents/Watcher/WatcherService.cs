namespace ShadowAgents;

/// <summary>
/// The resident half of discovery (design doc Sections 4.2, 5 and 8). It decides WHEN and WHAT to scan;
/// "ShadowAgents scan" (a separate process) does the scanning. Everything happens in one loop, one "tick" per second:
///
///   1. process starts/stops     → mark agents Running, or queue a scan of an unknown folder
///   2. proxy requests            → record the models agents really use
///   3. change journal            → changed files → project roots → change batcher
///   4. quiet roots and schedules → scan queue
///   5. the first queued scan     → run it (unless on battery or the machine is busy)
///
/// Results are merged into the inventory, and only the differences go to the outbox for the backend.
/// </summary>
public class WatcherService : IDisposable
{
    private readonly string stateFolder;
    private readonly WatcherState state;
    private readonly AgentInventory inventory;
    private readonly ChangeOutbox outbox;
    private readonly TelemetryLog telemetry;
    private readonly ProxyEventReader proxyEvents;
    private readonly ChangeBatcher batcher = new ChangeBatcher();
    private readonly List<ScanRequest> scanQueue = new List<ScanRequest>();
    private readonly List<JournalWatcher> journals = new List<JournalWatcher>();
    private readonly RuntimeCorrelator correlator;
    private readonly SystemConditions systemConditions = new SystemConditions();
    private readonly TextWriter log;

    private ProcessMonitor processMonitor;
    private ScanSchedule incrementalSchedule;
    private ScanSchedule fullSchedule;
    private DateTimeOffset lastJournalPoll = DateTimeOffset.MinValue;
    private int machineCpuPercent;
    private string lastPostponeReason;

    public WatcherService(string stateFolder, TextWriter log)
    {
        this.stateFolder = stateFolder;
        this.log = log;
        Directory.CreateDirectory(stateFolder);

        correlator = new RuntimeCorrelator();
        state = WatcherState.Load(StatePath);
        inventory = AgentInventory.Load(InventoryPath);
        outbox = new ChangeOutbox(Path.Combine(stateFolder, "outbox.jsonl"));
        telemetry = new TelemetryLog(Path.Combine(stateFolder, "telemetry.jsonl"));
        proxyEvents = new ProxyEventReader(Path.Combine(stateFolder, "proxy-events.jsonl"));
    }

    private string StatePath
    {
        get { return Path.Combine(this.stateFolder, "watcher-state.json"); }
    }

    private string InventoryPath
    {
        get { return Path.Combine(this.stateFolder, "inventory.json"); }
    }

    // ------------------------------------------------------------ start and loop

    /// <summary>Opens the change journals, starts process monitoring and queues a baseline when one is needed.</summary>
    public void Start()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Log("Starting. State folder: " + this.stateFolder + ". Elevated: " + SystemConditions.IsElevated() + ". Known findings: " + inventory.Count);
        incrementalSchedule = new ScanSchedule(Settings.IncrementalSchedule, now);
        fullSchedule = new ScanSchedule(Settings.FullSchedule, now);
        Log("Next incremental scan " + incrementalSchedule.NextRun.ToLocalTime() + ", next full scan " + fullSchedule.NextRun.ToLocalTime());

        OpenJournals();

        bool needsBaseline = state.LastFullScan == null;
        if (needsBaseline)
        {
            foreach (string drive in FixedDrives())
            {
                QueueVolumeScan(drive, "baseline", now);
            }
        }

        StartProcessMonitor();
        SaveState();
    }

    /// <summary>Runs ticks until <paramref name="cancellationToken"/> is cancelled (Ctrl+C or service stop).</summary>
    public void RunUntilStopped(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Tick(cancellationToken);
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }

        SaveState();
        Log("Stopped.");
    }

    /// <summary>One pass of the main loop.</summary>
    private void Tick(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        machineCpuPercent = systemConditions.SampleCpuPercent();
        HandleProcessEvents(now);
        HandleProxyEvents(now);

        if (now - lastJournalPoll >= TimeSpan.FromSeconds(Settings.JournalPollSeconds))
        {
            lastJournalPoll = now;
            PollJournals(now);
        }

        List<string> quietRoots = batcher.TakeQuietRoots(now, TimeSpan.FromSeconds(Settings.DebounceSeconds));
        if (quietRoots.Count > 0)
        {
            QueueRootScan(quietRoots, "file-change", now);
        }

        QueueDueSchedules(now);
        RunNextScanIfAllowed(now, cancellationToken);
    }

    /// <summary>
    /// The entry point for Titanium Proxy: one request from one process. When the request goes to an LLM
    /// provider, the process's agents are marked Running and the requested model is recorded as observed.
    /// </summary>
    public void OnProxyRequest(int processId, Uri url, string requestBody)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (LlmEndpoints.ProviderFor(url) == null)
        {
            return;
        }

        List<InventoryChange> changes = new List<InventoryChange>();
        string projectRoot = correlator.FindProjectRootForProcess(processId);
        if (projectRoot == null)
        {
            // We missed the process start (or it started before the watcher). Look it up now.
            string commandLine = ProcessMonitor.QueryCommandLine(processId);
            if (commandLine.Length > 0)
            {
                ProcessEvent started = new ProcessEvent { IsStart = true, ProcessId = processId, ProcessName = "process", CommandLine = commandLine };
                changes.AddRange(correlator.OnProcessEvent(started, inventory, now));
                projectRoot = correlator.FindProjectRootForProcess(processId);
            }
        }

        if (projectRoot == null)
        {
            return;
        }

        ModelRecord observedModel = LlmEndpoints.ExtractModel(url, requestBody);
        if (observedModel != null)
        {
            changes.AddRange(inventory.AddObservedModel(projectRoot, observedModel, now));
        }

        SendChanges(changes, now);
    }

    // ------------------------------------------------------------ continuous inputs

    private void OpenJournals()
    {
        foreach (string drive in FixedDrives())
        {
            JournalWatcher journal = JournalWatcher.TryCreate(drive, out string problem);
            if (journal == null)
            {
                Log("No change journal for " + drive + " (" + problem + "); scheduled scans cover it.");
                continue;
            }

            VolumeJournalState volumeState = state.FindVolume(drive);
            if (volumeState == null)
            {
                volumeState = new VolumeJournalState { Drive = drive };
                state.Volumes.Add(volumeState);
            }

            JournalStartStatus status = journal.CheckSavedPosition(volumeState);
            if (status == JournalStartStatus.NotAvailable)
            {
                Log("Change journal not active on " + drive + "; scheduled scans cover it.");
                journal.Dispose();
                continue;
            }

            if (status == JournalStartStatus.BaselineNeeded)
            {
                Log("Journal on " + drive + " is new, reset or wrapped: queueing a baseline scan.");
                QueueVolumeScan(drive, "baseline", DateTimeOffset.UtcNow);
            }

            journals.Add(journal);
        }
    }

    private void PollJournals(DateTimeOffset now)
    {
        foreach (JournalWatcher journal in journals)
        {
            VolumeJournalState volumeState = state.FindVolume(journal.Drive);
            if (volumeState == null)
            {
                continue;
            }

            List<string> changedFiles = journal.ReadChangedCandidateFiles(volumeState, out bool baselineNeeded);
            if (baselineNeeded)
            {
                Log("Journal on " + journal.Drive + " was reset or wrapped: queueing a baseline scan.");
                QueueVolumeScan(journal.Drive, "journal-reset", now);
            }

            foreach (string changedFile in changedFiles)
            {
                batcher.AddChange(ProjectRootFinder.FindRootFor(changedFile), now);
            }
        }
    }

    private void StartProcessMonitor()
    {
        try
        {
            processMonitor = new ProcessMonitor();
            processMonitor.Start();
            processMonitor.QueueAlreadyRunningProcesses();
            Log("Process monitoring: " + processMonitor.Mode);
        }
        catch (System.Management.ManagementException exception)
        {
            Log("Process monitoring unavailable: " + exception.Message);
            processMonitor = null;
        }
    }

    private void HandleProcessEvents(DateTimeOffset now)
    {
        if (processMonitor == null)
        {
            return;
        }

        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (ProcessEvent processEvent in processMonitor.TakeEvents())
        {
            changes.AddRange(correlator.OnProcessEvent(processEvent, inventory, now));
        }

        SendChanges(changes, now);

        // Processes running code in folders we have never scanned.
        if (correlator.RootsToScan.Count > 0)
        {
            QueueRootScan(new List<string>(correlator.RootsToScan), "process-start", now);
            correlator.RootsToScan.Clear();
        }
    }

    private void HandleProxyEvents(DateTimeOffset now)
    {
        long offset = state.ProxyEventsOffset;
        List<ProxyRequestEvent> events = proxyEvents.ReadNewEvents(ref offset);
        state.ProxyEventsOffset = offset;

        if (events.Count == 0)
        {
            return;
        }

        foreach (ProxyRequestEvent proxyEvent in events)
        {
            if (Uri.TryCreate(proxyEvent.Url, UriKind.Absolute, out Uri url))
            {
                OnProxyRequest(proxyEvent.ProcessId, url, proxyEvent.Body);
            }
        }

        SaveState(); // remember how far we read, so a restart does not replay these events
    }

    // ------------------------------------------------------------ schedules and the scan queue

    private void QueueDueSchedules(DateTimeOffset now)
    {
        if (fullSchedule != null && fullSchedule.TakeIfDue(now))
        {
            foreach (string drive in FixedDrives())
            {
                QueueVolumeScan(drive, "full-schedule", now);
            }
        }

        if (incrementalSchedule != null && incrementalSchedule.TakeIfDue(now))
        {
            // Re-check every known project. Unchanged files cost nothing thanks to the scanner's cache.
            List<string> roots = inventory.KnownProjectRoots();
            if (roots.Count > 0)
            {
                QueueRootScan(roots, "incremental-schedule", now);
            }
        }
    }

    private void QueueVolumeScan(string drive, string reason, DateTimeOffset now)
    {
        foreach (ScanRequest queued in scanQueue)
        {
            if (queued.Volumes.Contains(drive, StringComparer.OrdinalIgnoreCase))
            {
                return; // already queued
            }
        }

        ScanRequest request = new ScanRequest { Reason = reason, QueuedAt = now };
        request.Volumes.Add(drive);
        scanQueue.Add(request);
    }

    /// <summary>Adds roots to a queued project scan with the same reason, or queues a new one.</summary>
    private void QueueRootScan(List<string> roots, string reason, DateTimeOffset now)
    {
        ScanRequest request = null;
        foreach (ScanRequest queued in scanQueue)
        {
            if (!queued.IsWholeVolume && queued.Reason == reason)
            {
                request = queued;
            }
        }

        if (request == null)
        {
            request = new ScanRequest { Reason = reason, QueuedAt = now };
            scanQueue.Add(request);
        }

        foreach (string root in roots)
        {
            if (!request.Roots.Contains(root, StringComparer.OrdinalIgnoreCase) && !IsCoveredByQueuedVolumeScan(root))
            {
                request.Roots.Add(root);
            }
        }

        if (request.Roots.Count == 0)
        {
            scanQueue.Remove(request);
        }
    }

    private bool IsCoveredByQueuedVolumeScan(string root)
    {
        foreach (ScanRequest queued in scanQueue)
        {
            foreach (string drive in queued.Volumes)
            {
                if (root.StartsWith(drive, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RunNextScanIfAllowed(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (scanQueue.Count == 0)
        {
            return;
        }

        ScanRequest request = scanQueue[0];
        string postponeReason = WhyPostpone(request, now);
        if (postponeReason != null)
        {
            if (postponeReason != lastPostponeReason)
            {
                Log("Postponing scans: " + postponeReason);
                lastPostponeReason = postponeReason;
            }

            return;
        }

        lastPostponeReason = null;
        scanQueue.RemoveAt(0);
        RunScan(request, now, cancellationToken);
    }

    /// <summary>Section 9: pause scanning on battery or under heavy load, but never forever.</summary>
    private string WhyPostpone(ScanRequest request, DateTimeOffset now)
    {
        bool waitedTooLong = now - request.QueuedAt > TimeSpan.FromMinutes(Settings.MaxPostponeMinutes);
        if (waitedTooLong)
        {
            return null;
        }

        if (SystemConditions.IsOnBattery())
        {
            return "on battery";
        }

        if (machineCpuPercent > Settings.PauseAboveCpuPercent)
        {
            return "machine busy (" + machineCpuPercent + "% CPU)";
        }

        return null;
    }

    private void RunScan(ScanRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A project folder that was deleted cannot be scanned; its findings are simply removed.
        List<string> existingRoots = new List<string>();
        List<string> deletedRoots = new List<string>();
        foreach (string root in request.Roots)
        {
            if (Directory.Exists(root))
            {
                existingRoots.Add(root);
            }
            else
            {
                deletedRoots.Add(root);
            }
        }

        List<InventoryChange> changes = inventory.ApplyScanResults(new List<FindingRecord>(), deletedRoots);

        if (existingRoots.Count > 0 || request.Volumes.Count > 0)
        {
            ScanJob job = new ScanJob
            {
                Roots = existingRoots,
                Volumes = request.Volumes,
                CachePath = Path.Combine(this.stateFolder, "scan-cache.db"),
            };

            Log("Scanning (" + request.Reason + "): " + DescribeScope(request.Volumes, existingRoots));
            ScannerLauncher launcher = new ScannerLauncher(Path.Combine(this.stateFolder, "work"));
            ScanOutcome outcome = launcher.Run(job, cancellationToken);

            // Only a complete scan may remove findings: missing results from a crashed scan mean nothing.
            List<string> scope = new List<string>();
            if (outcome.IsComplete)
            {
                scope.AddRange(existingRoots);
                foreach (string drive in request.Volumes)
                {
                    scope.Add(drive + "\\");
                }
            }

            changes.AddRange(inventory.ApplyScanResults(outcome.Findings, scope));
            changes.AddRange(correlator.ReapplyRunning(inventory, now));
            telemetry.Record(request, outcome, changes.Count, now);

            string summaryText = "no summary";
            if (outcome.Summary != null)
            {
                summaryText = outcome.Summary.AgentsFound + " agents, " + outcome.Summary.FilesRead + " files read, " + outcome.Summary.FilesFromCache + " from cache";
            }
            Log("Scan finished in " + outcome.Duration.TotalSeconds.ToString("F1") + "s (exit " + outcome.ExitCode + "): " + summaryText + ", " + changes.Count + " changes.");
            if (outcome.ExitCode != 0 && outcome.ErrorOutput.Length > 0)
            {
                Log("Scanner said: " + outcome.ErrorOutput.Trim());
            }

            if (outcome.IsComplete && request.IsWholeVolume)
            {
                state.LastFullScan = now;
            }
            else if (outcome.IsComplete && (request.Reason == "baseline" || request.Reason == "full-schedule"))
            {
                state.LastFullScan = now;
            }
            else if (outcome.IsComplete && request.Reason == "incremental-schedule")
            {
                state.LastIncrementalScan = now;
            }
        }

        SendChanges(changes, now);
        SaveState();
    }

    // ------------------------------------------------------------ helpers

    private void SendChanges(List<InventoryChange> changes, DateTimeOffset now)
    {
        if (changes.Count == 0)
        {
            return;
        }

        outbox.Add(changes, now);
        inventory.Save(InventoryPath);
        foreach (InventoryChange change in changes)
        {
            string name = change.Record.Agent.Name;
            if (name == null)
            {
                name = change.Record.Framework.Key;
            }
            Log("  " + change.Change + ": " + change.Record.State + " " + change.Record.Framework.Key + " " + name);
        }
    }

    private void SaveState()
    {
        state.Save(StatePath);
        inventory.Save(InventoryPath);
    }

    private static List<string> FixedDrives()
    {
        return ScanRunner.ResolveVolumes(new List<string> { "auto" });
    }

    private static string DescribeScope(List<string> volumes, List<string> roots)
    {
        List<string> parts = new List<string>(volumes);
        if (roots.Count <= 5)
        {
            parts.AddRange(roots);
        }
        else
        {
            parts.Add(roots.Count + " project folders");
        }

        return string.Join(", ", parts);
    }

    private void Log(string message)
    {
        this.log.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
    }

    public void Dispose()
    {
        foreach (JournalWatcher journal in journals)
        {
            journal.Dispose();
        }

        if (processMonitor != null)
        {
            processMonitor.Dispose();
        }
    }
}
