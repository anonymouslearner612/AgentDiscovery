using System.Text.Json;

namespace ShadowAgents;

/// <summary>One change to send to the backend: a finding that is new, changed or removed.</summary>
public class InventoryChange
{
    public const string New = "new";
    public const string Changed = "changed";
    public const string Removed = "removed";

    public InventoryChange(string change, FindingRecord record)
    {
        Change = change;
        Record = record;
    }

    public string Change { get; }
    public FindingRecord Record { get; }
}

/// <summary>
/// Every finding the watcher knows about, keyed by finding id. Scan results are compared against it so
/// only changes are sent (Section 10: "Send changes only (new, changed, removed), keyed by id").
/// The Running state is also kept here, because only the watcher sees processes.
/// </summary>
public class AgentInventory
{
    private readonly Dictionary<string, FindingRecord> records = new Dictionary<string, FindingRecord>();

    public int Count
    {
        get { return this.records.Count; }
    }

    public List<FindingRecord> AllRecords()
    {
        return new List<FindingRecord>(this.records.Values);
    }

    public FindingRecord Find(string id)
    {
        this.records.TryGetValue(id, out FindingRecord record);
        return record;
    }

    /// <summary>
    /// Merges the findings of one scan. <paramref name="scopePaths"/> are the folders or drives the scan
    /// covered: a known finding inside them that the scan no longer reports has been removed.
    /// </summary>
    public List<InventoryChange> ApplyScanResults(List<FindingRecord> scannedRecords, List<string> scopePaths)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        HashSet<string> seenIds = new HashSet<string>();

        foreach (FindingRecord scanned in scannedRecords)
        {
            seenIds.Add(scanned.Id);
            if (!this.records.TryGetValue(scanned.Id, out FindingRecord known))
            {
                this.records[scanned.Id] = scanned;
                changes.Add(new InventoryChange(InventoryChange.New, scanned));
                continue;
            }

            // The scanner cannot see processes; keep the runtime facts the watcher collected.
            // (n8n is the exception: the scanner reads its "active" flag itself.)
            if (scanned.Framework.Key != "n8n")
            {
                scanned.Runtime = known.Runtime;
                bool canRun = scanned.IsAgent() || scanned.IsConfiguredMcpServer();
                if (canRun && known.Runtime.Running)
                {
                    scanned.State = FindingStates.Running;
                }
            }

            scanned.FirstSeen = known.FirstSeen;
            this.records[scanned.Id] = scanned;
            if (Fingerprint(known) != Fingerprint(scanned))
            {
                changes.Add(new InventoryChange(InventoryChange.Changed, scanned));
            }
        }

        List<FindingRecord> knownRecords = new List<FindingRecord>(this.records.Values);
        foreach (FindingRecord known in knownRecords)
        {
            if (!seenIds.Contains(known.Id) && IsInScope(known, scopePaths))
            {
                this.records.Remove(known.Id);
                changes.Add(new InventoryChange(InventoryChange.Removed, known));
            }
        }

