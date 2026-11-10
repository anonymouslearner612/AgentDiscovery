using System.Diagnostics;
using System.Text;

namespace ShadowAgents.Tests;

public class JavaScriptDetectionTests
{
    private static FileDetectionResult Detect(string source, string fileName = "agent.ts")
    {
        JavaScriptAgentDetector detector = new JavaScriptAgentDetector();
        return detector.Detect(source, fileName);
    }

    [Fact]
    public void OpenAI_agents_js_agent_is_profiled()
    {
        const string source = """
            import { Agent, run, tool } from '@openai/agents';

            const getWeather = tool({ name: 'get_weather', description: 'Weather', execute: async () => 'sunny' });

            export const triageAgent = new Agent({
              name: 'Triage',
              instructions: 'Route the user to the right agent.',
              model: 'gpt-4.1',
              tools: [getWeather, searchWeb],
              handoffs: [billingAgent],
            });
            """;

        DetectedAgent agent = Assert.Single(Detect(source).Agents);

        Assert.Equal("openai_agents_js", agent.FrameworkKey);
        Assert.Equal("Triage", agent.Name);
        Assert.Equal("triageAgent", agent.VariableName);
        Assert.Equal("Route the user to the right agent.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4.1", "openai"), agent.Models[0]);
        Assert.Equal(new List<string> { "getWeather", "searchWeb" }, agent.Tools);
        Assert.Contains(Capabilities.Delegation, agent.Capabilities);
        Assert.Equal(5, agent.Evidence.Line);
    }

    [Fact]
    public void Mastra_agent_reads_model_call_and_tool_object_keys()
    {
        const string source = """
            import { Agent } from "@mastra/core/agent";
            import { openai } from "@ai-sdk/openai";

            const INSTRUCTIONS = `You help with weather.`;

            export const weatherAgent = new Agent({
              name: "Weather Agent",
              instructions: INSTRUCTIONS,
              model: openai("gpt-4o-mini"),
              tools: { weatherTool, sendEmail: emailTool },
            });
            """;

        DetectedAgent agent = Assert.Single(Detect(source).Agents);

        Assert.Equal("mastra", agent.FrameworkKey);
        Assert.Equal("You help with weather.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o-mini", "openai"), agent.Models[0]);
        Assert.Equal(new List<string> { "weatherTool", "sendEmail" }, agent.Tools);
    }

    [Fact]
    public void Vercel_generateText_is_an_agent_only_with_tools()
    {
        const string source = """
            import { generateText, tool } from 'ai';
            import { anthropic } from '@ai-sdk/anthropic';

            const plain = await generateText({ model: anthropic('claude-sonnet-4-5'), prompt: 'hi' });

            const withTools = await generateText({
              model: anthropic('claude-sonnet-4-5'),
              system: 'You run SQL.',
              tools: { runQuery: tool({ description: 'Run SQL', execute: async () => 1 }) },
            });
            """;

        FileDetectionResult result = Detect(source);

        DetectedAgent agent = Assert.Single(result.Agents);
        Assert.Equal("vercel_ai_sdk", agent.FrameworkKey);
        Assert.Equal("withTools", agent.Name);
        Assert.Equal("anthropic", agent.Models[0].Provider);
        Assert.Equal(new List<string> { "runQuery" }, agent.Tools);
    }

    [Fact]
    public void LangGraph_js_react_agent_follows_model_variable()
    {
        const string source = """
            import { ChatOpenAI } from "@langchain/openai";
            import { createReactAgent } from "@langchain/langgraph/prebuilt";

            const llm = new ChatOpenAI({ model: "gpt-4o", temperature: 0 });
            const app = createReactAgent({ llm, tools: [tavily, deleteRecord], prompt: "You are careful." });
            """;

        DetectedAgent agent = Assert.Single(Detect(source).Agents);

        Assert.Equal("langgraph_js", agent.FrameworkKey);
        Assert.Equal(new ModelReference("gpt-4o", "openai"), agent.Models[0]);
        Assert.Contains("deleteRecord", agent.Tools);
        Assert.Contains(RiskFlags.DestructiveWithoutApproval, agent.RiskFlags);
    }

    [Fact]
    public void Claude_agent_sdk_ts_reads_nested_options()
    {
        const string source = """
            import { query } from "@anthropic-ai/claude-agent-sdk";

            for await (const message of query({
              prompt: "Fix the bug",
              options: { systemPrompt: "You are a coding agent.", allowedTools: ["Read", "Edit", "Bash"], model: "claude-sonnet-4-5" },
            })) {
              console.log(message);
            }
            """;

        DetectedAgent agent = Assert.Single(Detect(source).Agents);

        Assert.Equal("claude_agent_sdk_ts", agent.FrameworkKey);
        Assert.Equal("You are a coding agent.", agent.Description);
        Assert.Equal(new List<string> { "Read", "Edit", "Bash" }, agent.Tools);
        Assert.Contains(Capabilities.CodeExecution, agent.Capabilities);
    }

    [Fact]
    public void Commonjs_require_is_an_import()
    {
        const string source = """
            const { Agent } = require("@openai/agents");
            const bot = new Agent({ name: "Bot", instructions: "Hi" });
            """;

        Assert.Equal("Bot", Assert.Single(Detect(source, "bot.js").Agents).Name);
    }

    [Fact]
    public void Openai_package_is_not_the_ai_package()
    {
        const string source = """
            import OpenAI from "openai";
            const client = new OpenAI();
            const result = generateText({ tools: { a } });
            """;

        FileDetectionResult result = Detect(source);

        Assert.Empty(result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Names_in_strings_comments_and_regexes_do_not_count()
    {
        const string source = """
            // import { Agent } from "@openai/agents";
            /* new Agent({ name: "x" }) */
            const docs = `import { Agent } from "@mastra/core"`;
            const pattern = /new Agent\(/g;
            """;

        FileDetectionResult result = Detect(source);

        Assert.Empty(result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Local_Agent_class_next_to_framework_import_is_ignored()
    {
        const string source = """
            import { tool } from "@openai/agents";
            import { Agent } from "./models/agent";
            const row = new Agent({ name: "db row" });
            """;

        Assert.Empty(Detect(source).Agents);
    }

    [Fact]
    public void Generic_query_call_without_explicit_import_is_ignored()
    {
        const string source = """
            import * as sdk from "@anthropic-ai/claude-agent-sdk";
            const rows = db.query({ sql: "select 1" });
            """;

        Assert.Empty(Detect(source).Agents);
    }

    [Fact]
    public void Hostile_unclosed_constructors_finish_quickly()
    {
        StringBuilder hostile = new StringBuilder("import { Agent } from '@openai/agents';\n");
        for (int index = 0; index < 100_000; index++)
        {
            hostile.Append("new Agent({\n");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        FileDetectionResult result = Detect(hostile.ToString());
        stopwatch.Stop();

        Assert.True(result.DetectionCapHit);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Took " + stopwatch.Elapsed);
    }
}
