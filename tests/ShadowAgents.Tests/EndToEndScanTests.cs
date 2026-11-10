using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShadowAgents.Tests;

/// <summary>Runs the whole scanner on a small seeded folder tree and checks the JSON lines output.</summary>
public class EndToEndScanTests
{
    private const string N8nNodes = """
        [
          { "name": "Support Agent", "type": "@n8n/n8n-nodes-langchain.agent", "parameters": {} },
          { "name": "Claude", "type": "@n8n/n8n-nodes-langchain.lmChatAnthropic", "parameters": { "model": "claude-sonnet-4-5" } }
        ]
        """;

    private const string N8nConnections = """
        { "Claude": { "ai_languageModel": [[{ "node": "Support Agent", "type": "ai_languageModel", "index": 0 }]] } }
        """;

    [Fact]
    public void Scanner_reports_agents_in_every_language_and_n8n()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        folder.WriteFile("py-bot/requirements.txt", "openai-agents==0.3.0");
        folder.WriteFile("py-bot/main.py", "from agents import Agent\nbot = Agent(name='PyBot', instructions='Hi', model='gpt-4.1')\n");
        folder.WriteFile("ts-bot/package.json", "{ \"dependencies\": { \"@mastra/core\": \"^0.10.0\" } }");
        folder.WriteFile("ts-bot/src/agent.ts", "import { Agent } from '@mastra/core/agent';\nexport const tsBot = new Agent({ name: 'TsBot', instructions: 'Hi', model: 'openai/gpt-4o' });\n");
        folder.WriteFile("cs-bot/Bot.csproj", "<PackageReference Include=\"Microsoft.Agents.AI\" Version=\"1.0.0\" />");
        folder.WriteFile("cs-bot/Program.cs", "using Microsoft.Agents.AI;\nvar agent = client.GetChatClient(\"gpt-4o\").CreateAIAgent(instructions: \"Hi\", name: \"CsBot\");\n");
        CreateN8nDatabase(Path.Combine(folder.CreateFolder(".n8n"), "database.sqlite"));

        string outputPath = Path.Combine(folder.Path, "out.jsonl");
        ScanJob job = new ScanJob { OutputPath = outputPath };
        job.Roots.Add(folder.Path);

        using ScanStopSignal stopSignal = new ScanStopSignal();
        int exitCode = new ScanRunner(job, stopSignal, TextWriter.Null).Run(null);
        SqliteConnection.ClearAllPools();

        Assert.Equal(ScanRunner.ExitSuccess, exitCode);
        List<FindingRecord> findings = ReadFindings(outputPath, out SummaryRecord summary);

        Assert.Contains(findings, finding => finding.Agent.Name == "PyBot" && finding.Framework.Key == "openai_agents" && finding.Framework.Version == "0.3.0");
        Assert.Contains(findings, finding => finding.Agent.Name == "TsBot" && finding.Framework.Key == "mastra");
        Assert.Contains(findings, finding => finding.Agent.Name == "CsBot" && finding.Framework.Key == "microsoft_agent_framework");

        FindingRecord n8nAgent = findings.Single(finding => finding.Framework.Key == "n8n" && finding.IsAgent());
        Assert.Equal("Support Agent", n8nAgent.Agent.Name);
        Assert.Equal("Ticket triage", n8nAgent.Project!.Name);
        Assert.Equal(FindingStates.Running, n8nAgent.State);
        Assert.Equal("claude-sonnet-4-5", n8nAgent.Agent.Models[0].Id);

