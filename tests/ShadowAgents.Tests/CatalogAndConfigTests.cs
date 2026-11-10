namespace ShadowAgents.Tests;

public class CatalogAndConfigTests
{
    [Theory]
    [InlineData("LangChain-Core", "langchain")]
    [InlineData("langgraph_checkpoint", "langgraph")]
    [InlineData("pyautogen", "autogen")]
    [InlineData("autogen-agentchat", "autogen_agentchat")]
    [InlineData("openai-agents", "openai_agents")]
    [InlineData("strands-agents-tools", "strands")]
    [InlineData("llama-index-core", "llama_index")]
    [InlineData("haystack-ai", "haystack")]
    [InlineData("openai", null)]
    [InlineData("agents", null)]
    public void PyPI_names_map_to_frameworks(string packageName, string? expectedFramework)
    {
        Assert.Equal(expectedFramework, PackageCatalog.FindFramework(PackageEcosystem.PyPI, packageName));
    }

    [Theory]
    [InlineData("ai", "vercel_ai_sdk")]
    [InlineData("openai", null)]
    [InlineData("@langchain/langgraph", "langgraph_js")]
    [InlineData("@langchain/openai", "langchain_js")]
    [InlineData("@anthropic-ai/claude-agent-sdk", "claude_agent_sdk_ts")]
    public void Npm_names_map_to_frameworks(string packageName, string? expectedFramework)
    {
        Assert.Equal(expectedFramework, PackageCatalog.FindFramework(PackageEcosystem.Npm, packageName));
    }

    [Theory]
    [InlineData("Microsoft.SemanticKernel.Agents.Core", "semantic_kernel_dotnet")]
    [InlineData("Microsoft.Agents.AI.OpenAI", "microsoft_agent_framework")]
    [InlineData("Microsoft.Extensions.AI", "microsoft_extensions_ai")]
    [InlineData("Newtonsoft.Json", null)]
    public void NuGet_ids_map_to_frameworks(string packageId, string? expectedFramework)
    {
        Assert.Equal(expectedFramework, PackageCatalog.FindFramework(PackageEcosystem.NuGet, packageId));
    }

    [Fact]
    public void CrewAI_agents_yaml_is_read()
    {
        const string yaml = """
            researcher:
              role: >
                Senior Data Researcher
              goal: Uncover developments
              backstory: You're a seasoned researcher.
              llm: openai/gpt-4o
            not_an_agent:
              something: else
            """;

        List<CrewAiYamlAgent> agents = CrewAiConfigReader.ReadAgents(yaml);

        CrewAiYamlAgent researcher = Assert.Single(agents);
        Assert.Equal("researcher", researcher.Key);
        Assert.Equal("Senior Data Researcher\n", researcher.Role);
        Assert.Equal("openai/gpt-4o", researcher.Llm);
    }

    [Fact]
    public void Invalid_yaml_returns_no_agents()
    {
        Assert.Empty(CrewAiConfigReader.ReadAgents("researcher: [unclosed"));
    }

    [Theory]
    [InlineData("openai:gpt-4o", "gpt-4o", "openai")]
    [InlineData("openai/gpt-4o", "gpt-4o", "openai")]
    [InlineData("claude-opus-4-1", "claude-opus-4-1", "anthropic")]
    [InlineData("us.anthropic.claude-sonnet-4-20250514-v1:0", "us.anthropic.claude-sonnet-4-20250514-v1:0", "bedrock")]
    [InlineData("llama3.1", "llama3.1", null)]
    public void Model_strings_are_parsed(string modelString, string expectedId, string? expectedProvider)
    {
        ModelReference? model = ModelProviders.FromModelString(modelString, classProvider: null);

        Assert.NotNull(model);
        Assert.Equal(expectedId, model.Id);
        Assert.Equal(expectedProvider, model.Provider);
    }

    [Theory]
    [InlineData("SerperDevTool", Capabilities.WebAccess)]
    [InlineData("send_email_tool", Capabilities.EmailMessaging)]
    [InlineData("PythonREPLTool", Capabilities.CodeExecution)]
    [InlineData("delete_user", Capabilities.Destructive)]
    [InlineData("query_db", Capabilities.Database)]
    public void Tool_names_are_classified(string toolName, string expectedCapability)
    {
        Assert.Contains(expectedCapability, CapabilityClassifier.ClassifyToolName(toolName));
    }
}