        return changes;
    }

    /// <summary>The deepest known project root that contains <paramref name="filePath"/>, or null.</summary>
    public string FindProjectRootContaining(string filePath)
    {
        string bestRoot = null;
        foreach (FindingRecord record in this.records.Values)
        {
            if (record.Project == null || !IsSameOrInside(filePath, record.Project.Root))
            {
                continue;
            }

            if (bestRoot == null || record.Project.Root.Length > bestRoot.Length)
            {
                bestRoot = record.Project.Root;
            }
        }

        return bestRoot;
    }

    /// <summary>
    /// For a relative script path such as "src/agent.py" (WMI does not tell us the working folder):
    /// the project of a known agent whose evidence file ends with that path.
    /// </summary>
    public string FindProjectRootByRelativeFile(string relativePath)
    {
        string wantedEnding = relativePath.Replace('\\', '/').TrimStart('.', '/');
        if (wantedEnding.Length == 0)
        {
            return null;
        }

        foreach (FindingRecord record in this.records.Values)
        {
            if (!record.IsAgent() || record.Project == null || record.Evidence.Count == 0)
            {
                continue;
            }

            string evidenceFile = record.Evidence[0].File;
            if (evidenceFile.Equals(wantedEnding, StringComparison.OrdinalIgnoreCase)
                || evidenceFile.EndsWith("/" + wantedEnding, StringComparison.OrdinalIgnoreCase))
            {
                return record.Project.Root;
            }
        }

        return null;
    }

    public List<string> KnownProjectRoots()
    {
        HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FindingRecord record in this.records.Values)
        {
            if (record.Project != null)
            {
                roots.Add(record.Project.Root);
            }
        }

        return new List<string>(roots);
    }

    /// <summary>Marks every agent of a project as running in a process (Stage 4).</summary>
    public List<InventoryChange> MarkRunning(string projectRoot, string processDescription, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (FindingRecord record in AgentsOfProject(projectRoot))
        {
            bool wasRunning = record.Runtime.Running;
            bool isNewProcess = !record.Runtime.Processes.Contains(processDescription);
            record.Runtime.Running = true;
            record.Runtime.LastSeenRunning = now;
            record.State = FindingStates.Running;
            if (isNewProcess)
            {
                record.Runtime.Processes.Add(processDescription);
            }

            if (!wasRunning || isNewProcess)
            {
                changes.Add(new InventoryChange(InventoryChange.Changed, record));
            }
        }

        return changes;
    }

    /// <summary>A process ended. Agents stay "running" while another process of the same project is alive.</summary>
    public List<InventoryChange> MarkProcessEnded(string projectRoot, string processDescription, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (FindingRecord record in AgentsOfProject(projectRoot))
        {
            if (!record.Runtime.Processes.Remove(processDescription))
            {
                continue;
            }

            record.Runtime.LastSeenRunning = now;
            if (record.Runtime.Processes.Count == 0)
            {
                record.Runtime.Running = false;
                record.State = FindingStates.Implemented; // lastSeenRunning keeps "ran recently"
            }

            changes.Add(new InventoryChange(InventoryChange.Changed, record));
        }

        return changes;
    }

    // ------------------------------------------------------------ configured MCP servers (Section 12)

    /// <summary>
    /// Configured stdio MCP servers whose process this command line is, found by the server's package or
    /// script path ("node ...\@modelcontextprotocol\server-filesystem\dist\index.js" is server-filesystem).
    /// </summary>
    public List<string> FindMcpServersStartedBy(string commandLine)
    {
        List<string> serverIds = new List<string>();
        foreach (FindingRecord record in this.records.Values)
        {
            if (!record.IsConfiguredMcpServer() || record.McpServer.Transport != "stdio")
            {
                continue;
            }

            string signature = McpServerProfile.ProcessSignature(record.McpServer);
            if (signature == null)
            {
                continue;
            }

            // Packages are written with '/', but on a command line they are often part of a Windows path.
            string windowsSignature = signature.Replace('/', '\\');
            bool appears = commandLine.IndexOf(signature, StringComparison.OrdinalIgnoreCase) >= 0
                || commandLine.IndexOf(windowsSignature, StringComparison.OrdinalIgnoreCase) >= 0;
            if (appears)
            {
                serverIds.Add(record.Id);
            }
        }

        return serverIds;
    }

    public List<InventoryChange> MarkMcpServerRunning(string serverId, string processDescription, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        FindingRecord record = Find(serverId);
        if (record == null)
        {
            return changes;
        }

        bool wasRunning = record.Runtime.Running;
        bool isNewProcess = !record.Runtime.Processes.Contains(processDescription);
        record.Runtime.Running = true;
        record.Runtime.LastSeenRunning = now;
        record.State = FindingStates.Running;
        if (isNewProcess)
        {
            record.Runtime.Processes.Add(processDescription);
        }

        if (!wasRunning || isNewProcess)
        {
            changes.Add(new InventoryChange(InventoryChange.Changed, record));
        }

        return changes;
    }

    /// <summary>The server's process ended. It goes back to "referenced" (configured) once no process of it is left.</summary>
    public List<InventoryChange> MarkMcpServerProcessEnded(string serverId, string processDescription, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        FindingRecord record = Find(serverId);
        if (record == null || !record.Runtime.Processes.Remove(processDescription))
        {
            return changes;
        }

        record.Runtime.LastSeenRunning = now;
        if (record.Runtime.Processes.Count == 0)
        {
            record.Runtime.Running = false;
            record.State = FindingStates.Referenced;
        }

        changes.Add(new InventoryChange(InventoryChange.Changed, record));
        return changes;
    }

    /// <summary>
    /// A proxied request went to a configured remote MCP server. There is no "stopped" event for remote
    /// servers, so only lastSeenRunning is kept, and a change is sent the first time only (like observed models).
    /// </summary>
    public List<InventoryChange> MarkRemoteMcpServerUsed(Uri requestUrl, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (FindingRecord record in this.records.Values)
        {
            if (!record.IsConfiguredMcpServer() || record.McpServer.Url == null)
            {
                continue;
            }

            Uri serverUrl;
            if (!Uri.TryCreate(record.McpServer.Url, UriKind.Absolute, out serverUrl))
            {
                continue;
            }

            bool sameHost = serverUrl.Host.Equals(requestUrl.Host, StringComparison.OrdinalIgnoreCase) && serverUrl.Port == requestUrl.Port;
            bool samePath = requestUrl.AbsolutePath.StartsWith(serverUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            if (!sameHost || !samePath)
            {
                continue;
            }

            bool firstTimeSeen = record.Runtime.LastSeenRunning == null;
            record.Runtime.LastSeenRunning = now;
            if (firstTimeSeen)
            {
                changes.Add(new InventoryChange(InventoryChange.Changed, record));
            }
        }

        return changes;
    }

    /// <summary>The per-user MCP configuration files behind known findings, to re-read on the incremental schedule.</summary>
    public List<string> KnownUserMcpConfigFiles()
    {
        HashSet<string> configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FindingRecord record in this.records.Values)
        {
            if (record.IsConfiguredMcpServer() && record.Project == null)
            {
                configFiles.Add(record.McpServer.ConfigFile);
            }
        }

        return new List<string>(configFiles);
    }

    /// <summary>A model seen in proxy traffic from this project's process. The observed model is the one that matters for risk.</summary>
    public List<InventoryChange> AddObservedModel(string projectRoot, ModelRecord observedModel, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (FindingRecord record in AgentsOfProject(projectRoot))
        {
            bool alreadyKnown = false;
            foreach (ModelRecord model in record.Runtime.ObservedModels)
            {
                if (model.Id == observedModel.Id && model.Provider == observedModel.Provider)
                {
                    alreadyKnown = true;
                }
            }

            record.Runtime.LastSeenRunning = now;
            if (!alreadyKnown)
            {
                record.Runtime.ObservedModels.Add(observedModel);
                changes.Add(new InventoryChange(InventoryChange.Changed, record));
            }
        }

        return changes;
    }

    public static AgentInventory Load(string path)
    {
        AgentInventory inventory = new AgentInventory();
        if (!File.Exists(path))
        {
            return inventory;
        }

        try
        {
            List<FindingRecord> records = JsonSerializer.Deserialize<List<FindingRecord>>(File.ReadAllText(path), ContractJson.LineOptions);
            if (records != null)
            {
                foreach (FindingRecord record in records)
                {
                    inventory.records[record.Id] = record;
                }
            }
        }
        catch (JsonException)
        {
            // A damaged inventory is rebuilt by the next scan; every finding is then reported as new.
        }

        return inventory;
    }

    public void Save(string path)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(AllRecords(), ContractJson.LineOptions));
        FileReplace.MoveOver(temporaryPath, path);
    }

    private List<FindingRecord> AgentsOfProject(string projectRoot)
    {
        List<FindingRecord> agents = new List<FindingRecord>();
        foreach (FindingRecord record in this.records.Values)
        {
            if (record.IsAgent() && record.Project != null && record.Project.Root.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                agents.Add(record);
            }
        }

        return agents;
    }

    private static bool IsInScope(FindingRecord record, List<string> scopePaths)
    {
        string location = null;
        if (record.Project != null)
        {
            location = record.Project.Root;
        }
        else if (record.Environment != null)
        {
            location = record.Environment.Path;
        }
        else if (record.McpServer != null)
        {
            location = record.McpServer.ConfigFile; // a per-user MCP configuration has no project
        }

        if (location == null)
        {
            return false;
        }

        foreach (string scopePath in scopePaths)
        {
            if (IsSameOrInside(location, scopePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere below it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string folderWithSeparator = folder;
        if (!folder.EndsWith("\\", StringComparison.Ordinal))
        {
            folderWithSeparator = folder + "\\";
        }
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folderWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The record's content without its timestamps, to tell whether anything really changed.</summary>
    private static string Fingerprint(FindingRecord record)
    {
        DateTimeOffset firstSeen = record.FirstSeen;
        DateTimeOffset lastSeen = record.LastSeen;
        record.FirstSeen = DateTimeOffset.MinValue;
        record.LastSeen = DateTimeOffset.MinValue;
        string fingerprint = JsonSerializer.Serialize(record, ContractJson.LineOptions);
        record.FirstSeen = firstSeen;
        record.LastSeen = lastSeen;
        return fingerprint;
    }
}

/// <summary>
/// Where changes go before the backend picks them up: one JSON line per change in outbox.jsonl.
/// The endpoint agent's existing uplink to the backend should read and clear this file
/// (replace this class when wiring the watcher into the agent).
/// </summary>
public class ChangeOutbox
{
    private class OutboxEntry
    {
        public string Change { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public FindingRecord Record { get; set; } = new FindingRecord();
    }

    private readonly string path;

    public ChangeOutbox(string path)
    {
        this.path = path;
    }

    public void Add(List<InventoryChange> changes, DateTimeOffset now)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using StreamWriter writer = new StreamWriter(this.path, append: true);
        foreach (InventoryChange change in changes)
        {
            OutboxEntry entry = new OutboxEntry { Change = change.Change, At = now, Record = change.Record };
            writer.WriteLine(JsonSerializer.Serialize(entry, ContractJson.LineOptions));
        }
    }
}

/// <summary>Where we are in one volume's change journal (Section 8, "USN journal details").</summary>
public class VolumeJournalState
{
    public string Drive { get; set; } = "";

    /// <summary>Changes when the journal is deleted and recreated. A different id means: run a new baseline.</summary>
    public ulong JournalId { get; set; }

    /// <summary>The next change to read.</summary>
    public long NextUsn { get; set; }
}

/// <summary>Everything the watcher must remember across restarts. Saved as watcher-state.json.</summary>
public class WatcherState
{
    public List<VolumeJournalState> Volumes { get; set; } = new List<VolumeJournalState>();
    public Nullable<DateTimeOffset> LastFullScan { get; set; }
    public Nullable<DateTimeOffset> LastIncrementalScan { get; set; }

    /// <summary>Proxy event file read position, so events are not processed twice.</summary>
    public long ProxyEventsOffset { get; set; }

    public VolumeJournalState FindVolume(string drive)
    {
        foreach (VolumeJournalState volume in Volumes)
        {
            if (volume.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase))
            {
                return volume;
            }
        }

        return null;
    }

    public static WatcherState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new WatcherState();
        }

        try
        {
            WatcherState state = JsonSerializer.Deserialize<WatcherState>(File.ReadAllText(path), ContractJson.ReadOptions);
            if (state == null)
            {
                return new WatcherState();
            }

            return state;
        }
        catch (JsonException)
        {
            // A damaged state file only costs a new baseline.
            return new WatcherState();
        }
    }

    public void Save(string path)
    {
        // Write to a temporary file first, then replace, so a crash never leaves a half-written file.
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, ContractJson.WriteIndentedOptions));
        FileReplace.MoveOver(temporaryPath, path);
    }
}

public static class FileReplace
{
    /// <summary>Moves <paramref name="sourcePath"/> to <paramref name="targetPath"/>, replacing the target if it exists.</summary>
    public static void MoveOver(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(sourcePath, targetPath, null);
            return;
        }

        File.Move(sourcePath, targetPath);
    }
}
