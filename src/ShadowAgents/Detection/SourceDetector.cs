using System.Buffers;
using System.Text.Json.Serialization;

namespace ShadowAgents;

/// <summary>
/// The single entry point for detection: picks the right language detector from the file extension.
/// </summary>
public class SourceDetector
{
    private static readonly HashSet<string> PythonExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" };
    private static readonly HashSet<string> NotebookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ipynb" };
    private static readonly HashSet<string> CSharpExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
    private static readonly HashSet<string> JavaScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs",
    };

    private readonly PythonAgentDetector pythonDetector;
    private readonly JavaScriptAgentDetector javaScriptDetector;
    private readonly CSharpAgentDetector cSharpDetector;

    public SourceDetector()
    {
        pythonDetector = new PythonAgentDetector();
        javaScriptDetector = new JavaScriptAgentDetector();
        cSharpDetector = new CSharpAgentDetector();
    }

    /// <summary>True when a file name has an extension we have a detector for, and is not generated code.</summary>
    public static bool IsSupportedSourceFile(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        bool hasSupportedExtension = PythonExtensions.Contains(extension) || NotebookExtensions.Contains(extension)
            || CSharpExtensions.Contains(extension) || JavaScriptExtensions.Contains(extension);
        if (!hasSupportedExtension)
        {
            return false;
        }

        // Type declarations, minified bundles and generated C# never define agents by hand.
        string lowerName = fileName.ToLowerInvariant();
        bool isGenerated = lowerName.EndsWith(".d.ts") || lowerName.EndsWith(".min.js") || lowerName.EndsWith(".bundle.js")
            || lowerName.EndsWith(".g.cs") || lowerName.EndsWith(".designer.cs");
        return !isGenerated;
    }

    public FileDetectionResult Detect(string sourceText, string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (PythonExtensions.Contains(extension))
        {
            return pythonDetector.Detect(sourceText, filePath);
        }

        if (NotebookExtensions.Contains(extension))
        {
            return pythonDetector.DetectNotebook(sourceText, filePath);
        }

        if (JavaScriptExtensions.Contains(extension))
        {
            return javaScriptDetector.Detect(sourceText, filePath);
        }

        if (CSharpExtensions.Contains(extension))
        {
            return cSharpDetector.Detect(sourceText, filePath);
        }

        return FileDetectionResult.Empty();
    }
}

/// <summary>
/// The cheap first filter (Stage 3, step 3): does the file mention any framework import root at all?
/// Most source files on a machine fail this check and are never parsed.
/// </summary>
public sealed class FrameworkPreCheck
{
    private readonly List<string> frameworkTokens;

    public FrameworkPreCheck(IEnumerable<FrameworkRule> rules)
    {
        HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameworkRule rule in rules)
        {
            foreach (string token in rule.PreCheckTokens())
            {
                tokens.Add(token);
            }
        }

