namespace ShadowAgents.Tests;

public class CSharpAndN8nDetectionTests
{
    private static FileDetectionResult DetectCSharp(string source)
    {
        CSharpAgentDetector detector = new CSharpAgentDetector();
        return detector.Detect(source, "Program.cs");
    }

    [Fact]
    public void SemanticKernel_agent_reads_initializer_model_and_kernel_functions()
    {
        const string source = """
            using Microsoft.SemanticKernel;
            using Microsoft.SemanticKernel.Agents;

            var builder = Kernel.CreateBuilder();
            builder.AddAzureOpenAIChatCompletion("gpt-4o-prod", "https://x.openai.azure.com", key);
            Kernel kernel = builder.Build();
            kernel.Plugins.AddFromType<MenuPlugin>();

            ChatCompletionAgent host = new ChatCompletionAgent
            {
                Name = "Host",
                Instructions = "Answer questions about the menu.",
                Kernel = kernel,
            };

            public class OrderPlugin
            {
                [KernelFunction]
                public string IssueRefund(string orderId) => "ok";
            }
            """;

        DetectedAgent agent = Assert.Single(DetectCSharp(source).Agents);

        Assert.Equal("semantic_kernel_dotnet", agent.FrameworkKey);
        Assert.Equal("Host", agent.Name);
        Assert.Equal("Answer questions about the menu.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o-prod", "azure_openai"), agent.Models[0]);
        Assert.Contains("IssueRefund", agent.Tools);
        Assert.Contains("MenuPlugin", agent.Tools);
        Assert.Contains(Capabilities.Financial, agent.Capabilities);
        Assert.Equal(Confidence.High, agent.Confidence);
    }

    [Fact]
    public void Target_typed_new_is_recognised_from_the_declared_type()
    {
        const string source = """
            using Microsoft.SemanticKernel;
            using Microsoft.SemanticKernel.Agents;
            var builder = Kernel.CreateBuilder();
            ChatCompletionAgent agent = new() { Name = "DevOps", Instructions = "Restart services." };
            """;

        DetectedAgent agent = Assert.Single(DetectCSharp(source).Agents);

        Assert.Equal("DevOps", agent.Name);
    }

    [Fact]
    public void AgentFramework_CreateAIAgent_reads_named_arguments_and_tools()
    {
        const string source = """
            using Microsoft.Agents.AI;
            using OpenAI;

            const string Instructions = "You are good at telling jokes.";
            AIAgent agent = new OpenAIClient(apiKey)
                .GetChatClient("gpt-4o-mini")
                .CreateAIAgent(instructions: Instructions, name: "Joker", tools: [AIFunctionFactory.Create(GetWeather)]);
            """;

        DetectedAgent agent = Assert.Single(DetectCSharp(source).Agents);

        Assert.Equal("microsoft_agent_framework", agent.FrameworkKey);
        Assert.Equal("Joker", agent.Name);
        Assert.Equal("You are good at telling jokes.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o-mini", "openai"), agent.Models[0]);
        Assert.Equal(new List<string> { "GetWeather" }, agent.Tools);
    }

    [Fact]
    public void AutoGen_dotnet_agent_reads_name_system_message_and_function_map()
    {
        const string source = """
            using AutoGen;
            using AutoGen.Core;

            var assistant = new AssistantAgent(
                name: "assistant",
                systemMessage: "You convert units.",
                functionMap: new Dictionary<string, Func<string, Task<string>>> { { nameof(ConvertUnits), ConvertUnitsWrapper } });
            """;

        DetectedAgent agent = Assert.Single(DetectCSharp(source).Agents);

        Assert.Equal("autogen_dotnet", agent.FrameworkKey);
        Assert.Equal("assistant", agent.Name);
        Assert.Equal("You convert units.", agent.Description);
        Assert.Equal(new List<string> { "ConvertUnits" }, agent.Tools);
    }

    [Fact]
    public void ExtensionsAI_chat_options_count_only_with_tools()
    {
        const string withTools = """
            using Microsoft.Extensions.AI;
            IChatClient client = new OllamaChatClient(new Uri("http://localhost:11434"), "llama3.1");
            var options = new ChatOptions { Tools = [AIFunctionFactory.Create(SendEmail)] };
            """;
        const string withoutTools = """
            using Microsoft.Extensions.AI;
            var options = new ChatOptions { Temperature = 0.2f };
            """;

        DetectedAgent agent = Assert.Single(DetectCSharp(withTools).Agents);
        Assert.Equal("options", agent.Name);
        Assert.Equal(new ModelReference("llama3.1", "ollama"), agent.Models[0]);
        Assert.Contains(Capabilities.EmailMessaging, agent.Capabilities);

        Assert.Empty(DetectCSharp(withoutTools).Agents);
    }

    [Fact]
    public void CSharp_file_without_framework_is_skipped()
    {
        Assert.Empty(DetectCSharp("class A { void B() { var agent = new Agent(); } }").ReferencedFrameworks);
    }

    [Fact]
    public void CSharp_broken_code_does_not_crash()
    {
        FileDetectionResult result = DetectCSharp("using Microsoft.SemanticKernel; new ChatCompletionAgent { Name = \"x\" ((((");

        Assert.Contains("semantic_kernel_dotnet", result.ReferencedFrameworks);
    }

    [Fact]
    public void N8n_agent_is_linked_to_its_model_and_tools()
    {
        const string nodes = """
            [
              { "name": "AI Agent", "type": "@n8n/n8n-nodes-langchain.agent",
                "parameters": { "options": { "systemMessage": "You triage support tickets." } } },
              { "name": "OpenAI Chat Model", "type": "@n8n/n8n-nodes-langchain.lmChatOpenAi",
                "parameters": { "model": { "__rl": true, "value": "gpt-4o-mini", "mode": "list" } } },
              { "name": "Send Gmail", "type": "n8n-nodes-base.gmailTool", "parameters": {} },
              { "name": "Fetch page", "type": "@n8n/n8n-nodes-langchain.toolHttpRequest", "parameters": {} },
              { "name": "Webhook", "type": "n8n-nodes-base.webhook", "parameters": {} }
            ]
            """;
        const string connections = """
            {
              "OpenAI Chat Model": { "ai_languageModel": [[{ "node": "AI Agent", "type": "ai_languageModel", "index": 0 }]] },
              "Send Gmail": { "ai_tool": [[{ "node": "AI Agent", "type": "ai_tool", "index": 0 }]] },
              "Fetch page": { "ai_tool": [[{ "node": "AI Agent", "type": "ai_tool", "index": 0 }]] },
              "Webhook": { "main": [[{ "node": "AI Agent", "type": "main", "index": 0 }]] }
            }
            """;

        List<DetectedAgent> agents = N8nWorkflowParser.FindAgents("Support triage", nodes, connections, "database.sqlite");

        DetectedAgent agent = Assert.Single(agents);
        Assert.Equal("AI Agent", agent.Name);
        Assert.Equal("You triage support tickets.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o-mini", "openai"), agent.Models[0]);
        Assert.Equal(new List<string> { "Send Gmail", "Fetch page" }, agent.Tools);
        Assert.Contains(RiskFlags.ExfiltrationCombo, agent.RiskFlags);
    }

    [Fact]
    public void N8n_broken_json_returns_no_agents()
    {
        Assert.Empty(N8nWorkflowParser.FindAgents("x", "[{", "{}", "db"));
    }
}
