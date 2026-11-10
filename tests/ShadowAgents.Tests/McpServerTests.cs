using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShadowAgents.Tests;

/// <summary>Phase 2: MCP servers configured in AI clients, built in code, and seen running (design Section 12).</summary>
public class McpServerTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------ reading client configurations

    [Fact]
    public void Claude_desktop_config_gives_package_transport_and_env_names_without_secrets()
    {
        const string config = """
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github", "--token", "ghp_abcdefghijklmnopqrstuvwxyz0123456789", "GITHUB_TOKEN=abc"],
                  "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_secretsecretsecretsecretsecretsecret00" }
                },
                "files": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\alice\\Documents"]
                }
              }
            }
            """;

        List<ConfiguredMcpServer> servers = McpConfigReader.Read(@"C:\Users\alice\AppData\Roaming\Claude\claude_desktop_config.json", config);

        Assert.Equal(2, servers.Count);
        ConfiguredMcpServer github = servers[0];
        Assert.Equal("github", github.Name);
        Assert.Equal("stdio", github.Transport);
        Assert.Equal("@modelcontextprotocol/server-github", github.Package);
        Assert.Equal(new List<string> { "-y", "@modelcontextprotocol/server-github", "--token" }, github.Arguments);
        Assert.Equal(new List<string> { "GITHUB_PERSONAL_ACCESS_TOKEN" }, github.EnvironmentVariableNames);
        Assert.Equal(3, github.Line);
        Assert.DoesNotContain("ghp_", JsonSerializer.Serialize(servers));
    }

    [Fact]
    public void Connection_strings_keep_the_address_but_never_the_login()
    {
        List<string> arguments = new List<string>
        {
            "--connection-string", "postgresql://app:Sup3rS3cret@db.local:5432/orders",
            "Host=db.local;Username=app;Password=Sup3rS3cret",
            "--read-only",
        };

        List<string> safe = McpConfigReader.WithoutSecrets(arguments);

        Assert.Equal(new List<string> { "--connection-string", "postgresql://<redacted>@db.local:5432/orders", "--read-only" }, safe);
    }

    [Fact]
    public void VsCode_settings_with_comments_and_remote_server_url_are_read()
    {
        const string settings = """
            {
              // editor settings
              "editor.fontSize": 14,
              "mcp": {
                "servers": {
                  "docs": { "type": "sse", "url": "https://user:pw@mcp.example.com/sse?api_key=abc123" },
                },
              },
            }
            """;

        List<ConfiguredMcpServer> servers = McpConfigReader.Read(@"C:\Users\alice\AppData\Roaming\Code\User\settings.json", settings);

        ConfiguredMcpServer docs = Assert.Single(servers);
        Assert.Equal("sse", docs.Transport);
        Assert.Equal("https://mcp.example.com/sse", docs.Url);
    }

    [Fact]
    public void Claude_code_keeps_servers_per_project_too()
    {
        const string config = """
            { "mcpServers": { "fetch": { "command": "uvx", "args": ["mcp-server-fetch"] } },
              "projects": { "C:\\code\\bot": { "mcpServers": { "db": { "command": "docker", "args": ["run", "-i", "--rm", "-e", "PGPASSWORD", "mcp/postgres"] } } } } }
            """;

        List<ConfiguredMcpServer> servers = McpConfigReader.Read(@"C:\Users\alice\.claude.json", config);

        Assert.Equal(2, servers.Count);
        Assert.Equal("mcp-server-fetch", servers[0].Package);
        Assert.Equal("mcp/postgres", servers[1].Package);
    }

    [Fact]
    public void Continue_yaml_list_is_read()
    {
        const string config = """
            name: my assistant
            mcpServers:
              - name: SQLite
                command: npx
                args:
                  - -y
                  - mcp-sqlite
                env:
                  DB_PATH: C:\data\app.db
            """;

        List<ConfiguredMcpServer> servers = McpConfigReader.Read(@"C:\Users\alice\.continue\config.yaml", config);

        ConfiguredMcpServer sqlite = Assert.Single(servers);
        Assert.Equal("SQLite", sqlite.Name);
        Assert.Equal("mcp-sqlite", sqlite.Package);
        Assert.Equal(new List<string> { "DB_PATH" }, sqlite.EnvironmentVariableNames);
    }

    [Fact]
    public void Broken_config_gives_no_servers()
    {
        Assert.Empty(McpConfigReader.Read(@"C:\x\.mcp.json", "{ \"mcpServers\": { "));
        Assert.Empty(McpConfigReader.Read(@"C:\x\.continue\config.yaml", "mcpServers: [ { "));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\.cursor\mcp.json", "cursor", null)]
    [InlineData(@"C:\Users\alice\AppData\Roaming\Claude\claude_desktop_config.json", "claude_desktop", null)]
    [InlineData(@"C:\code\bot\.mcp.json", "claude_code", @"C:\code\bot")]
    [InlineData(@"C:\code\bot\.vscode\mcp.json", "vscode", @"C:\code\bot")]
    [InlineData(@"C:\Users\alice\code\bot\.cursor\mcp.json", "cursor", @"C:\Users\alice\code\bot")]
    public void Config_files_are_recognised_by_path(string path, string expectedClient, string? expectedProject)
    {
        McpConfigLocation location = McpClientConfigs.Identify(path)!;

        Assert.Equal(expectedClient, location.Client);
        Assert.Equal(expectedProject, location.ProjectFolder);
    }

    [Fact]
    public void Other_json_files_are_not_mcp_configs()
    {
        Assert.Null(McpClientConfigs.Identify(@"C:\code\bot\package.json"));
        Assert.Null(McpClientConfigs.Identify(@"C:\code\bot\settings.json"));
    }

    // ------------------------------------------------------------ servers in code

    [Fact]
    public void Python_fastmcp_server_with_decorated_tools_is_found()
    {
        const string source = """
            from mcp.server.fastmcp import FastMCP

            mcp = FastMCP("weather", instructions="Weather forecasts for US cities.")

            @mcp.tool()
            async def get_forecast(city: str) -> str:
                return "sunny"

            @mcp.tool()
            def send_email(to: str) -> str:
                return "sent"
            """;

        FileDetectionResult result = new PythonAgentDetector().Detect(source, "server.py");

        DetectedAgent server = Assert.Single(result.Agents);
        Assert.Equal(McpFrameworks.Python, server.FrameworkKey);
        Assert.Equal("weather", server.Name);
        Assert.Equal(new List<string> { "get_forecast", "send_email" }, server.Tools);
        Assert.Contains(Capabilities.EmailMessaging, server.Capabilities);
    }

    [Fact]
    public void Python_mcp_client_code_is_not_a_server()
    {
        const string source = """
            from mcp import ClientSession, StdioServerParameters
            params = StdioServerParameters(command="npx", args=["-y", "server"])
            """;

        Assert.Empty(new PythonAgentDetector().Detect(source, "client.py").Agents);
    }

    [Fact]
    public void TypeScript_mcp_server_with_tool_calls_is_found()
    {
        const string source = """
            import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
            const server = new McpServer({ name: "files", version: "1.0.0" });
            server.tool("read_file", { path: z.string() }, async ({ path }) => ({ content: [] }));
            server.registerTool("delete_file", { description: "Delete" }, async () => ({ content: [] }));
            """;

        FileDetectionResult result = new JavaScriptAgentDetector().Detect(source, "server.ts");

        DetectedAgent server = Assert.Single(result.Agents);
        Assert.Equal(McpFrameworks.TypeScript, server.FrameworkKey);
        Assert.Equal("files", server.Name);
        Assert.Equal(new List<string> { "read_file", "delete_file" }, server.Tools);
        Assert.Contains(Capabilities.Destructive, server.Capabilities);
    }

    [Fact]
    public void TypeScript_server_class_not_from_the_mcp_sdk_is_ignored()
    {
        const string source = """
            import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
            import { Server } from "./my-http-server";
            const http = new Server({ name: "web" });
            """;

        Assert.Empty(new JavaScriptAgentDetector().Detect(source, "main.ts").Agents);
    }

    [Fact]
    public void CSharp_mcp_server_with_tool_methods_is_found()
    {
        const string source = """
            using ModelContextProtocol.Server;

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

            [McpServerToolType]
            public static class EchoTool
            {
                [McpServerTool, Description("Echoes the message back.")]
                public static string Echo(string message) => message;
            }
            """;

        FileDetectionResult result = new CSharpAgentDetector().Detect(source, "Program.cs");

        DetectedAgent server = Assert.Single(result.Agents);
        Assert.Equal(McpFrameworks.CSharp, server.FrameworkKey);
        Assert.Equal(new List<string> { "Echo" }, server.Tools);
        Assert.Equal(new List<string> { "Echo" }, result.McpServerTools);
    }

    // ------------------------------------------------------------ end to end

    [Fact]
    public void Scan_reports_configured_and_implemented_mcp_servers()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        folder.WriteFile("weather/pyproject.toml", "[project]\nname = \"weather\"\ndependencies = [\"mcp>=1.2\"]\n");
        folder.WriteFile("weather/server.py", "from mcp.server.fastmcp import FastMCP\nmcp = FastMCP(\"weather\")\n\n@mcp.tool()\ndef get_forecast(city: str) -> str:\n    return city\n");
        folder.WriteFile("weather/.mcp.json", "{ \"mcpServers\": { \"weather\": { \"command\": \"uv\", \"args\": [\"run\", \"server.py\"] } } }");

        string outputPath = Path.Combine(folder.Path, "out.jsonl");
        ScanJob job = new ScanJob { OutputPath = outputPath };
        job.Roots.Add(Path.Combine(folder.Path, "weather"));
        using ScanStopSignal stopSignal = new ScanStopSignal();
        new ScanRunner(job, stopSignal, TextWriter.Null).Run(null);
        SqliteConnection.ClearAllPools();

        List<FindingRecord> findings = new List<FindingRecord>();
        SummaryRecord? summary = null;
        foreach (string line in File.ReadAllLines(outputPath))
        {
            if (line.Contains("\"recordType\":\"summary\""))
            {
                summary = JsonSerializer.Deserialize<SummaryRecord>(line, ContractJson.LineOptions);
            }
            else
            {
                findings.Add(JsonSerializer.Deserialize<FindingRecord>(line, ContractJson.LineOptions)!);
            }
        }

        FindingRecord configured = findings.Single(finding => finding.Framework.Key == McpFrameworks.ClientConfig);
        Assert.Equal(FindingKinds.McpServer, configured.Kind);
        Assert.Equal(FindingStates.Referenced, configured.State);
        Assert.Equal("claude_code", configured.McpServer!.Client);
        Assert.Equal(".mcp.json", configured.Evidence[0].File);
        Assert.Equal(Path.Combine(folder.Path, "weather"), configured.Project!.Root);

        FindingRecord implemented = findings.Single(finding => finding.Framework.Key == McpFrameworks.Python && finding.State == FindingStates.Implemented);
        Assert.Equal(FindingKinds.McpServer, implemented.Kind);
        Assert.Equal(new List<string> { "get_forecast" }, implemented.Agent.Tools);

        Assert.Equal(2, summary!.McpServersFound);
        Assert.Equal(0, summary.AgentsFound);
    }

    // ------------------------------------------------------------ running

    [Fact]
    public void Mcp_server_process_started_by_a_client_marks_the_configured_server_running()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { ConfiguredServer("m1", "@modelcontextprotocol/server-filesystem", null) }, new List<string>());
        RuntimeCorrelator correlator = new RuntimeCorrelator();
        ProcessEvent started = new ProcessEvent
        {
            IsStart = true,
            ProcessId = 99,
            ProcessName = "node.exe",
            CommandLine = @"node C:\Users\alice\AppData\Local\npm-cache\_npx\1a2b\node_modules\@modelcontextprotocol\server-filesystem\dist\index.js C:\Users\alice\Documents",
        };

        correlator.OnProcessEvent(started, inventory, Now);

        FindingRecord running = inventory.Find("m1")!;
        Assert.Equal(FindingStates.Running, running.State);
        Assert.Equal(new List<string> { "node.exe (pid 99)" }, running.Runtime.Processes);

        correlator.OnProcessEvent(new ProcessEvent { IsStart = false, ProcessId = 99, ProcessName = "node.exe" }, inventory, Now.AddMinutes(1));

        FindingRecord stopped = inventory.Find("m1")!;
        Assert.Equal(FindingStates.Referenced, stopped.State);
        Assert.False(stopped.Runtime.Running);
    }

    [Fact]
    public void Proxy_request_to_a_configured_remote_server_is_recorded_once()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { ConfiguredServer("m2", null, "https://mcp.example.com/sse") }, new List<string>());

        Assert.Single(inventory.MarkRemoteMcpServerUsed(new Uri("https://mcp.example.com/sse/messages?session=1"), Now));
        Assert.Empty(inventory.MarkRemoteMcpServerUsed(new Uri("https://mcp.example.com/sse"), Now.AddMinutes(1)));
        Assert.Empty(inventory.MarkRemoteMcpServerUsed(new Uri("https://other.example.com/sse"), Now));
        Assert.Equal(Now.AddMinutes(1), inventory.Find("m2")!.Runtime.LastSeenRunning);
    }

    [Fact]
    public void Removed_server_disappears_when_its_config_file_is_rescanned()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { ConfiguredServer("m3", "mcp-server-fetch", null) }, new List<string>());

        List<InventoryChange> changes = inventory.ApplyScanResults(new List<FindingRecord>(), new List<string> { @"C:\Users\alice\.cursor\mcp.json" });

        Assert.Equal(InventoryChange.Removed, Assert.Single(changes).Change);
        Assert.Null(inventory.Find("m3"));
    }

    private static FindingRecord ConfiguredServer(string id, string? package, string? url)
    {
        string transport = "stdio";
        if (url != null)
        {
            transport = "sse";
        }

        return new FindingRecord
        {
            Id = id,
            Kind = FindingKinds.McpServer,
            State = FindingStates.Referenced,
            Framework = new FrameworkRecord { Key = McpFrameworks.ClientConfig },
            McpServer = new McpServerRecord
            {
                Client = "cursor",
                ConfigFile = @"C:\Users\alice\.cursor\mcp.json",
                Transport = transport,
                Command = "npx",
                Package = package,
                Url = url,
            },
        };
    }
}
