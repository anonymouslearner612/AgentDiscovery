using System.Diagnostics;
using System.Text;
using static ShadowAgents.Tests.PythonDetectorTestHelpers;

namespace ShadowAgents.Tests;

/// <summary>Negative corpus, hostile input, notebooks and privacy (Section 13).</summary>
public class PythonNegativeAndHostileTests
{
    [Fact]
    public void File_without_any_framework_name_is_skipped_by_the_pre_check()
    {
        var result = Detect("import os\nprint('hello')\n");

        Assert.Empty(result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Framework_names_inside_strings_and_comments_do_not_count()
    {
        const string source = """
            # from crewai import Agent
            DOC = '''
            from crewai import Agent
            researcher = Agent(role="x")
            '''
            example = "Agent(role='y')"
            """;

        var result = Detect(source);

        Assert.Empty(result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Local_module_named_agents_is_not_the_OpenAI_Agents_SDK()
    {
        const string source = """
            import agents
            from agents import researcher, writer

            team = Agent(name="not an sdk agent")
            """;

        var result = Detect(source);

        Assert.Empty(result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Plain_LLM_call_is_referenced_but_not_an_agent()
    {
        const string source = """
            from langchain_openai import ChatOpenAI

            llm = ChatOpenAI(model="gpt-4o")
            print(llm.invoke("hi"))
            """;

        var result = Detect(source);

        Assert.Equal(["langchain"], result.ReferencedFrameworks);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Agent_class_from_an_unrelated_module_is_ignored()
    {
        const string source = """
            import crewai
            from myapp.models import Agent

            record = Agent(name="db row")
            """;

        Assert.Empty(Detect(source).Agents);
    }

    [Fact]
    public void Class_and_function_definitions_named_like_constructors_are_not_agents()
    {
        const string source = """
            from crewai import Agent

            class Agent(object):
                pass

            def Agent(role):
                return role
            """;

        Assert.Empty(Detect(source).Agents);
    }

    [Fact]
    public void Method_call_on_another_object_is_not_an_agent()
    {
        const string source = """
            from langchain.agents import create_agent

            remote = project_client.agents.create_agent(model="gpt-4o", name="remote")
            """;

        Assert.Empty(Detect(source).Agents);
    }

    [Fact]
    public void Hundred_thousand_unclosed_constructors_finish_quickly_and_report_the_cap()
    {
        var hostileSource = new StringBuilder("from crewai import Agent\n");
        for (int index = 0; index < 100_000; index++)
        {
            hostileSource.Append("Agent(\n");
        }

        var stopwatch = Stopwatch.StartNew();
        var result = Detect(hostileSource.ToString());
        stopwatch.Stop();

        Assert.True(result.DetectionCapHit);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Deeply_nested_brackets_do_not_crash()
    {
        string source = "from crewai import Agent\nresearcher = Agent(role=\"x\", tools=" + new string('[', 5000) + new string(']', 5000) + ")\n";

        var result = Detect(source);

        Assert.NotNull(result);
    }

    [Fact]
    public void Huge_single_line_file_is_handled()
    {
        string source = "from crewai import Agent\nx = '" + new string('a', 900_000) + "'\nresearcher = Agent(role=\"Analyst\")\n";

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Equal("Analyst", agent.Name);
    }

    [Fact]
    public void Secrets_are_dropped_from_descriptions_and_redacted_from_snippets()
    {
        const string source = """
            from openai import OpenAI
            from agents import Agent

            bot = Agent(name="Leaky", instructions="Use key sk-proj-abcdefghijklmnopqrstuvwxyz123456 to call the API"); key = "sk-ant-abcdefghijklmnopqrstuvwxyz0123"
            """;

        DetectedAgent agent = SingleAgent(Detect(source));

        Assert.Null(agent.Description);
        Assert.DoesNotContain("sk-proj-", agent.Evidence.Snippet);
        Assert.DoesNotContain("sk-ant-", agent.Evidence.Snippet);
        Assert.Contains("<redacted>", agent.Evidence.Snippet);
    }

    [Fact]
    public void Non_literal_description_is_reported_as_dynamic()
    {
        const string source = """
            from agents import Agent
            bot = Agent(name="Dyn", instructions=build_prompt(user))
            """;

        Assert.Equal("<dynamic>", SingleAgent(Detect(source)).Description);
    }

    [Fact]
    public void Notebook_evidence_points_at_cell_and_line()
    {
        const string notebook = """
            {
              "cells": [
                { "cell_type": "markdown", "source": ["# Demo\n", "from crewai import Agent"] },
                { "cell_type": "code", "source": ["!pip install crewai\n", "from crewai import Agent\n"] },
                { "cell_type": "code", "source": "x = 1\nanalyst = Agent(role=\"Analyst\", goal=\"Analyze.\")" }
              ],
              "nbformat": 4
            }
            """;

        var detector = new PythonAgentDetector();
        DetectedAgent agent = SingleAgent(detector.DetectNotebook(notebook, "demo.ipynb"));

        Assert.Equal("Analyst", agent.Name);
        Assert.Equal(2, agent.Evidence.Cell);
        Assert.Equal(2, agent.Evidence.Line);
    }

    [Fact]
    public void Invalid_notebook_json_returns_empty_result()
    {
        var detector = new PythonAgentDetector();

        var result = detector.DetectNotebook("{ \"cells\": [ crewai", "broken.ipynb");

        Assert.Empty(result.Agents);
    }
}
