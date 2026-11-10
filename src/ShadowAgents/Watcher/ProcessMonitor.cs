using System.Collections.Concurrent;
using System.Management;
using System.Text;

namespace ShadowAgents;

/// <summary>
/// Links running processes to known projects and marks their agents Running (Section 6.5).
/// A process in a folder with no known project asks for a scan of that folder: this catches agents in
/// places the indexer missed.
/// </summary>
public class RuntimeCorrelator
{
    private class TrackedProcess
    {
        public TrackedProcess(string projectRoot, string description)
        {
            ProjectRoot = projectRoot;
            Description = description;
        }

        public string ProjectRoot { get; }
        public string Description { get; }
    }

    private readonly Dictionary<int, TrackedProcess> processesById = new Dictionary<int, TrackedProcess>();
    /// <summary>Folders to scan because a process runs code there that we have not scanned yet.</summary>
    public List<string> RootsToScan { get; } = new List<string>();

    public int TrackedProcessCount
    {
        get { return processesById.Count; }
    }

    public List<InventoryChange> OnProcessEvent(ProcessEvent processEvent, AgentInventory inventory, DateTimeOffset now)
    {
        if (processEvent.IsStart)
        {
            return OnProcessStarted(processEvent, inventory, now);
        }

        return OnProcessStopped(processEvent.ProcessId, inventory, now);
    }

    /// <summary>The project a process is running, if we know it. Used to attribute proxy traffic.</summary>
    public string FindProjectRootForProcess(int processId)
    {
        if (processesById.TryGetValue(processId, out TrackedProcess tracked))
        {
            return tracked.ProjectRoot;
        }

        return null;
    }

    /// <summary>
    /// After a scan adds new agents, marks the ones whose projects already have a tracked process.
    /// (The process may have started before its project was scanned.)
    /// </summary>
    public List<InventoryChange> ReapplyRunning(AgentInventory inventory, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (TrackedProcess tracked in processesById.Values)
        {
            changes.AddRange(inventory.MarkRunning(tracked.ProjectRoot, tracked.Description, now));
        }

        return changes;
    }

    private List<InventoryChange> OnProcessStarted(ProcessEvent processEvent, AgentInventory inventory, DateTimeOffset now)
    {
        List<InventoryChange> changes = new List<InventoryChange>();
        foreach (string scriptPath in ScriptPathResolver.FindScriptPaths(processEvent.CommandLine))
        {
            string projectRoot = FindProjectRoot(scriptPath, inventory);
            if (projectRoot == null)
            {
                continue;
            }

            processesById[processEvent.ProcessId] = new TrackedProcess(projectRoot, processEvent.Description);
            changes.AddRange(inventory.MarkRunning(projectRoot, processEvent.Description, now));
            break; // one project per process
        }

        return changes;
    }

    private List<InventoryChange> OnProcessStopped(int processId, AgentInventory inventory, DateTimeOffset now)
    {
        if (!processesById.TryGetValue(processId, out TrackedProcess tracked))
        {
            return new List<InventoryChange>();
        }

        processesById.Remove(processId);
        return inventory.MarkProcessEnded(tracked.ProjectRoot, tracked.Description, now);
    }

    /// <summary>
    /// Absolute path: the known project that contains it, or else the folder it is in (and ask for a scan).
    /// Relative path: a known agent file that ends with it.
    /// </summary>
    private string FindProjectRoot(string scriptPath, AgentInventory inventory)
    {
        if (!PathHelpers.IsFullyQualified(scriptPath))
        {
            return inventory.FindProjectRootByRelativeFile(scriptPath);
        }

        string knownRoot = inventory.FindProjectRootContaining(scriptPath);
        if (knownRoot != null)
        {
            return knownRoot;
        }

        // Tools run their own code from Program Files, AppData\Local\Programs, node_modules, bin, ...
        // Those are never projects, so never scan them.
        if (SkipRules.IsInSkippedFolder(scriptPath) || SkipRules.IsInSkippedFolder(Path.Combine(scriptPath, "x")))
        {
            return null;
        }

        bool isFolder = Directory.Exists(scriptPath);
        bool isFile = File.Exists(scriptPath);
        if (!isFolder && !isFile)
        {
            return null;
        }

        string newRoot = scriptPath;
        if (!isFolder)
        {
            newRoot = ProjectRootFinder.FindRootFor(scriptPath);
        }
        if (!RootsToScan.Contains(newRoot, StringComparer.OrdinalIgnoreCase))
        {
            RootsToScan.Add(newRoot);
        }

        return newRoot;
    }
}