        frameworkTokens = new List<string>(tokens);
    }

    public bool MentionsAnyFramework(string sourceText)
    {
        foreach (string token in frameworkTokens)
        {
            if (sourceText.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

public enum SourceLanguage
{
    Python,
    TypeScript,
    CSharp,
    LowCode,

    /// <summary>Not code: an AI client's configuration file (MCP servers, Section 12).</summary>
    Config,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// The four discovery states from Section 3 of the design doc. Higher states include the lower ones.
/// Only <see cref="Implemented"/> and <see cref="Running"/> count as agents.
/// </summary>
public enum DiscoveryState
{
    Installed,
    Referenced,
    Implemented,
    Running,
}

public static class ModelSources
{
    /// <summary>The model was read from source code.</summary>
    public const string Static = "static";

    /// <summary>The model was seen in proxy traffic (Stage 4, later step).</summary>
    public const string Observed = "observed";
}

public sealed class ModelReference

{

    public ModelReference(string id, string provider, string source = ModelSources.Static)

    {

        Id = id;

        Provider = provider;

        Source = source;

    }


    public string Id { get; }

    public string Provider { get; }

    public string Source { get; }


    // Two references to the same model are the same (used to avoid listing a model twice).

    public override bool Equals(object other)

    {

        ModelReference otherModel = other as ModelReference;

        if (otherModel == null)

        {

            return false;

        }


        return Id == otherModel.Id && Provider == otherModel.Provider && Source == otherModel.Source;

    }


    public override int GetHashCode()

    {

        string combined = Id + "|" + Provider + "|" + Source;

        return combined.GetHashCode();

    }


    public override string ToString()

    {

        return Id + " (" + Provider + ", " + Source + ")";

    }

}

/// <summary>Where an agent was found. <see cref="Cell"/> is set only for Jupyter notebooks.</summary>
public sealed class Evidence
{
    public Evidence(string file, int line, string snippet, Nullable<int> cell = null)
    {
        File = file;
        Line = line;
        Snippet = snippet;
        Cell = cell;
    }

    public string File { get; }
    public int Line { get; }
    public string Snippet { get; }
    public Nullable<int> Cell { get; }
}

/// <summary>One agent definition found in one source file.</summary>
public sealed class DetectedAgent
{
    public string FrameworkKey { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The variable the agent was assigned to, if any. Used to link tools and wrappers.</summary>
    public string VariableName { get; set; }

    public string Description { get; set; }
    public List<ModelReference> Models { get; set; } = new List<ModelReference>();
    public List<string> Tools { get; set; } = new List<string>();
    public List<string> Capabilities { get; set; } = new List<string>();
    public List<string> RiskFlags { get; set; } = new List<string>();
    public Confidence Confidence { get; set; }
    public Evidence Evidence { get; set; } = new Evidence("", 0, "");

    /// <summary>True when the code around the agent shows a human approval step.</summary>
    public bool HasHumanApproval { get; set; }

    /// <summary>
    /// A model variable that could not be resolved inside the file. The scanner tries to resolve it
    /// against model assignments in the other files of the same project.
    /// </summary>
    public string UnresolvedModelVariable { get; set; }

    /// <summary>Character offset of the constructor in the file. Only meaningful during detection.</summary>
    [JsonIgnore]
    public int AnchorOffset { get; set; }

    /// <summary>Character offset just past the constructor's closing bracket. Only meaningful during detection.</summary>
    [JsonIgnore]
    public int ConstructorEndOffset { get; set; }
}

/// <summary>Everything the detector learned from one source file. This is what the scanner caches.</summary>
public sealed class FileDetectionResult
{
    /// <summary>Framework keys this file imports (the Referenced state).</summary>
    public List<string> ReferencedFrameworks { get; set; } = new List<string>();

    public List<DetectedAgent> Agents { get; set; } = new List<DetectedAgent>();

    /// <summary>
    /// Variables in this file that hold a model, for example <c>llm = ChatOpenAI(model="gpt-4o")</c>.
    /// Lets the scanner link an agent in one file to a model defined in another.
    /// </summary>
    public Dictionary<string, ModelReference> ModelAssignments { get; set; } = new Dictionary<string, ModelReference>();

    /// <summary>Functions marked with a framework tool decorator such as <c>@tool</c>.</summary>
    public List<string> DecoratedTools { get; set; } = new List<string>();

    /// <summary>C# methods marked <c>[McpServerTool]</c>. They belong to the project's MCP server, wherever it is set up.</summary>
    public List<string> McpServerTools { get; set; } = new List<string>();

    /// <summary>True when a per-file safety cap stopped detection early (hostile or very unusual input).</summary>
    public bool DetectionCapHit { get; set; }

    public static FileDetectionResult Empty()
    {
        return new FileDetectionResult();
    }
}

/// <summary>Capability categories from Section 7.3 of the design doc.</summary>
public static class Capabilities
{
    public const string WebAccess = "web_access";
    public const string EmailMessaging = "email_messaging";
    public const string Filesystem = "filesystem";
    public const string CodeExecution = "code_execution";
    public const string Database = "database";
    public const string Financial = "financial";
    public const string Destructive = "destructive";
    public const string IdentityAdmin = "identity_admin";
    public const string Delegation = "delegation";
    public const string Mcp = "mcp";
}

public static class RiskFlags
{
    public const string FinancialWithoutApproval = "financial_without_approval";
    public const string DestructiveWithoutApproval = "destructive_without_approval";
    public const string CodeExecution = "code_execution";

    /// <summary>The agent can both read from the web and send messages out, so data can leave.</summary>
    public const string ExfiltrationCombo = "exfiltration_combo";
}

/// <summary>Small path helpers.</summary>
public static class PathHelpers
{
    /// <summary>The folder that holds <paramref name="path"/>, or the path itself when it has no parent (a drive root).</summary>
    public static string FolderOf(string path)
    {
        string folder = Path.GetDirectoryName(path);
        if (folder == null)
        {
            return path;
        }

        return folder;
    }

    /// <summary>
    /// <paramref name="filePath"/> relative to <paramref name="rootPath"/> ("src\\agent.py"), or the full path
    /// when the file is not inside the root.
    /// </summary>
    public static string RelativeTo(string rootPath, string filePath)
    {
        string rootWithSeparator = rootPath.TrimEnd('\\', '/') + "\\";
        if (filePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return filePath.Substring(rootWithSeparator.Length);
        }

        return filePath;
    }

    /// <summary>True for a full path that does not depend on the current folder: "C:\\code\\a.py" or "\\\\server\\share\\a.py".</summary>
    public static bool IsFullyQualified(string path)
    {
        bool isDrivePath = path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        bool isNetworkPath = path.StartsWith("\\\\", StringComparison.Ordinal);
        return isDrivePath || isNetworkPath;
    }
}

/// <summary>Small list helpers used by every detector.</summary>
public static class ListHelpers
{
    /// <summary>Adds the item unless the list already contains it.</summary>
    public static void AddDistinct<T>(List<T> target, T item)
    {
        if (!target.Contains(item))
        {
            target.Add(item);
        }
    }

    /// <summary>Sets <paramref name="count"/> items of <paramref name="array"/>, starting at <paramref name="start"/>, to <paramref name="value"/>.</summary>
    public static void FillRange<T>(T[] array, T value, int start, int count)
    {
        for (int index = start; index < start + count; index++)
        {
            array[index] = value;
        }
    }

    /// <summary>The value stored under <paramref name="key"/>, or the default (null for classes) when there is none.</summary>
    public static TValue ValueOrDefault<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
    {
        TValue value;
        dictionary.TryGetValue(key, out value);
        return value;
    }

    /// <summary>Adds each item unless the list already contains it.</summary>
    public static void AddDistinct<T>(List<T> target, IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            AddDistinct(target, item);
        }
    }
}