        Assert.Equal(4, summary.AgentsFound);
        Assert.False(summary.StoppedEarly);
        Assert.Equal(findings.Count, findings.Select(finding => finding.Id).Distinct().Count());
    }

    [Fact]
    public void Two_agents_with_the_same_name_in_one_file_get_different_ids()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        folder.WriteFile(
            "bot/agent.py",
            "from agents import Agent\nfirst = Agent(name='Support', instructions='A')\nsecond = Agent(name='Support', instructions='B')\n");

        List<FindingRecord> firstScan = RunScanAndReadFindings(folder);
        List<FindingRecord> secondScan = RunScanAndReadFindings(folder);
        SqliteConnection.ClearAllPools();

        List<FindingRecord> agents = firstScan.Where(finding => finding.IsAgent()).ToList();
        Assert.Equal(2, agents.Count);
        Assert.NotEqual(agents[0].Id, agents[1].Id);
        Assert.Equal(firstScan.Count, firstScan.Select(finding => finding.Id).Distinct().Count());

        // The ids stay the same from one scan to the next.
        Assert.Equal(firstScan.Select(finding => finding.Id), secondScan.Select(finding => finding.Id));
    }

    [Fact]
    public void Second_run_reads_nothing_thanks_to_the_cache()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        folder.WriteFile("bot/agent.py", "from crewai import Agent\nr = Agent(role='R', goal='G', backstory='B')\n");
        string cachePath = Path.Combine(folder.Path, "cache.db");

        SummaryRecord first = RunScan(folder, cachePath);
        SummaryRecord second = RunScan(folder, cachePath);
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, first.FilesRead);
        Assert.Equal(0, second.FilesRead);
        Assert.Equal(1, second.FilesFromCache);
        Assert.Equal(1, second.AgentsFound);
    }

    [Fact]
    public void Locked_file_is_still_read_because_reads_share_access()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        string agentFile = folder.WriteFile("bot/agent.py", "from crewai import Agent\nr = Agent(role='R')\n");

        // Another program has the file open for writing (like an editor).
        using FileStream lockingStream = new FileStream(agentFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        SummaryRecord summary = RunScan(folder, cachePath: null);

        Assert.Equal(1, summary.AgentsFound);
    }

    private static List<FindingRecord> RunScanAndReadFindings(TemporaryFolder folder)
    {
        string outputPath = Path.Combine(folder.Path, "out.jsonl");
        ScanJob job = new ScanJob { OutputPath = outputPath };
        job.Roots.Add(Path.Combine(folder.Path, "bot"));

        using ScanStopSignal stopSignal = new ScanStopSignal();
        new ScanRunner(job, stopSignal, TextWriter.Null).Run(null);
        return ReadFindings(outputPath, out _);
    }

    private static SummaryRecord RunScan(TemporaryFolder folder, string? cachePath)
    {
        string outputPath = Path.Combine(folder.Path, "out.jsonl");
        ScanJob job = new ScanJob { OutputPath = outputPath, CachePath = cachePath };
        job.Roots.Add(Path.Combine(folder.Path, "bot"));

        using ScanStopSignal stopSignal = new ScanStopSignal();
        new ScanRunner(job, stopSignal, TextWriter.Null).Run(null);
        ReadFindings(outputPath, out SummaryRecord summary);
        return summary;
    }

    private static List<FindingRecord> ReadFindings(string outputPath, out SummaryRecord summary)
    {
        List<FindingRecord> findings = new List<FindingRecord>();
        SummaryRecord? summaryRecord = null;
        foreach (string line in File.ReadAllLines(outputPath))
        {
            if (line.Contains("\"recordType\":\"summary\""))
            {
                summaryRecord = JsonSerializer.Deserialize<SummaryRecord>(line, ContractJson.LineOptions);
            }
            else
            {
                findings.Add(JsonSerializer.Deserialize<FindingRecord>(line, ContractJson.LineOptions)!);
            }
        }

        Assert.NotNull(summaryRecord);
        summary = summaryRecord;
        return findings;
    }

    private static void CreateN8nDatabase(string databasePath)
    {
        using SqliteConnection connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE workflow_entity (id TEXT, name TEXT, active INTEGER, nodes TEXT, connections TEXT);
            INSERT INTO workflow_entity VALUES ('1', 'Ticket triage', 1, $nodes, $connections);
            """;
        command.Parameters.AddWithValue("$nodes", N8nNodes);
        command.Parameters.AddWithValue("$connections", N8nConnections);
        command.ExecuteNonQuery();
    }
}