/// <summary>A process that started or stopped.</summary>
public class ProcessEvent
{
    public bool IsStart { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string CommandLine { get; set; } = "";

    /// <summary>"python.exe (pid 1234)": how the process is shown in the runtime block.</summary>
    public string Description
    {
        get { return ProcessName + " (pid " + ProcessId + ")"; }
    }
}

/// <summary>
/// Watches process starts and stops through WMI (Section 6.5, "Process starts") and queues the ones that
/// can run agents. Events arrive on WMI's threads; the watcher's main loop takes them from the queue.
/// </summary>
/// <remarks>
/// Win32_ProcessStartTrace and Win32_ProcessStopTrace are pushed by the kernel's ETW provider, so nothing
/// is polled. They need administrator rights. Without them we fall back to WMI instance events, which
/// WMI checks every few seconds ("WITHIN 3"); short-lived scripts can then be missed.
/// </remarks>
public class ProcessMonitor : IDisposable
{
    private readonly ConcurrentQueue<ProcessEvent> events = new ConcurrentQueue<ProcessEvent>();
    private readonly List<ManagementEventWatcher> wmiWatchers = new List<ManagementEventWatcher>();

    /// <summary>"trace" (event driven) or "instance-polling" (fallback), for the log.</summary>
    public string Mode { get; private set; } = "off";

    public void Start()
    {
        try
        {
            StartWatcher("SELECT * FROM Win32_ProcessStartTrace", OnTraceStart);
            StartWatcher("SELECT * FROM Win32_ProcessStopTrace", OnTraceStop);
            Mode = "trace";
        }
        catch (ManagementException)
        {
            StopWatchers();
            StartWatcher("SELECT * FROM __InstanceCreationEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'", OnInstanceCreated);
            StartWatcher("SELECT * FROM __InstanceDeletionEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'", OnInstanceDeleted);
            Mode = "instance-polling";
        }
    }

    /// <summary>Queues a start event for every agent-runner process already running (used once at startup).</summary>
    public void QueueAlreadyRunningProcesses()
    {
        using ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process");
        foreach (ManagementBaseObject process in searcher.Get())
        {
            QueueIfAgentRunner(true, process["ProcessId"], process["Name"], process["CommandLine"]);
        }
    }

    public List<ProcessEvent> TakeEvents()
    {
        List<ProcessEvent> events = new List<ProcessEvent>();
        while (this.events.TryDequeue(out ProcessEvent processEvent))
        {
            events.Add(processEvent);
        }

        return events;
    }

    /// <summary>The command line of a running process, or "" if it has exited or cannot be read.</summary>
    public static string QueryCommandLine(int processId)
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId);
            foreach (ManagementBaseObject process in searcher.Get())
            {
                return Convert.ToString(process["CommandLine"]);
            }
        }
        catch (ManagementException)
        {
            // The process exited, or we lack the rights to read it.
        }

        return "";
    }

    public void Dispose()
    {
        StopWatchers();
    }

    private void StartWatcher(string query, EventArrivedEventHandler handler)
    {
        ManagementEventWatcher watcher = new ManagementEventWatcher(new WqlEventQuery(query));
        watcher.EventArrived += handler;
        wmiWatchers.Add(watcher);
        watcher.Start();
    }

    private void StopWatchers()
    {
        foreach (ManagementEventWatcher watcher in wmiWatchers)
        {
            try
            {
                watcher.Stop();
            }
            catch (ManagementException)
            {
                // Already stopped.
            }

            watcher.Dispose();
        }

        wmiWatchers.Clear();
    }

    private void OnTraceStart(object sender, EventArrivedEventArgs eventArgs)
    {
        object processId = eventArgs.NewEvent["ProcessID"];
        object processName = eventArgs.NewEvent["ProcessName"];

        // The trace event has no command line; ask for it at once, before a short script exits.
        string commandLine = "";
        if (ScriptPathResolver.IsAgentRunner(Convert.ToString(processName)))
        {
            commandLine = QueryCommandLine(Convert.ToInt32(processId));
        }

        QueueIfAgentRunner(true, processId, processName, commandLine);
    }

    private void OnTraceStop(object sender, EventArrivedEventArgs eventArgs)
    {
        QueueIfAgentRunner(false, eventArgs.NewEvent["ProcessID"], eventArgs.NewEvent["ProcessName"], "");
    }

    private void OnInstanceCreated(object sender, EventArrivedEventArgs eventArgs)
    {
        if (eventArgs.NewEvent["TargetInstance"] is ManagementBaseObject process)
        {
            QueueIfAgentRunner(true, process["ProcessId"], process["Name"], process["CommandLine"]);
        }
    }

    private void OnInstanceDeleted(object sender, EventArrivedEventArgs eventArgs)
    {
        if (eventArgs.NewEvent["TargetInstance"] is ManagementBaseObject process)
        {
            QueueIfAgentRunner(false, process["ProcessId"], process["Name"], "");
        }
    }

    private void QueueIfAgentRunner(bool isStart, object processId, object processName, object commandLine)
    {
        string name = Convert.ToString(processName);
        if (processId == null || !ScriptPathResolver.IsAgentRunner(name))
        {
            return;
        }

        this.events.Enqueue(new ProcessEvent
        {
            IsStart = isStart,
            ProcessId = Convert.ToInt32(processId),
            ProcessName = name,
            CommandLine = Convert.ToString(commandLine),
        });
    }
}

