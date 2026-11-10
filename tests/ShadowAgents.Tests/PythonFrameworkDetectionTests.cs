using static ShadowAgents.Tests.PythonDetectorTestHelpers;

namespace ShadowAgents.Tests;

/// <summary>Positive cases: one realistic agent per framework, checking the full profile.</summary>
public class PythonFrameworkDetectionTests
{
    [Fact]
    public void CrewAI_agent_profile_follows_model_variable_and_joins_goal_and_backstory()
    {
        const string source = """"
            from crewai import Agent, Task, Crew
            from crewai_tools import SerperDevTool
            from langchain_openai import ChatOpenAI

            llm = ChatOpenAI(model="gpt-4o", temperature=0)
            search_tool = SerperDevTool()

            researcher = Agent(
                role="Senior Researcher",
                goal="Investigate customer complaints.",
                backstory="""An AI research
                analyst.""",
                llm=llm,
                tools=[search_tool, send_email_tool],  # comment, with (brackets]
                allow_delegation=False,
            )
            """";

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("crewai", agent.FrameworkKey);
        Assert.Equal("Senior Researcher", agent.Name);
        Assert.Equal("researcher", agent.VariableName);
        Assert.Equal("Investigate customer complaints. An AI research analyst.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o", "openai"), Assert.Single(agent.Models));
        Assert.Equal(["search_tool", "send_email_tool"], agent.Tools);
        Assert.Contains(Capabilities.WebAccess, agent.Capabilities);
        Assert.Contains(Capabilities.EmailMessaging, agent.Capabilities);
        Assert.DoesNotContain(Capabilities.Delegation, agent.Capabilities);
        Assert.Contains(RiskFlags.ExfiltrationCombo, agent.RiskFlags);
        Assert.Equal(Confidence.High, agent.Confidence);
        Assert.Equal(8, agent.Evidence.Line);
    }

    [Fact]
    public void CrewAI_scaffold_agent_is_named_after_its_method()
    {
        const string source = """
            from crewai import Agent
            from crewai.project import CrewBase, agent
            from crewai_tools import SerperDevTool

            @CrewBase
            class ResearchCrew:
                @agent
                def researcher(self) -> Agent:
                    return Agent(config=self.agents_config["researcher"], tools=[SerperDevTool()])
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("researcher", agent.Name);
        Assert.Equal(["SerperDevTool"], agent.Tools);
    }

    [Fact]
    public void PydanticAI_reads_positional_model_and_agent_bound_tools()
    {
        const string source = """
            from pydantic_ai import Agent, RunContext

            SYSTEM_PROMPT = "You help customers with refunds."

            support_agent = Agent(
                'openai:gpt-4o',
                system_prompt=SYSTEM_PROMPT,
            )

            @support_agent.tool
            async def issue_refund(ctx: RunContext, order_id: str) -> str:
                return "ok"

            @support_agent.tool_plain
            def lookup_order(order_id: str) -> str:
                return "order"
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("pydantic_ai", agent.FrameworkKey);
        Assert.Equal("support_agent", agent.Name);
        Assert.Equal("You help customers with refunds.", agent.Description);
        Assert.Equal(new ModelReference("gpt-4o", "openai"), Assert.Single(agent.Models));
        Assert.Equal(["issue_refund", "lookup_order"], agent.Tools);
        Assert.Contains(Capabilities.Financial, agent.Capabilities);
        Assert.Contains(RiskFlags.FinancialWithoutApproval, agent.RiskFlags);
    }

    [Fact]
    public void OpenAIAgents_reads_function_tools_and_handoffs()
    {
        const string source = """
            from agents import Agent, Runner, function_tool

            @function_tool
            def get_weather(city: str) -> str:
                return "sunny"

            billing_agent = Agent(name="Billing", instructions="Handle billing.")
            triage_agent = Agent(
                name="Triage",
                instructions="Route the user.",
                model="gpt-4.1",
                tools=[get_weather],
                handoffs=[billing_agent],
            )
            """;

        var result = Detect(source);

        Assert.Equal(2, result.Agents.Count);
        DetectedAgent triage = AgentNamed(result, "Triage");
        Assert.Equal("openai_agents", triage.FrameworkKey);
        Assert.Equal("Route the user.", triage.Description);
        Assert.Equal("gpt-4.1", triage.Models[0].Id);
        Assert.Equal(["get_weather"], triage.Tools);
        Assert.Contains(Capabilities.Delegation, triage.Capabilities);
        Assert.Contains("get_weather", result.DecoratedTools);
    }

    [Fact]
    public void LangChain_executor_and_inner_agent_are_reported_once()
    {
        const string source = """
            from langchain.agents import AgentExecutor, create_tool_calling_agent
            from langchain_anthropic import ChatAnthropic

            llm = ChatAnthropic(model="claude-sonnet-4-5")
            tools = [search, delete_record]
            agent = create_tool_calling_agent(llm, tools, prompt)
            agent_executor = AgentExecutor(agent=agent, tools=tools, verbose=True)
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("langchain", agent.FrameworkKey);
        Assert.Equal("agent_executor", agent.Name);
        Assert.Equal(new ModelReference("claude-sonnet-4-5", "anthropic"), Assert.Single(agent.Models));
        Assert.Equal(["search", "delete_record"], agent.Tools);
        Assert.Contains(RiskFlags.DestructiveWithoutApproval, agent.RiskFlags);
    }

    [Fact]
    public void LangChain_v1_create_agent_is_detected()
    {
        const string source = """
            from langchain.agents import create_agent

            agent = create_agent("anthropic:claude-sonnet-4-5", tools=[get_weather], system_prompt="Be helpful.")
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("langchain", agent.FrameworkKey);
        Assert.Equal(new ModelReference("claude-sonnet-4-5", "anthropic"), agent.Models[0]);
        Assert.Equal("Be helpful.", agent.Description);
    }

    [Fact]
    public void LangGraph_graph_takes_tools_and_model_from_the_file()
    {
        const string source = """
            from langgraph.graph import StateGraph, END
            from langgraph.prebuilt import ToolNode
            from langchain_openai import ChatOpenAI

            model = ChatOpenAI(model="gpt-4o-mini").bind_tools([web_search])
            tool_node = ToolNode([web_search, send_slack_message])

            workflow = StateGraph(AgentState)
            workflow.add_node("agent", call_model)
            workflow.add_node("tools", tool_node)
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("langgraph", agent.FrameworkKey);
        Assert.Equal("workflow", agent.Name);
        Assert.Contains("web_search", agent.Tools);
        Assert.Contains("send_slack_message", agent.Tools);
        Assert.Equal("gpt-4o-mini", Assert.Single(agent.Models).Id);
        Assert.Equal(Confidence.High, agent.Confidence);
    }

    [Fact]
    public void LangGraph_graph_without_nodes_has_low_confidence()
    {
        const string source = """
            from langgraph.graph import StateGraph

            graph = StateGraph(dict)
            """;

        Assert.Equal(Confidence.Low, SingleAgent(Detect(source)).Confidence);
    }

    [Fact]
    public void LangGraph_prebuilt_react_agent_wins_over_langchain_when_imported_from_langgraph()
    {
        const string source = """
            from langchain_core.tools import tool
            from langgraph.prebuilt import create_react_agent

            app = create_react_agent("openai:gpt-4o", [get_weather], prompt="You are a weather bot.")
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("langgraph", agent.FrameworkKey);
        Assert.Equal(["get_weather"], agent.Tools);
    }

    [Fact]
    public void AutoGen_04_reads_positional_name_and_model_client()
    {
        const string source = """
            from autogen_agentchat.agents import AssistantAgent, CodeExecutorAgent
            from autogen_ext.models.openai import OpenAIChatCompletionClient

            model_client = OpenAIChatCompletionClient(model="gpt-4o")
            assistant = AssistantAgent("weather_assistant", model_client=model_client, tools=[get_weather],
                                       system_message="You report the weather.")
            coder = CodeExecutorAgent("coder", code_executor=executor)
            """;

        var result = Detect(source);

        DetectedAgent assistant = AgentNamed(result, "weather_assistant");
        Assert.Equal("autogen_agentchat", assistant.FrameworkKey);
        Assert.Equal(new ModelReference("gpt-4o", "openai"), assistant.Models[0]);
        Assert.Equal("You report the weather.", assistant.Description);

        DetectedAgent coder = AgentNamed(result, "coder");
        Assert.Contains(Capabilities.CodeExecution, coder.Capabilities);
        Assert.Contains(RiskFlags.CodeExecution, coder.RiskFlags);
    }

    [Fact]
    public void AutoGen_02_reads_llm_config_dictionary_and_registered_functions()
    {
        const string source = """
            import autogen
            from autogen import AssistantAgent, UserProxyAgent, register_function

            llm_config = {"config_list": [{"model": "gpt-4", "api_key": os.environ["OPENAI_API_KEY"]}]}
            assistant = AssistantAgent(name="planner", llm_config=llm_config)
            user_proxy = UserProxyAgent("user_proxy", code_execution_config={"work_dir": "coding"})
            register_function(run_sql_query, caller=assistant, executor=user_proxy)
            """;

        var result = Detect(source);

        DetectedAgent planner = AgentNamed(result, "planner");
        Assert.Equal("autogen", planner.FrameworkKey);
        Assert.Equal("gpt-4", planner.Models[0].Id);
        Assert.Equal(["run_sql_query"], planner.Tools);
        Assert.Contains(Capabilities.Database, planner.Capabilities);

        DetectedAgent userProxy = AgentNamed(result, "user_proxy");
        Assert.Contains(Capabilities.CodeExecution, userProxy.Capabilities);
    }

    [Fact]
    public void SemanticKernel_reads_service_model_and_plugins()
    {
        const string source = """
            from semantic_kernel.agents import ChatCompletionAgent
            from semantic_kernel.connectors.ai.open_ai import AzureChatCompletion

            agent = ChatCompletionAgent(
                service=AzureChatCompletion(deployment_name="gpt-4o-prod"),
                name="Host",
                instructions="Answer questions about the menu.",
                plugins=[MenuPlugin()],
            )
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("Host", agent.Name);
        Assert.Equal(new ModelReference("gpt-4o-prod", "azure_openai"), agent.Models[0]);
        Assert.Equal(["MenuPlugin"], agent.Tools);
    }

    [Fact]
    public void LlamaIndex_function_agent_reads_function_tools()
    {
        const string source = """
            from llama_index.core.agent.workflow import FunctionAgent
            from llama_index.core.tools import FunctionTool
            from llama_index.llms.openai import OpenAI

            agent = FunctionAgent(
                name="calculator",
                tools=[FunctionTool.from_defaults(fn=multiply), add],
                llm=OpenAI(model="gpt-4o-mini"),
                system_prompt="You are a calculator.",
            )
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("llama_index", agent.FrameworkKey);
        Assert.Equal(["multiply", "add"], agent.Tools);
        Assert.Equal("gpt-4o-mini", agent.Models[0].Id);
    }

    [Fact]
    public void Strands_follows_bedrock_model_variable()
    {
        const string source = """
            from strands import Agent, tool
            from strands.models import BedrockModel

            bedrock_model = BedrockModel(model_id="us.anthropic.claude-sonnet-4-20250514-v1:0")
            agent = Agent(model=bedrock_model, system_prompt="You manage files.", tools=[file_read, file_write])
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("strands", agent.FrameworkKey);
        Assert.Equal("bedrock", agent.Models[0].Provider);
        Assert.Contains(Capabilities.Filesystem, agent.Capabilities);
    }

    [Fact]
    public void ClaudeAgentSdk_options_and_client_are_reported_once()
    {
        const string source = """
            from claude_agent_sdk import ClaudeSDKClient, ClaudeAgentOptions

            options = ClaudeAgentOptions(
                system_prompt="You are a release assistant.",
                allowed_tools=["Read", "Bash"],
                model="claude-sonnet-4-5",
                mcp_servers={"github": github_server},
            )

            async def main():
                async with ClaudeSDKClient(options=options) as client:
                    await client.query("Ship it")
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("claude_agent_sdk", agent.FrameworkKey);
        Assert.Equal("options", agent.Name);
        Assert.Equal(["Read", "Bash"], agent.Tools);
        Assert.Contains(Capabilities.Mcp, agent.Capabilities);
        Assert.Contains(Capabilities.CodeExecution, agent.Capabilities);
    }

    [Fact]
    public void GoogleAdk_reads_sub_agents_as_delegation()
    {
        const string source = """
            from google.adk.agents import Agent

            root_agent = Agent(
                name="coordinator",
                model="gemini-2.0-flash",
                description="Routes requests.",
                sub_agents=[greeter, closer],
            )
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("google_adk", agent.FrameworkKey);
        Assert.Equal(new ModelReference("gemini-2.0-flash", "google"), agent.Models[0]);
        Assert.Contains(Capabilities.Delegation, agent.Capabilities);
    }

    [Fact]
    public void Smolagents_code_agent_implies_code_execution()
    {
        const string source = """
            from smolagents import CodeAgent, InferenceClientModel, WebSearchTool

            model = InferenceClientModel(model_id="Qwen/Qwen2.5-Coder-32B-Instruct")
            agent = CodeAgent(tools=[WebSearchTool()], model=model)
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("smolagents", agent.FrameworkKey);
        Assert.Equal("Qwen/Qwen2.5-Coder-32B-Instruct", agent.Models[0].Id);
        Assert.Contains(Capabilities.CodeExecution, agent.Capabilities);
        Assert.Contains(Capabilities.WebAccess, agent.Capabilities);
    }

    [Fact]
    public void Haystack_reads_chat_generator_model()
    {
        const string source = """
            from haystack.components.agents import Agent
            from haystack.components.generators.chat import OpenAIChatGenerator

            agent = Agent(chat_generator=OpenAIChatGenerator(model="gpt-4o-mini"), tools=[search_tool])
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("haystack", agent.FrameworkKey);
        Assert.Equal("gpt-4o-mini", agent.Models[0].Id);
    }

    [Fact]
    public void Shared_Agent_name_is_resolved_from_the_explicit_import()
    {
        const string source = """
            import strands
            from pydantic_ai import Agent

            bot = Agent("anthropic:claude-haiku-4-5")
            """;

        Assert.Equal("pydantic_ai", SingleAgent(Detect(source)).FrameworkKey);
    }

    [Fact]
    public void Unresolved_model_variable_is_reported_for_cross_file_linking()
    {
        const string source = """
            from crewai import Agent
            from .llms import shared_llm

            writer = Agent(role="Writer", goal="Write.", backstory="Writes.", llm=shared_llm)
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Empty(agent.Models);
        Assert.Equal("shared_llm", agent.UnresolvedModelVariable);
    }

    [Fact]
    public void Model_assignments_are_collected_for_other_files()
    {
        const string source = """
            from langchain_openai import AzureChatOpenAI

            shared_llm = AzureChatOpenAI(azure_deployment="gpt-4o-eu")
            """;

        var result = Detect(source);

        Assert.Equal(new ModelReference("gpt-4o-eu", "azure_openai"), result.ModelAssignments["shared_llm"]);
        Assert.Empty(result.Agents);
        Assert.Contains("langchain", result.ReferencedFrameworks);
    }
}
