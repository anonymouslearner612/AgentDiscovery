using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;

namespace ShadowAgents;

// The output schema from Section 10 of the design doc. The scanner writes one JSON line per record;
// the watcher reads them back. "recordType" says whether a line is a finding or the closing summary.

public static class RecordTypes
{
    public const string Finding = "finding";
    public const string Summary = "summary";
}

/// <summary>The four discovery states (Section 3), as written in the output.</summary>
public static class FindingStates
{
    public const string Installed = "installed";
    public const string Referenced = "referenced";
    public const string Implemented = "implemented";
    public const string Running = "running";
}

public class FindingRecord
{
    public string RecordType { get; set; } = RecordTypes.Finding;
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="FindingStates"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>"low", "medium" or "high". Set for agents only (implemented or running).</summary>
    public string Confidence { get; set; }

    public FrameworkRecord Framework { get; set; } = new FrameworkRecord();

    /// <summary>Empty for inventory records (installed, referenced).</summary>
    public AgentRecord Agent { get; set; } = new AgentRecord();

    public ProjectRecord Project { get; set; }

    /// <summary>Set for installed records: the environment the package is installed in.</summary>
    public EnvironmentRecord Environment { get; set; }

    public List<EvidenceRecord> Evidence { get; set; } = new List<EvidenceRecord>();
    public RuntimeRecord Runtime { get; set; } = new RuntimeRecord();
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>True for agents (implemented or running); false for inventory.</summary>
    public bool IsAgent()
    {
        return State == FindingStates.Implemented || State == FindingStates.Running;
    }
}

public class FrameworkRecord
{
    public string Key { get; set; } = "";
    public string Display { get; set; } = "";

    /// <summary>"python", "typescript", "csharp" or "lowcode".</summary>
    public string Language { get; set; } = "";

    public string Version { get; set; }
}

public class AgentRecord
{
    public string Name { get; set; }
    public string Description { get; set; }
    public List<ModelRecord> Models { get; set; } = new List<ModelRecord>();
    public List<string> Tools { get; set; } = new List<string>();
    public List<string> Capabilities { get; set; } = new List<string>();
    public List<string> RiskFlags { get; set; } = new List<string>();
}

public class ModelRecord
{
    public string Id { get; set; } = "";
    public string Provider { get; set; }

    /// <summary>"static" (read from code) or "observed" (seen in proxy traffic).</summary>
    public string Source { get; set; } = "static";
}

public class ProjectRecord
{
    public string Root { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manifest { get; set; }
    public string GitRemote { get; set; }
    public string Owner { get; set; }
    public bool IsTest { get; set; }
}

public class EnvironmentRecord
{
    public string Path { get; set; } = "";

    /// <summary>"PyPI", "Npm" or "NuGet".</summary>
    public string Ecosystem { get; set; } = "";
}

public class EvidenceRecord
{
    /// <summary>Path relative to the project root, with forward slashes ("src/crew.py").</summary>
    public string File { get; set; } = "";

    /// <summary>1-based line; 0 when the evidence is not a line of code (a package folder, a database row).</summary>
    public int Line { get; set; }

    public string Snippet { get; set; } = "";

    /// <summary>0-based cell index, for Jupyter notebooks only.</summary>
    public Nullable<int> Cell { get; set; }
}

/// <summary>Stage 4 (Running). Filled by the watcher; the scanner writes "not running" except for n8n.</summary>
public class RuntimeRecord
{
    public bool Running { get; set; }
    public Nullable<DateTimeOffset> LastSeenRunning { get; set; }

    /// <summary>Process descriptions such as "python.exe (pid 1234)".</summary>
    public List<string> Processes { get; set; } = new List<string>();

    /// <summary>Models seen in proxy traffic from this agent's processes.</summary>
    public List<ModelRecord> ObservedModels { get; set; } = new List<ModelRecord>();
}

public class SummaryRecord
{
    public string RecordType { get; set; } = RecordTypes.Summary;
    public int SchemaVersion { get; set; } = 1;
    public string Host { get; set; } = "";
    public List<string> Roots { get; set; } = new List<string>();
    public List<string> Volumes { get; set; } = new List<string>();

    /// <summary>How each volume or root was listed, for example "C: master-file-table" or "D: folder-walk".</summary>
    public List<string> ListingMethods { get; set; } = new List<string>();

    public DateTimeOffset StartedAt { get; set; }
    public long DurationMs { get; set; }
    public int DirectoriesVisited { get; set; }
    public int DirectoriesInaccessible { get; set; }
    public int ManifestsRead { get; set; }
    public int CandidateSourceFiles { get; set; }
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
    public string ResourceLimits { get; set; }
}

public static class FindingId
{
    /// <summary>
    /// Stable id for a finding (Section 10): sha256 of host, project root, framework, file and agent name.
    /// The same agent keeps the same id across scans, so only changes need to be sent to the backend.
    /// </summary>
    public static string Compute(string host, string root, string frameworkKey, string file, string agentName)
    {
        string idSource = string.Join("|", host, root, frameworkKey, file, agentName).ToLowerInvariant();
        return Sha256Hex(idSource);
    }

    /// <summary>
    /// Id for the second, third, ... different finding that <see cref="Compute"/> gives the same id, for
    /// example two agents named "workflow" in one file. Occurrence 2 is the second one in scan order, so
    /// the id stays the same across scans as long as the agents keep their order in the file.
    /// </summary>
    public static string ForRepeat(string firstId, int occurrence)
    {
        return Sha256Hex(firstId + "#" + occurrence);
    }

    private static string Sha256Hex(string text)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}

/// <summary>
/// The watcher-to-scanner contract (design doc Section 5): what to scan and where to put the results.
/// The watcher writes this as a JSON "job file" and starts <c>ShadowAgents scan --job job.json</c>.
/// Everything else (budgets, file limits) is hard-coded in <see cref="Settings"/>.
/// </summary>
public class ScanJob
{
    /// <summary>Folders to discover in. Each may contain any number of projects, or be one project.</summary>
    public List<string> Roots { get; set; } = new List<string>();

    /// <summary>
    /// Whole volumes to discover on, such as "C:". The single value "auto" means every fixed drive.
    /// NTFS volumes are listed through the master file table; other volumes are walked folder by folder.
    /// </summary>
    public List<string> Volumes { get; set; } = new List<string>();

    /// <summary>SQLite cache file. When empty, nothing is cached on disk (an in-memory cache is used for the run).</summary>
    public string CachePath { get; set; }

    /// <summary>JSON lines output file. Standard output when empty.</summary>
    public string OutputPath { get; set; }

    public static ScanJob LoadFromFile(string jobFilePath)
    {
        string jobJson = File.ReadAllText(jobFilePath);
        ScanJob job = JsonSerializer.Deserialize<ScanJob>(jobJson, ContractJson.ReadOptions);
        if (job == null)
        {
            throw new InvalidDataException($"Job file '{jobFilePath}' is empty.");
        }

        return job;
    }

    public void SaveToFile(string jobFilePath)
    {
        string jobJson = JsonSerializer.Serialize(this, ContractJson.WriteIndentedOptions);
        File.WriteAllText(jobFilePath, jobJson);
    }
}

/// <summary>JSON settings shared by the scanner and the watcher, so both read and write the same format.</summary>
public static class ContractJson
{
    /// <summary>One record per line, camelCase property names.</summary>
    public static readonly JsonSerializerOptions LineOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Keep paths and model names readable. The output goes to our own backend, not into a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>For job files: forgiving when reading.</summary>
    public static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions WriteIndentedOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
