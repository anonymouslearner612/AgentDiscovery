using System.Text;

namespace ShadowAgents;

/// <summary>What a positional argument of an agent constructor means.</summary>
public enum ArgumentRole
{
    Ignored,
    Name,
    Description,
    Model,
    Tools,
}

/// <summary>A call that starts an agent definition, such as <c>Agent(</c> or <c>create_react_agent(</c>.</summary>
public sealed class AgentConstructor
{
    /// <summary>The called name. May contain dots, for example <c>OpenAIAgent.from_tools</c>.</summary>
    public string CallName { get; set; }

    /// <summary>Meaning of positional arguments, by position. Keyword arguments are handled by the rule.</summary>
    public IReadOnlyList<ArgumentRole> PositionalArgs { get; set; } = new List<ArgumentRole>();

    /// <summary>
    /// True for graph builders such as LangGraph's <c>StateGraph(</c>. These have no model or tools
    /// arguments; they are filled from the rest of the file, and get low confidence without nodes.
    /// </summary>
    public bool IsGraphBuilder { get; set; }

    /// <summary>Capabilities the constructor implies by itself, for example code execution for <c>CodeAgent</c>.</summary>
    public IReadOnlyList<string> ImpliedCapabilities { get; set; } = new List<string>();

    /// <summary>
    /// True when the call is only an agent if tools are attached. Vercel's <c>generateText(...)</c>
    /// without <c>tools</c> is a plain LLM call, not an agent.
    /// </summary>
    public bool RequiresTools { get; set; }

    /// <summary>
    /// True for generic names such as <c>query(</c> or <c>agent(</c>: they count only when the file
    /// imports that exact name from the framework.
    /// </summary>
    public bool RequiresExplicitImport { get; set; }
}

/// <summary>
/// A call that registers a tool with an agent outside its constructor, such as AutoGen's
/// <c>register_function(fn, caller=assistant, executor=user_proxy)</c>.
/// </summary>
public sealed class ToolRegistrationCall
{
    public ToolRegistrationCall(string callName, int toolPosition, string agentArgument)
    {
        CallName = callName;
        ToolPosition = toolPosition;
        AgentArgument = agentArgument;
    }

    public string CallName { get; }
    public int ToolPosition { get; }
    public string AgentArgument { get; }
}

/// <summary>
/// Detection rules for one framework (Section 7.1). Rules are data; the detector holds the logic.
/// </summary>
public sealed class FrameworkRule
{
    public string Key { get; set; }
    public SourceLanguage Language { get; set; }

    /// <summary>
    /// Module roots that prove the file references the framework. A trailing <c>*</c> matches any
    /// module whose name starts with the root, so <c>langchain*</c> matches <c>langchain_core</c>.
    /// </summary>
    public IReadOnlyList<string> ModuleRoots { get; set; }

    /// <summary>
    /// When not empty, an import counts only if it is a from-import of one of these names.
    /// Used for the OpenAI Agents SDK, whose module <c>agents</c> is also a common local folder name.
    /// </summary>
    public IReadOnlyList<string> RequiredImportedNames { get; set; } = new List<string>();

    public IReadOnlyList<AgentConstructor> Constructors { get; set; }

    public IReadOnlyList<string> NameArgs { get; set; } = new List<string> { "name" };
    public IReadOnlyList<string> DescriptionArgs { get; set; } = new List<string>();

    /// <summary>When true, every description argument found is joined (CrewAI goal + backstory).</summary>
    public bool CombineDescriptionArgs { get; set; }

    public IReadOnlyList<string> ModelArgs { get; set; } = new List<string> { "model" };
    public IReadOnlyList<string> ToolArgs { get; set; } = new List<string> { "tools" };

    /// <summary>Decorators that define a tool, such as <c>@tool</c> or <c>@function_tool</c>.</summary>
    public IReadOnlyList<string> ToolDecorators { get; set; } = new List<string>();

