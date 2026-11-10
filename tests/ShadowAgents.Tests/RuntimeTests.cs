namespace ShadowAgents.Tests;

public class RuntimeTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Command_line_is_split_with_quotes()
    {
        List<string> arguments = WindowsCommandLine.Split("\"C:\\Program Files\\Python\\python.exe\" -u \"C:\\My Code\\agent.py\" --name \"a \\\"b\\\"\"");

        Assert.Equal(new List<string> { @"C:\Program Files\Python\python.exe", "-u", @"C:\My Code\agent.py", "--name", "a \"b\"" }, arguments);
    }

    [Theory]
    [InlineData(@"python.exe C:\code\bot\main.py --verbose", @"C:\code\bot\main.py")]
    [InlineData(@"""C:\Python312\python.exe"" -u ""C:\My Code\agent.py""", @"C:\My Code\agent.py")]
    [InlineData(@"uv run --directory C:\code\bot python main.py", @"C:\code\bot")]
    [InlineData(@"node --env-file=.env dist\server.js", @"dist\server.js")]
    [InlineData(@"dotnet run --project C:\code\Bot\Bot.csproj", @"C:\code\Bot\Bot.csproj")]
    [InlineData(@"jupyter-lab --notebook-dir=C:\Users\a\notebooks", @"C:\Users\a\notebooks")]
    public void Script_path_is_found_on_the_command_line(string commandLine, string expectedPath)
    {
        Assert.Contains(expectedPath, ScriptPathResolver.FindScriptPaths(commandLine));
    }

    [Theory]
    [InlineData("python.exe", true)]
    [InlineData("node.exe", true)]
    [InlineData("chrome.exe", false)]
    public void Only_agent_runners_are_watched(string processName, bool expected)
    {
        Assert.Equal(expected, ScriptPathResolver.IsAgentRunner(processName));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", "{\"model\":\"gpt-4o\",\"messages\":[]}", "openai", "gpt-4o")]
    [InlineData("https://api.anthropic.com/v1/messages", "{\"model\":\"claude-sonnet-4-5\"}", "anthropic", "claude-sonnet-4-5")]
    [InlineData("https://contoso.openai.azure.com/openai/deployments/gpt4o-prod/chat/completions?api-version=1", null, "azure_openai", "gpt4o-prod")]
    [InlineData("https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-3-haiku-20240307-v1%3A0/invoke", null, "bedrock", "anthropic.claude-3-haiku-20240307-v1:0")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent", null, "google", "gemini-2.0-flash")]
    [InlineData("http://localhost:11434/api/chat", "{\"model\":\"llama3.1\"}", "ollama", "llama3.1")]
    public void Observed_model_is_read_from_url_or_body(string url, string? body, string expectedProvider, string expectedModel)
    {
        ModelRecord? model = LlmEndpoints.ExtractModel(new Uri(url), body);

        Assert.NotNull(model);
        Assert.Equal(expectedProvider, model.Provider);
        Assert.Equal(expectedModel, model.Id);
        Assert.Equal("observed", model.Source);
    }

    [Fact]
    public void Non_llm_hosts_are_ignored()
    {
        Assert.Null(LlmEndpoints.ProviderFor(new Uri("https://github.com/api")));
        Assert.Null(LlmEndpoints.ProviderFor(new Uri("http://localhost:8080/api/chat")));
    }

    [Fact]
    public void Process_start_and_stop_mark_agents_running_and_back()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { AgentRecord("a1", @"C:\code\bot", "main.py") }, new List<string>());
        RuntimeCorrelator correlator = new RuntimeCorrelator();
        ProcessEvent started = new ProcessEvent { IsStart = true, ProcessId = 42, ProcessName = "python.exe", CommandLine = @"python.exe C:\code\bot\main.py" };

        List<InventoryChange> startChanges = correlator.OnProcessEvent(started, inventory, Now);

        FindingRecord running = inventory.Find("a1")!;
        Assert.Single(startChanges);
        Assert.Equal(FindingStates.Running, running.State);
        Assert.True(running.Runtime.Running);
        Assert.Equal(new List<string> { "python.exe (pid 42)" }, running.Runtime.Processes);
        Assert.Equal(@"C:\code\bot", correlator.FindProjectRootForProcess(42));

        ProcessEvent stopped = new ProcessEvent { IsStart = false, ProcessId = 42, ProcessName = "python.exe" };
        correlator.OnProcessEvent(stopped, inventory, Now.AddMinutes(5));

        FindingRecord ended = inventory.Find("a1")!;
        Assert.Equal(FindingStates.Implemented, ended.State);
        Assert.False(ended.Runtime.Running);
        Assert.Equal(Now.AddMinutes(5), ended.Runtime.LastSeenRunning); // "ran recently"
    }

    [Fact]
    public void Relative_script_path_is_matched_against_known_agent_files()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { AgentRecord("a1", @"C:\code\bot", "src/agent.py") }, new List<string>());
        RuntimeCorrelator correlator = new RuntimeCorrelator();
        ProcessEvent started = new ProcessEvent { IsStart = true, ProcessId = 7, ProcessName = "python.exe", CommandLine = @"python src\agent.py" };

        correlator.OnProcessEvent(started, inventory, Now);

        Assert.True(inventory.Find("a1")!.Runtime.Running);
    }

    [Fact]
    public void Observed_models_are_added_once()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { AgentRecord("a1", @"C:\code\bot", "main.py") }, new List<string>());
        ModelRecord observed = new ModelRecord { Id = "gpt-4o", Provider = "openai", Source = "observed" };

        Assert.Single(inventory.AddObservedModel(@"C:\code\bot", observed, Now));
        Assert.Empty(inventory.AddObservedModel(@"C:\code\bot", observed, Now));
    }

    [Fact]
    public void Proxy_event_reader_reads_only_complete_new_lines()
    {
        string path = Path.Combine(Path.GetTempPath(), "proxy-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Starts with a byte-order mark, as Windows PowerShell 5 writes it.
            File.WriteAllText(path, "{\"processId\":1,\"url\":\"https://api.openai.com/v1/x\"}\n{\"processId\":2,\"url\":", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ProxyEventReader reader = new ProxyEventReader(path);
            long offset = 0;

            List<ProxyRequestEvent> first = reader.ReadNewEvents(ref offset);
            File.AppendAllText(path, "\"https://api.anthropic.com/v1/messages\"}\n");
            List<ProxyRequestEvent> second = reader.ReadNewEvents(ref offset);
            List<ProxyRequestEvent> third = reader.ReadNewEvents(ref offset);

            Assert.Equal(1, Assert.Single(first).ProcessId);
            Assert.Equal(2, Assert.Single(second).ProcessId);
            Assert.Empty(third);
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static FindingRecord AgentRecord(string id, string projectRoot, string evidenceFile)
    {
        FindingRecord record = new FindingRecord
        {
            Id = id,
            State = FindingStates.Implemented,
            Confidence = "high",
            Framework = new FrameworkRecord { Key = "crewai", Display = "CrewAI", Language = "python" },
            Project = new ProjectRecord { Root = projectRoot, Name = Path.GetFileName(projectRoot) },
            FirstSeen = Now,
            LastSeen = Now,
        };
        record.Agent.Name = "agent-" + id;
        record.Evidence.Add(new EvidenceRecord { File = evidenceFile, Line = 1, Snippet = "x = Agent(" });
        return record;
    }
}
