namespace ShadowAgents;

/// <summary>
/// Python framework rules from Section 7.2 of the design doc.
/// These APIs change often: check each rule against the current framework version when updating it.
/// </summary>
/// <remarks>
/// Order matters when a constructor name is shared (for example <c>Agent(</c>) and the file's imports
/// cannot tell the frameworks apart: the earlier rule wins.
/// </remarks>
public static class PythonFrameworkRules
{
    private static readonly ArgumentRole[] NoPositionalArgs = new ArgumentRole[0];

    public static IReadOnlyList<FrameworkRule> All { get; } = new List<FrameworkRule>
    {
        LangGraph(),
        LangChain(),
        CrewAI(),
        AutoGenLegacy(),
        AutoGenAgentChat(),
        SemanticKernel(),
        LlamaIndex(),
        PydanticAI(),
        OpenAIAgents(),
        Strands(),
        ClaudeAgentSdk(),
        GoogleAdk(),
        Smolagents(),
        Haystack(),
        McpServerPython(),
    };

    private static FrameworkRule LangChain()
    {
        return new FrameworkRule
        {
            Key = "langchain",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "langchain*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "AgentExecutor" },
                new AgentConstructor { CallName = "initialize_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model } },
                new AgentConstructor { CallName = "create_react_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools, ArgumentRole.Description } },
                new AgentConstructor { CallName = "create_openai_tools_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools, ArgumentRole.Description } },
                new AgentConstructor { CallName = "create_openai_functions_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools, ArgumentRole.Description } },
                new AgentConstructor { CallName = "create_tool_calling_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools, ArgumentRole.Description } },
                // LangChain v1
                new AgentConstructor { CallName = "create_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools } },
            },
            DescriptionArgs = new List<string> { "system_prompt", "prompt" },
            ModelArgs = new List<string> { "llm", "model" },
            ToolDecorators = new List<string> { "tool" },
            WrappedAgentArgs = new List<string> { "agent" },
        };
    }

    private static FrameworkRule LangGraph()
    {
        return new FrameworkRule
        {
            Key = "langgraph",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "langgraph*" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "StateGraph", IsGraphBuilder = true, PositionalArgs = NoPositionalArgs },
                new AgentConstructor { CallName = "MessageGraph", IsGraphBuilder = true },
                new AgentConstructor { CallName = "create_react_agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model, ArgumentRole.Tools } },
            },
            DescriptionArgs = new List<string> { "prompt", "state_modifier", "messages_modifier" },
            ModelArgs = new List<string> { "model" },
            ToolDecorators = new List<string> { "tool" },
            GraphToolCalls = new List<string> { "ToolNode", "bind_tools" },
            GraphNodeCall = "add_node",
        };
    }

    private static FrameworkRule CrewAI()
    {
        return new FrameworkRule
        {
            Key = "crewai",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "crewai*" },
            Constructors = new List<AgentConstructor> { new AgentConstructor { CallName = "Agent" } },
            NameArgs = new List<string> { "role", "name" },
            DescriptionArgs = new List<string> { "goal", "backstory" },
            CombineDescriptionArgs = true,
            ModelArgs = new List<string> { "llm" },
            ToolDecorators = new List<string> { "tool" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["allow_delegation"] = Capabilities.Delegation,
                ["allow_code_execution"] = Capabilities.CodeExecution,
            },
        };
    }

    private static FrameworkRule AutoGenLegacy()
    {
        return new FrameworkRule
        {
            Key = "autogen",
            Language = SourceLanguage.Python,
            // Exact root: "autogen_agentchat" and friends belong to the 0.4+ rule below.
            ModuleRoots = new List<string> { "autogen" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "AssistantAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name } },
                new AgentConstructor { CallName = "UserProxyAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name } },
                new AgentConstructor { CallName = "ConversableAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name } },
                new AgentConstructor { CallName = "GPTAssistantAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name } },
            },
            DescriptionArgs = new List<string> { "system_message", "description" },
            ModelArgs = new List<string> { "llm_config" },
            AgentBoundToolDecorators = new List<string> { "register_for_llm" },
            ToolRegistrationCalls = new List<ToolRegistrationCall> { new ToolRegistrationCall("register_function", 0, "caller") },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["code_execution_config"] = Capabilities.CodeExecution,
            },
        };
    }

    private static FrameworkRule AutoGenAgentChat()
    {
        return new FrameworkRule
        {
            Key = "autogen_agentchat",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "autogen_agentchat", "autogen_ext", "autogen_core" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "AssistantAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name, ArgumentRole.Model } },
                new AgentConstructor
                {
                    CallName = "CodeExecutorAgent",
                    PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name },
                    ImpliedCapabilities = new List<string> { Capabilities.CodeExecution },
                },
            },
            DescriptionArgs = new List<string> { "system_message", "description" },
            ModelArgs = new List<string> { "model_client" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["handoffs"] = Capabilities.Delegation,
                ["code_executor"] = Capabilities.CodeExecution,
                ["workbench"] = Capabilities.Mcp,
            },
        };
    }

    private static FrameworkRule SemanticKernel()
    {
        return new FrameworkRule
        {
            Key = "semantic_kernel",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "semantic_kernel" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "ChatCompletionAgent" },
                new AgentConstructor { CallName = "AzureAIAgent" },
                new AgentConstructor { CallName = "OpenAIAssistantAgent" },
                new AgentConstructor { CallName = "AzureAssistantAgent" },
            },
            DescriptionArgs = new List<string> { "instructions", "description" },
            ModelArgs = new List<string> { "service" },
            ToolArgs = new List<string> { "plugins" },
            ToolDecorators = new List<string> { "kernel_function" },
        };
    }

    private static FrameworkRule LlamaIndex()
    {
        return new FrameworkRule
        {
            Key = "llama_index",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "llama_index" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "FunctionAgent" },
                new AgentConstructor { CallName = "ReActAgent" },
                new AgentConstructor { CallName = "CodeActAgent", ImpliedCapabilities = new List<string> { Capabilities.CodeExecution } },
                new AgentConstructor { CallName = "AgentWorkflow" },
                new AgentConstructor { CallName = "OpenAIAgent.from_tools", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model } },
                new AgentConstructor { CallName = "ReActAgent.from_tools", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model } },
                new AgentConstructor { CallName = "FunctionCallingAgent.from_tools", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model } },
            },
            DescriptionArgs = new List<string> { "description", "system_prompt" },
            ModelArgs = new List<string> { "llm" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["agents"] = Capabilities.Delegation,
                ["can_handoff_to"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule PydanticAI()
    {
        return new FrameworkRule
        {
            Key = "pydantic_ai",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "pydantic_ai" },
            Constructors = new List<AgentConstructor> { new AgentConstructor { CallName = "Agent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Model } } },
            DescriptionArgs = new List<string> { "system_prompt", "instructions" },
            ModelArgs = new List<string> { "model" },
            AgentBoundToolDecorators = new List<string> { "tool", "tool_plain" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["mcp_servers"] = Capabilities.Mcp,
            },
        };
    }

    private static FrameworkRule OpenAIAgents()
    {
        return new FrameworkRule
        {
            Key = "openai_agents",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "agents" },
            // Never a bare "import agents": that is usually a local package.
            RequiredImportedNames = new List<string>
            {
                "Agent", "Runner", "function_tool", "handoff", "ModelSettings", "WebSearchTool",
                "FileSearchTool", "ComputerTool", "RunContextWrapper", "InputGuardrail", "OutputGuardrail",
                "GuardrailFunctionOutput", "input_guardrail", "output_guardrail", "trace",
            },
            Constructors = new List<AgentConstructor> { new AgentConstructor { CallName = "Agent" } },
            DescriptionArgs = new List<string> { "instructions", "handoff_description" },
            ModelArgs = new List<string> { "model" },
            ToolDecorators = new List<string> { "function_tool" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["handoffs"] = Capabilities.Delegation,
                ["mcp_servers"] = Capabilities.Mcp,
            },
        };
    }

    private static FrameworkRule Strands()
    {
        return new FrameworkRule
        {
            Key = "strands",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "strands", "strands_tools" },
            Constructors = new List<AgentConstructor> { new AgentConstructor { CallName = "Agent" } },
            DescriptionArgs = new List<string> { "system_prompt", "description" },
            ModelArgs = new List<string> { "model" },
            ToolDecorators = new List<string> { "tool" },
        };
    }

    private static FrameworkRule ClaudeAgentSdk()
    {
        return new FrameworkRule
        {
            Key = "claude_agent_sdk",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "claude_agent_sdk" },
            // ClaudeAgentOptions holds the agent's settings. ClaudeSDKClient(options=...) wraps it.
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "ClaudeAgentOptions" },
                new AgentConstructor { CallName = "ClaudeSDKClient" },
            },
            DescriptionArgs = new List<string> { "system_prompt" },
            ModelArgs = new List<string> { "model" },
            ToolArgs = new List<string> { "allowed_tools" },
            ToolDecorators = new List<string> { "tool" },
            WrappedAgentArgs = new List<string> { "options" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["mcp_servers"] = Capabilities.Mcp,
                ["agents"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule GoogleAdk()
    {
        return new FrameworkRule
        {
            Key = "google_adk",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "google.adk" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "Agent" },
                new AgentConstructor { CallName = "LlmAgent" },
                new AgentConstructor { CallName = "SequentialAgent" },
                new AgentConstructor { CallName = "ParallelAgent" },
                new AgentConstructor { CallName = "LoopAgent" },
            },
            DescriptionArgs = new List<string> { "description", "instruction" },
            ModelArgs = new List<string> { "model" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["sub_agents"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule Smolagents()
    {
        return new FrameworkRule
        {
            Key = "smolagents",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "smolagents" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor
                {
                    CallName = "CodeAgent",
                    PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model },
                    ImpliedCapabilities = new List<string> { Capabilities.CodeExecution },
                },
                new AgentConstructor { CallName = "ToolCallingAgent", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Tools, ArgumentRole.Model } },
            },
            DescriptionArgs = new List<string> { "description", "instructions" },
            ModelArgs = new List<string> { "model" },
            ToolDecorators = new List<string> { "tool" },
            CapabilityArgs = new Dictionary<string, string>
            {
                ["managed_agents"] = Capabilities.Delegation,
            },
        };
    }

    private static FrameworkRule Haystack()
    {
        return new FrameworkRule
        {
            Key = "haystack",
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "haystack", "haystack_integrations" },
            Constructors = new List<AgentConstructor> { new AgentConstructor { CallName = "Agent" } },
            DescriptionArgs = new List<string> { "system_prompt" },
            ModelArgs = new List<string> { "chat_generator" },
            ToolDecorators = new List<string> { "tool" },
        };
    }

    /// <summary>
    /// MCP servers (Section 12): <c>mcp = FastMCP("weather")</c> with tools added as <c>@mcp.tool()</c>.
    /// Works for both <c>mcp.server.fastmcp</c> and the separate <c>fastmcp</c> package. Importing only
    /// the client side (<c>from mcp import ClientSession</c>) does not count.
    /// </summary>
    private static FrameworkRule McpServerPython()
    {
        return new FrameworkRule
        {
            Key = McpFrameworks.Python,
            Language = SourceLanguage.Python,
            ModuleRoots = new List<string> { "mcp", "fastmcp" },
            RequiredImportedNames = new List<string> { "FastMCP" },
            Constructors = new List<AgentConstructor>
            {
                new AgentConstructor { CallName = "FastMCP", PositionalArgs = new List<ArgumentRole> { ArgumentRole.Name } },
            },
            DescriptionArgs = new List<string> { "instructions" },
            ModelArgs = new List<string>(),
            ToolArgs = new List<string> { "tools" },
            AgentBoundToolDecorators = new List<string> { "tool" },
        };
    }
}