    /// <summary>
    /// Decorators called on an agent variable, such as Pydantic AI's <c>@support_agent.tool</c>.
    /// The tool is attached to the agent held in that variable.
    /// </summary>
    public IReadOnlyList<string> AgentBoundToolDecorators { get; set; } = new List<string>();

    /// <summary>Keyword arguments that grant a capability unless their value is False, None or empty.</summary>
    public IReadOnlyDictionary<string, string> CapabilityArgs { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Keyword arguments whose value is another agent of the same framework that this one wraps,
    /// such as LangChain's <c>AgentExecutor(agent=...)</c>. The wrapped agent is merged into the wrapper.
    /// </summary>
    public IReadOnlyList<string> WrappedAgentArgs { get; set; } = new List<string>();

    /// <summary>
    /// Calls whose first argument is a list of tools for graph builders, such as <c>ToolNode([...])</c>
    /// and <c>llm.bind_tools([...])</c>.
    /// </summary>
    public IReadOnlyList<string> GraphToolCalls { get; set; } = new List<string>();

    /// <summary>The method that adds a node to a graph (<c>add_node</c> in Python, <c>addNode</c> in JS).</summary>
    public string GraphNodeCall { get; set; } = "add_node";

    public IReadOnlyList<ToolRegistrationCall> ToolRegistrationCalls { get; set; } = new List<ToolRegistrationCall>();

    /// <summary>
    /// TS/JS only: a property that holds more settings, such as the Claude Agent SDK's
    /// <c>query({ prompt, options: { systemPrompt, allowedTools } })</c>.
    /// </summary>
    public string NestedOptionsProperty { get; set; }

    /// <summary>Plain strings for the cheap pre-check: if none appears in a file, the rule cannot match.</summary>
    public List<string> PreCheckTokens()
    {
        List<string> tokens = new List<string>();
        foreach (string root in ModuleRoots)
        {
            string plainRoot = root.TrimEnd('*');

            // A short package name like "ai" appears inside countless words ("main", "said"),
            // so look for it as a quoted import specifier instead: "ai" or 'ai/rsc'.
            if (plainRoot.Length < 4)
            {
                tokens.Add("\"" + plainRoot);
                tokens.Add("'" + plainRoot);
            }
            else
            {
                tokens.Add(plainRoot);
            }
        }

        return tokens;
    }

    /// <summary>
    /// True when an imported module belongs to this framework. Python modules are separated by '.'
    /// ("langchain.agents"); JavaScript packages by '/' ("@langchain/core/tools").
    /// </summary>
    public bool MatchesModule(string moduleName)
    {
        string separator = "/";
        if (Language == SourceLanguage.Python)
        {
            separator = ".";
        }
        foreach (string root in ModuleRoots)
        {
            if (root.EndsWith("*", StringComparison.Ordinal))
            {
                string prefix = root.Substring(0, root.Length - 1);
                if (moduleName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (moduleName == root || moduleName.StartsWith(root + separator, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class FrameworkInfo

{

    public FrameworkInfo(string key, string displayName, SourceLanguage language)

    {

        Key = key;

        DisplayName = displayName;

        Language = language;

    }


    public string Key { get; }

    public string DisplayName { get; }

    public SourceLanguage Language { get; }

}

/// <summary>Every framework in scope for Phase 1 (Section 2), with its display name and language.</summary>
public static class FrameworkCatalog
{
    private static readonly Dictionary<string, FrameworkInfo> FrameworksByKey = BuildCatalog();

    public static FrameworkInfo Describe(string frameworkKey)
    {
        if (FrameworksByKey.TryGetValue(frameworkKey, out FrameworkInfo info))
        {
            return info;
        }

        return new FrameworkInfo(frameworkKey, frameworkKey, SourceLanguage.Python);
    }

    private static Dictionary<string, FrameworkInfo> BuildCatalog()
    {
        List<FrameworkInfo> frameworks = new List<FrameworkInfo>
        {
            // Python
            new("langchain", "LangChain", SourceLanguage.Python),
            new("langgraph", "LangGraph", SourceLanguage.Python),
            new("crewai", "CrewAI", SourceLanguage.Python),
            new("autogen", "AutoGen 0.2 / AG2", SourceLanguage.Python),
            new("autogen_agentchat", "AutoGen 0.4+", SourceLanguage.Python),
            new("semantic_kernel", "Semantic Kernel", SourceLanguage.Python),
            new("llama_index", "LlamaIndex", SourceLanguage.Python),
            new("pydantic_ai", "Pydantic AI", SourceLanguage.Python),
            new("openai_agents", "OpenAI Agents SDK", SourceLanguage.Python),
            new("strands", "AWS Strands", SourceLanguage.Python),
            new("claude_agent_sdk", "Claude Agent SDK", SourceLanguage.Python),
            new("haystack", "Haystack", SourceLanguage.Python),
            new("google_adk", "Google ADK", SourceLanguage.Python),
            new("smolagents", "smolagents", SourceLanguage.Python),

            // TypeScript / JavaScript
            new("langchain_js", "LangChain.js", SourceLanguage.TypeScript),
            new("langgraph_js", "LangGraph.js", SourceLanguage.TypeScript),
            new("vercel_ai_sdk", "Vercel AI SDK", SourceLanguage.TypeScript),
            new("mastra", "Mastra", SourceLanguage.TypeScript),
            new("openai_agents_js", "OpenAI Agents SDK (JS)", SourceLanguage.TypeScript),
            new("llamaindex_ts", "LlamaIndex.TS", SourceLanguage.TypeScript),
            new("claude_agent_sdk_ts", "Claude Agent SDK (TS)", SourceLanguage.TypeScript),

            // .NET
            new("semantic_kernel_dotnet", "Semantic Kernel (.NET)", SourceLanguage.CSharp),
            new("microsoft_agent_framework", "Microsoft Agent Framework", SourceLanguage.CSharp),
            new("autogen_dotnet", "AutoGen.NET", SourceLanguage.CSharp),
            new("microsoft_extensions_ai", "Microsoft.Extensions.AI", SourceLanguage.CSharp),

            // Low-code
            new("n8n", "n8n", SourceLanguage.LowCode),
        };

        Dictionary<string, FrameworkInfo> frameworksByKey = new Dictionary<string, FrameworkInfo>(StringComparer.Ordinal);
        foreach (FrameworkInfo framework in frameworks)
        {
            frameworksByKey.Add(framework.Key, framework);
        }

        return frameworksByKey;
    }
}

public enum PackageEcosystem
{
    PyPI,
    Npm,
    NuGet,
}

/// <summary>
/// Maps package names (as they appear in manifests, <c>*.dist-info</c> folders, <c>node_modules</c>
/// and the NuGet cache) to framework keys. Used for the Installed and Referenced states.
/// </summary>
public static class PackageCatalog
{
    // Order matters: the first matching pattern wins, so specific names come before broad prefixes.
    // A trailing '*' means "starts with". PyPI names are normalized first (see NormalizePyPIName).
    private static readonly PackagePattern[] PyPIPackages = new PackagePattern[]
    {
        new PackagePattern("langgraph*", "langgraph"),
        new PackagePattern("langchain", "langchain"),
        new PackagePattern("langchain_*", "langchain"),
        new PackagePattern("crewai*", "crewai"),
        new PackagePattern("autogen_agentchat", "autogen_agentchat"),
        new PackagePattern("autogen_core", "autogen_agentchat"),
        new PackagePattern("autogen_ext", "autogen_agentchat"),
        new PackagePattern("pyautogen", "autogen"),
        new PackagePattern("autogen", "autogen"),
        new PackagePattern("ag2", "autogen"),
        new PackagePattern("semantic_kernel", "semantic_kernel"),
        new PackagePattern("llama_index*", "llama_index"),
        new PackagePattern("pydantic_ai*", "pydantic_ai"),
        new PackagePattern("openai_agents", "openai_agents"),
        new PackagePattern("strands_agents*", "strands"),
        new PackagePattern("claude_agent_sdk", "claude_agent_sdk"),
        new PackagePattern("haystack_ai", "haystack"),
        new PackagePattern("farm_haystack", "haystack"),
        new PackagePattern("google_adk", "google_adk"),
        new PackagePattern("smolagents", "smolagents"),
    };

    private static readonly PackagePattern[] NpmPackages = new PackagePattern[]
    {
        new PackagePattern("@langchain/langgraph*", "langgraph_js"),
        new PackagePattern("langchain", "langchain_js"),
        new PackagePattern("@langchain/*", "langchain_js"),
        new PackagePattern("ai", "vercel_ai_sdk"),
        new PackagePattern("@ai-sdk/*", "vercel_ai_sdk"),
        new PackagePattern("@mastra/*", "mastra"),
        new PackagePattern("@openai/agents*", "openai_agents_js"),
        new PackagePattern("llamaindex", "llamaindex_ts"),
        new PackagePattern("@llamaindex/*", "llamaindex_ts"),
        new PackagePattern("@anthropic-ai/claude-agent-sdk", "claude_agent_sdk_ts"),
        new PackagePattern("n8n", "n8n"),
    };

    // NuGet ids are compared in lower case.
    private static readonly PackagePattern[] NuGetPackages = new PackagePattern[]
    {
        new PackagePattern("microsoft.semantickernel*", "semantic_kernel_dotnet"),
        new PackagePattern("microsoft.agents.ai*", "microsoft_agent_framework"),
        new PackagePattern("autogen*", "autogen_dotnet"),
        new PackagePattern("microsoft.extensions.ai*", "microsoft_extensions_ai"),
    };

    public static string FindFramework(PackageEcosystem ecosystem, string packageName)
    {
        switch (ecosystem)
        {
            case PackageEcosystem.PyPI:
                return FindFirstMatch(PyPIPackages, NormalizePyPIName(packageName));
            case PackageEcosystem.Npm:
                return FindFirstMatch(NpmPackages, packageName.ToLowerInvariant());
            case PackageEcosystem.NuGet:
                return FindFirstMatch(NuGetPackages, packageName.ToLowerInvariant());
            default:
                return null;
        }
    }

    /// <summary>
    /// PEP 503 style normalization, with '_' as the separator because that is what
    /// <c>*.dist-info</c> folder names use: "LangChain-Core" and "langchain.core" both become "langchain_core".
    /// </summary>
    public static string NormalizePyPIName(string packageName)
    {
        StringBuilder normalized = new StringBuilder(packageName.Length);
        bool lastWasSeparator = false;
        foreach (char character in packageName)
        {
            bool isSeparator = character == '-' || character == '_' || character == '.';
            if (isSeparator)
            {
                if (!lastWasSeparator)
                {
                    normalized.Append('_');
                }

                lastWasSeparator = true;
                continue;
            }

            normalized.Append(char.ToLowerInvariant(character));
            lastWasSeparator = false;
        }

        return normalized.ToString();
    }

    private static string FindFirstMatch(PackagePattern[] packages, string packageName)
    {
        foreach (PackagePattern package in packages)
        {
            string pattern = package.Pattern;
            string frameworkKey = package.FrameworkKey;
            bool isPrefixPattern = pattern.EndsWith("*", StringComparison.Ordinal);
            if (isPrefixPattern && packageName.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal))
            {
                return frameworkKey;
            }

            if (!isPrefixPattern && packageName == pattern)
            {
                return frameworkKey;
            }
        }

        return null;
    }
}

/// <summary>One row of a package table: a package-name pattern and the framework it belongs to.</summary>
public class PackagePattern
{
    public PackagePattern(string pattern, string frameworkKey)
    {
        Pattern = pattern;
        FrameworkKey = frameworkKey;
    }

    /// <summary>A package name; a trailing '*' means "starts with".</summary>
    public string Pattern { get; }

    public string FrameworkKey { get; }
}