/// <summary>
/// Works out which script or project a process is running, from its name and command line
/// (Section 6.5, "Matching"). For example <c>python.exe C:\code\bot\main.py</c> runs C:\code\bot\main.py.
/// </summary>
public static class ScriptPathResolver
{
    // Programs that run agent code. Others (browsers, editors, ...) are ignored.
    private static readonly HashSet<string> RunnerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "python", "pythonw", "py", "uv", "uvx", "poetry", "pipenv", "conda",
        "node", "npx", "tsx", "ts-node", "bun", "deno", "dotnet",
        "jupyter", "jupyter-lab", "jupyter-notebook", "ipython", "streamlit", "chainlit", "uvicorn", "fastapi",
        "langgraph", "crewai", "mastra", "n8n",
    };

    private static readonly string[] ScriptExtensions =
    {
        ".py", ".ipynb", ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".tsx", ".csproj", ".dll",
    };

    // Options whose value is a project or notebook folder.
    private static readonly string[] FolderOptionPrefixes =
    {
        "--notebook-dir=", "--ServerApp.root_dir=", "--NotebookApp.notebook_dir=", "--project=", "--directory=",
    };

    /// <summary>"python.exe" and "C:\Python312\python.exe" both count as "python".</summary>
    public static bool IsAgentRunner(string processName)
    {
        string name = Path.GetFileNameWithoutExtension(processName);
        return RunnerNames.Contains(name);
    }

    /// <summary>
    /// Script, project or folder paths named on the command line. They may be relative (WMI does not
    /// report a process's working folder); the caller decides how to match those.
    /// </summary>
    public static List<string> FindScriptPaths(string commandLine)
    {
        List<string> paths = new List<string>();
        List<string> arguments = WindowsCommandLine.Split(commandLine);

        // arguments[0] is the program itself.
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];

            // dotnet run --project src/Bot ; uv run --directory C:\code\bot ...
            bool isFolderOption = argument == "--project" || argument == "--directory" || argument == "--notebook-dir";
            if (isFolderOption && index + 1 < arguments.Count)
            {
                AddPath(paths, arguments[index + 1]);
                index++;
                continue;
            }

            string folderValue = ValueOfFolderOption(argument);
            if (folderValue != null)
            {
                AddPath(paths, folderValue);
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (HasScriptExtension(argument))
            {
                AddPath(paths, argument);
            }
        }

        return paths;
    }

    private static string ValueOfFolderOption(string argument)
    {
        foreach (string prefix in FolderOptionPrefixes)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument.Substring(prefix.Length);
            }
        }

        return null;
    }

    private static bool HasScriptExtension(string argument)
    {
        foreach (string extension in ScriptExtensions)
        {
            if (argument.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddPath(List<string> paths, string path)
    {
        string cleanPath = path.Trim().Trim('"');
        if (cleanPath.Length > 0 && !paths.Contains(cleanPath))
        {
            paths.Add(cleanPath);
        }
    }
}

/// <summary>Splits a Windows command line into arguments, the way most programs (and the C runtime) do.</summary>
public static class WindowsCommandLine
{
    /// <summary>
    /// Rules: spaces separate arguments; double quotes group text with spaces; a backslash before a quote
    /// escapes it. Example: <c>python "C:\My Code\agent.py" --verbose</c> gives three arguments.
    /// </summary>
    public static List<string> Split(string commandLine)
    {
        List<string> arguments = new List<string>();
        StringBuilder current = new StringBuilder();
        bool insideQuotes = false;
        bool hasArgument = false;

        for (int index = 0; index < commandLine.Length; index++)
        {
            char character = commandLine[index];

            if (character == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"'); // \" is a literal quote
                index++;
                hasArgument = true;
                continue;
            }

            if (character == '"')
            {
                insideQuotes = !insideQuotes;
                hasArgument = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !insideQuotes)
            {
                if (hasArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }

                continue;
            }

            current.Append(character);
            hasArgument = true;
        }

        if (hasArgument)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
