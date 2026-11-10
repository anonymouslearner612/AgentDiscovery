namespace ShadowAgents;

/// <summary>
/// TypeScript / JavaScript framework rules from Section 7.2 of the design doc.
/// In JS, most constructors take one options object: <c>new Agent({ name, instructions, model, tools })</c>.
/// The "Args" lists of a rule name properties of that object.
/// </summary>
/// <remarks>
/// Order matters when a constructor name is shared (for example <c>Agent</c>) and the file's imports
/// cannot tell the frameworks apart: the earlier rule wins.
/// </remarks>
public static class JavaScriptFrameworkRules
{
    public static IReadOnlyList<FrameworkRule> All { get; } = new List<FrameworkRule>
    {
        LangGraphJs(),
        LangChainJs(),
        OpenAIAgentsJs(),
        Mastra(),
        VercelAiSdk(),
        LlamaIndexTs(),
        ClaudeAgentSdkTs(),
        McpServerTypeScript(),
    };

    private static FrameworkRule LangChainJs()
    {
        return new FrameworkRule
        {
            Key = "langchain_js",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "langchain", "@langchain/*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "AgentExecutor" },
                new AgentConstructor { CallName = "AgentExecutor.fromAgentAndTools" },
                new AgentConstructor { CallName = "createToolCallingAgent" },
                new AgentConstructor { CallName = "createOpenAIToolsAgent" },
                new AgentConstructor { CallName = "createOpenAIFunctionsAgent" },
                new AgentConstructor
                {
                    CallName = "initializeAgentExecutorWithOptions",
                    PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model },
                },
                new AgentConstructor { CallName = "createAgent" },
            },
            DescriptionArgs = new List<string> { "systemPrompt", "prompt" },
            ModelArgs = new List<string> { "llm", "model" },
            WrappedAgentArgs = new List<string> { "agent" },
        };
    }

    private static FrameworkRule LangGraphJs()
    {
        return new FrameworkRule
        {
            Key = "langgraph_js",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "@langchain/langgraph*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "StateGraph", IsGraphBuilder = true },
                new AgentConstructor { CallName = "createReactAgent" },
            },
            DescriptionArgs = new List<string> { "prompt", "stateModifier", "messageModifier" },
            ModelArgs = new List<string> { "llm", "model" },
            GraphToolCalls = new List<string> { "ToolNode", "bindTools" },
            GraphNodeCall = "addNode",
        };
    }

    private static FrameworkRule VercelAiSdk()
    {
        return new FrameworkRule
        {
            Key = "vercel_ai_sdk",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "ai", "@ai-sdk/*" },
            Constructors = new List<AgentConstructor>
            {
                // Without tools these are plain LLM calls, not agents.
                new AgentConstructor { CallName = "generateText", RequiresTools = true },
                new AgentConstructor { CallName = "streamText", RequiresTools = true },
                new AgentConstructor { CallName = "Agent" },
                new AgentConstructor { CallName = "Experimental_Agent" },
                new AgentConstructor { CallName = "ToolLoopAgent" },
            },
            DescriptionArgs = new List<string> { "system", "instructions" },
        };
    }

    private static FrameworkRule Mastra()
    {
        return new FrameworkRule
        {
            Key = "mastra",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "@mastra/*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "Agent" },
            },
            DescriptionArgs = new List<string> { "instructions", "description" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["agents"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule OpenAIAgentsJs()
    {
        return new FrameworkRule
        {
            Key = "openai_agents_js",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "@openai/agents*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "Agent" },
                new AgentConstructor { CallName = "Agent.create" },
            },
            DescriptionArgs = new List<string> { "instructions", "handoffDescription" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["handoffs"] = Capabilities.Delegation,
                ["mcpServers"] = Capabilities.Mcp,
            },
        };
    }

    private static FrameworkRule LlamaIndexTs()
    {
        return new FrameworkRule
        {
            Key = "llamaindex_ts",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "llamaindex", "@llamaindex/*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "OpenAIAgent" },
                new AgentConstructor { CallName = "ReActAgent" },
                new AgentConstructor { CallName = "agent", RequiresExplicitImport = true },
                new AgentConstructor { CallName = "multiAgent", RequiresExplicitImport = true },
            },
            DescriptionArgs = new List<string> { "systemPrompt", "description" },
            ModelArgs = new List<string> { "llm" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["canHandoffTo"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule ClaudeAgentSdkTs()
    {
        return new FrameworkRule
        {
            Key = "claude_agent_sdk_ts",
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "@anthropic-ai/claude-agent-sdk" },
            Constructors = new List<AgentConstructor>
            {
                // query({ prompt, options: { systemPrompt, allowedTools, model } })
                new AgentConstructor { CallName = "query", RequiresExplicitImport = true },
            },
            NestedOptionsProperty = "options",
            DescriptionArgs = new List<string> { "systemPrompt", "appendSystemPrompt" },
            ToolArgs = new List<string> { "allowedTools" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["mcpServers"] = Capabilities.Mcp,
                ["agents"] = Capabilities.Delegation,
            },
        };
    }

    /// <summary>
    /// MCP servers (Section 12): <c>const server = new McpServer({ name: "weather", version: "1.0.0" })</c>
    /// with tools added as <c>server.tool("get_forecast", ...)</c> or <c>server.registerTool(...)</c>.
    /// The older low-level <c>new Server({ name })</c> counts too. Both names are common, so they count
    /// only when imported from the MCP SDK.
    /// </summary>
    private static FrameworkRule McpServerTypeScript()
    {
        return new FrameworkRule
        {
            Key = McpFrameworks.TypeScript,
            Language = SourceLanguage.TypeScript,
            ModuleRoots = new List<string> { "@modelcontextprotocol/sdk*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "McpServer", RequiresExplicitImport = true },
                new AgentConstructor { CallName = "Server", RequiresExplicitImport = true },
            },
            DescriptionArgs = new List<string> { "instructions", "description" },
            ModelArgs = new List<string>(),
            ToolArgs = new List<string>(),
            ToolMethodCalls = new List<string> { "tool", "registerTool" },
        };
    }
}
