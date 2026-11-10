using System.Text.Json;

namespace ShadowAgents;

/// <summary>
/// START HERE. ShadowAgents.exe has two commands:
///
///   ShadowAgents scan [folder]   find AI agents now (in one folder, or on every fixed drive), save
///                                one JSON line per finding in the "results" folder, then exit
///                                → Scanner\ScanRunner.cs
///
///   ShadowAgents watch           keep running: react to file changes and process starts, run
///                                scheduled scans, and keep an inventory of agents
///                                → Watcher\WatcherService.cs
///
/// Every setting is hard-coded in Settings.cs.
///
/// "watch" runs each scan as a separate "ShadowAgents scan --job job.json" process, so a crash while
/// reading files cannot take the watcher down, and the scan's memory is freed when it ends.
/// </summary>
public static class Program
{
    private const int ExitError = 1;
    private const int ExitBadArguments = 2;

    // What Ctrl+C stops: the running scan, or the watcher.
    private static ScanStopSignal scanStopSignal;
    private static CancellationTokenSource watchStop;

    private const string Usage =
        """
        Shadow AI agent discovery

          ShadowAgents scan <folder>   find agents in a folder (and everything below it)
          ShadowAgents scan            find agents on every fixed drive (best as administrator)
          ShadowAgents watch           keep watching for agents (Ctrl+C to stop)

        Scan results are saved in the project's "results" folder (each scan prints its file path).
        Exit codes: 0 done, 1 error, 2 bad arguments, 3 stopped early (Ctrl+C).
        """;

    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            return RunScan(WordsAfterCommand(args));
        }

        if (args.Length == 1 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            return RunWatch();
        }

        Console.WriteLine(Usage);
        if (args.Length == 0)
        {
            return 0;
        }

        return ExitBadArguments;
    }

    // ------------------------------------------------------------ scan

    /// <summary>
    /// "scan" = every fixed drive, "scan &lt;folder&gt;" = one folder,
    /// "scan --job &lt;file&gt;" = how the watcher calls it (no progress messages, output where the job says).
    /// </summary>
    private static int RunScan(string[] args)
    {
        ScanJob job;
        TextWriter progressLog = Console.Error;
        try
        {
            if (args.Length == 2 && args[0] == "--job")
            {
                job = ScanJob.LoadFromFile(args[1]);
                progressLog = TextWriter.Null;
            }
            else if (args.Length == 1)
            {
                job = new ScanJob { Roots = { ExistingFolder(args[0]) }, OutputPath = DefaultOutputPath(), CachePath = DefaultCachePath() };
            }
            else if (args.Length == 0)
            {
                job = new ScanJob { Volumes = { "auto" }, OutputPath = DefaultOutputPath(), CachePath = DefaultCachePath() };
            }
            else
            {
                Console.Error.WriteLine(Usage);
                return ExitBadArguments;
            }
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException
                                          || exception is JsonException || exception is InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitBadArguments;
        }

        string resourceLimits = ResourceLimiter.Apply(Settings.ScanCpuPercent, Settings.ScanMemoryMb);

        // Ctrl+C (or the watcher asking us to stop) ends cleanly: the cache is saved and a summary is written.
        using ScanStopSignal stopSignal = new ScanStopSignal();
        scanStopSignal = stopSignal;
        Console.CancelKeyPress += StopScanOnCtrlC;

        try
        {
            ScanRunner runner = new ScanRunner(job, stopSignal, progressLog);
            return runner.Run(resourceLimits);
        }
        catch (Exception exception)
        {
            // Last-resort guard: report and exit non-zero so the watcher can retry. Never crash silently.
            Console.Error.WriteLine("Scan failed: " + exception);
            return ExitError;
        }
    }

    private static string ExistingFolder(string folder)
    {
        // "%USERPROFILE%\code" becomes "C:\Users\alice\code", so scripts can use variables.
        string expandedFolder = Environment.ExpandEnvironmentVariables(folder);
        if (!Directory.Exists(expandedFolder))
        {
            throw new ArgumentException("Folder '" + expandedFolder + "' does not exist.");
        }

        return expandedFolder;
    }

    /// <summary>
    /// A new file for every scan, named after the time it started, so earlier results are never overwritten:
    /// D:\AgentDiscovery\results\scan-2026-11-05_19-02-31.jsonl
    /// </summary>
    public static string DefaultOutputPath()
    {
        string fileName = "scan-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".jsonl";
        return Path.Combine(DefaultResultsFolder(), fileName);
    }

    /// <summary>
    /// The "results" folder in the project (the folder that holds ShadowAgents.slnx) when the program runs
    /// from a build of the source code. A published exe copied to another machine has no project folder,
    /// so it uses %LOCALAPPDATA%\ShadowAgents\results instead.
    /// </summary>
    public static string DefaultResultsFolder()
    {
        string projectFolder = FindProjectFolder();
        if (projectFolder != null)
        {
            return Path.Combine(projectFolder, "results");
        }

        return Path.Combine(LocalAppDataFolder(), "results");
    }

    private static string DefaultCachePath()
    {
        return Path.Combine(LocalAppDataFolder(), "scan-cache.db");
    }

    private static string LocalAppDataFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowAgents");
    }

    /// <summary>
    /// Walks up from the program's own folder (for example src\ShadowAgents\bin\Debug\net10.0) looking for
    /// ShadowAgents.slnx. The program's folder is used rather than the current directory, so the answer is
    /// the same whether it is started from VS Code, Visual Studio or a terminal opened anywhere.
    /// </summary>
    private static string FindProjectFolder()
    {
        DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ShadowAgents.slnx")))
            {
                return folder.FullName;
            }

            folder = folder.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------ watch

    /// <summary>
    /// Runs the watcher until Ctrl+C. In production this logic lives inside the endpoint agent
    /// (design doc Section 4.2); this command hosts it on its own so it can be run and tested.
    /// </summary>
    private static int RunWatch()
    {
        // The inventory, outbox, telemetry, cache and journal positions are kept here.
        string stateFolder = Path.Combine(LocalAppDataFolder(), "Watcher");
        if (SystemConditions.IsElevated())
        {
            stateFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShadowAgents");
        }

        using CancellationTokenSource stop = new CancellationTokenSource();
        watchStop = stop;
        Console.CancelKeyPress += StopWatchOnCtrlC;

        using WatcherService watcher = new WatcherService(stateFolder, Console.Out);
        watcher.Start();
        Console.WriteLine("Watching. Press Ctrl+C to stop.");
        watcher.RunUntilStopped(stop.Token);
        return 0;
    }

    private static void StopScanOnCtrlC(object sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true; // do not kill the process: let the scan finish cleanly
        scanStopSignal.Stop("cancelled");
    }

    private static void StopWatchOnCtrlC(object sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true; // do not kill the process: let the watcher save its state
        watchStop.Cancel();
    }

    /// <summary>Everything after the command word: for "scan C:\code" that is ["C:\code"].</summary>
    private static string[] WordsAfterCommand(string[] args)
    {
        string[] words = new string[args.Length - 1];
        Array.Copy(args, 1, words, 0, words.Length);
        return words;
    }
}
