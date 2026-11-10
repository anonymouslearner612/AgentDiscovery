using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShadowAgents;

/// <summary>
/// Finds AI agents in one n8n workflow (Section 7.2, "n8n"). The scanner reads the workflow rows from
/// n8n's SQLite database and passes the "nodes" and "connections" JSON columns here.
/// </summary>
/// <remarks>
/// In n8n, models and tools are separate nodes connected TO the agent node:
/// connections["OpenAI Chat Model"]["ai_languageModel"] = [[{ "node": "AI Agent" }]] means
/// "the OpenAI Chat Model node is the language model of the AI Agent node".
/// </remarks>
public static class N8nWorkflowParser
{
    private static readonly HashSet<string> AgentNodeTypes = new HashSet<string>
    {
        "@n8n/n8n-nodes-langchain.agent",
        "@n8n/n8n-nodes-langchain.agentTool",
        "@n8n/n8n-nodes-langchain.openAiAssistant",
    };

    private const string ModelConnection = "ai_languageModel";
    private const string ToolConnection = "ai_tool";

    /// <summary>A workflow node: its display name, type and parameters.</summary>
    private sealed class WorkflowNode
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public JsonElement Parameters { get; set; }
    }

    public static List<DetectedAgent> FindAgents(
        string workflowName,
        string nodesJson,
        string connectionsJson,
        string databasePath)
    {
        List<DetectedAgent> agents = new List<DetectedAgent>();
        try
        {
            using JsonDocument nodesDocument = JsonDocument.Parse(nodesJson);
            if (string.IsNullOrWhiteSpace(connectionsJson))
            {
                connectionsJson = "{}";
            }

            using JsonDocument connectionsDocument = JsonDocument.Parse(connectionsJson);

            Dictionary<string, WorkflowNode> nodesByName = ReadNodes(nodesDocument.RootElement);
            foreach (WorkflowNode node in nodesByName.Values)
            {
                if (!AgentNodeTypes.Contains(node.Type))
                {
                    continue;
                }

                agents.Add(BuildAgent(workflowName, node, nodesByName, connectionsDocument.RootElement, databasePath));
            }
        }
        catch (JsonException)
        {
            // A broken workflow row is skipped; the other workflows are still read.
        }

        return agents;
    }

    private static DetectedAgent BuildAgent(
        string workflowName,
        WorkflowNode agentNode,
        Dictionary<string, WorkflowNode> nodesByName,
        JsonElement connections,
        string databasePath)
    {
        string snippet = TextCleanup.MakeSnippet("workflow \"" + workflowName + "\" node \"" + agentNode.Name + "\"");
        DetectedAgent agent = new DetectedAgent
        {
            FrameworkKey = "n8n",
            Name = agentNode.Name,
            Evidence = new Evidence(databasePath, 0, snippet),
            Description = ReadSystemMessage(agentNode.Parameters, Settings.MaxDescriptionChars),
        };

        foreach (string modelNodeName in FindNodesConnectedTo(connections, agentNode.Name, ModelConnection))
        {
            if (nodesByName.TryGetValue(modelNodeName, out WorkflowNode modelNode))
            {
                ModelReference model = ReadModel(modelNode);
                if (model != null)
                {
                    ListHelpers.AddDistinct(agent.Models, model);
                }
            }
        }

        foreach (string toolNodeName in FindNodesConnectedTo(connections, agentNode.Name, ToolConnection))
        {
            ListHelpers.AddDistinct(agent.Tools, toolNodeName);
            if (nodesByName.TryGetValue(toolNodeName, out WorkflowNode toolNode))
            {
                // The node type says more than its display name: "...langchain.toolHttpRequest", "...gmailTool".
                ListHelpers.AddDistinct(agent.Capabilities, CapabilitiesOfToolType(toolNode.Type));
            }
        }

        CapabilityClassifier.Apply(agent);
        agent.Confidence = AgentConfidence.Decide(agent, isGraphWithoutNodes: false);
        return agent;
    }

    private static Dictionary<string, WorkflowNode> ReadNodes(JsonElement nodesArray)
    {
        Dictionary<string, WorkflowNode> nodesByName = new Dictionary<string, WorkflowNode>();
        if (nodesArray.ValueKind != JsonValueKind.Array)
        {
            return nodesByName;
        }

        foreach (JsonElement nodeElement in nodesArray.EnumerateArray())
        {
            WorkflowNode node = new WorkflowNode
            {
                Name = ReadStringPropertyOrEmpty(nodeElement, "name"),
                Type = ReadStringPropertyOrEmpty(nodeElement, "type"),
            };

            if (nodeElement.ValueKind == JsonValueKind.Object && nodeElement.TryGetProperty("parameters", out JsonElement parameters))
            {
                // Clone so the element stays valid after the JsonDocument is disposed.
                node.Parameters = parameters.Clone();
            }

            if (node.Name.Length > 0)
            {
                nodesByName[node.Name] = node;
            }
        }

        return nodesByName;
    }

    /// <summary>Names of nodes whose <paramref name="connectionType"/> output goes into <paramref name="targetNodeName"/>.</summary>
    private static List<string> FindNodesConnectedTo(JsonElement connections, string targetNodeName, string connectionType)
    {
        List<string> sourceNodeNames = new List<string>();
        if (connections.ValueKind != JsonValueKind.Object)
        {
            return sourceNodeNames;
        }

        foreach (JsonProperty sourceNode in connections.EnumerateObject())
        {
            if (sourceNode.Value.ValueKind != JsonValueKind.Object
                || !sourceNode.Value.TryGetProperty(connectionType, out JsonElement outputs)
                || outputs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // outputs is a list of output slots; each slot is a list of { "node": ..., "type": ... } targets.
            foreach (JsonElement outputSlot in outputs.EnumerateArray())
            {
                if (outputSlot.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement target in outputSlot.EnumerateArray())
                {
                    if (ReadStringProperty(target, "node") == targetNodeName)
                    {
                        ListHelpers.AddDistinct(sourceNodeNames, sourceNode.Name);
                    }
                }
            }
        }

        return sourceNodeNames;
    }

    /// <summary>The model parameter is a string, or an object like { "value": "gpt-4o-mini", "mode": "list" }.</summary>
    private static ModelReference ReadModel(WorkflowNode modelNode)
    {
        string typeSuffix = TypeSuffix(modelNode.Type);
        string provider = ModelProviders.FromClassName(typeSuffix);
        foreach (string parameterName in new[] { "model", "modelName", "modelId" })
        {
            if (modelNode.Parameters.ValueKind != JsonValueKind.Object
                || !modelNode.Parameters.TryGetProperty(parameterName, out JsonElement modelValue))
            {
                continue;
            }

            string modelId = null;
            if (modelValue.ValueKind == JsonValueKind.String)
            {
                modelId = modelValue.GetString();
            }
            else if (modelValue.ValueKind == JsonValueKind.Object)
            {
                modelId = ReadStringProperty(modelValue, "value");
            }

            if (!string.IsNullOrWhiteSpace(modelId))
            {
                return ModelProviders.FromModelString(modelId.TrimStart('='), provider);
            }
        }

        // No explicit model: n8n uses the node's default. The provider is still worth reporting.
        if (provider != null)
        {
            return new ModelReference("<default>", provider);
        }

        return null;
    }

    private static string ReadSystemMessage(JsonElement parameters, int maxChars)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string systemMessage = null;
        if (parameters.TryGetProperty("options", out JsonElement agentOptions))
        {
            systemMessage = ReadStringProperty(agentOptions, "systemMessage");
        }

        if (systemMessage == null)
        {
            systemMessage = ReadStringProperty(parameters, "systemMessage");
        }

        if (string.IsNullOrWhiteSpace(systemMessage))
        {
            return null;
        }

        // n8n expressions ("={{ $json.prompt }}") are computed at run time.
        if (systemMessage.Contains("{{", StringComparison.Ordinal))
        {
            return "<dynamic>";
        }

        string description = TextCleanup.CollapseWhitespace(systemMessage.TrimStart('='), maxChars);
        return SecretFilter.DropIfSecret(description);
    }

    private static List<string> CapabilitiesOfToolType(string nodeType)
    {
        string typeSuffix = TypeSuffix(nodeType);
        List<string> capabilities = CapabilityClassifier.ClassifyToolName(typeSuffix);
        if (typeSuffix == "mcpClientTool")
        {
            ListHelpers.AddDistinct(capabilities, Capabilities.Mcp);
        }
        else if (typeSuffix == "toolCode")
        {
            ListHelpers.AddDistinct(capabilities, Capabilities.CodeExecution);
        }
        else if (typeSuffix == "toolHttpRequest" || typeSuffix == "httpRequestTool")
        {
            ListHelpers.AddDistinct(capabilities, Capabilities.WebAccess);
        }
        else if (typeSuffix == "toolWorkflow" || typeSuffix == "agentTool")
        {
            ListHelpers.AddDistinct(capabilities, Capabilities.Delegation);
        }

        return capabilities;
    }

    /// <summary>"@n8n/n8n-nodes-langchain.lmChatOpenAi" gives "lmChatOpenAi".</summary>
    private static string TypeSuffix(string nodeType)
    {
        int lastDot = nodeType.LastIndexOf('.');
        if (lastDot < 0)
        {
            return nodeType;
        }

        return nodeType.Substring(lastDot + 1);
    }

    private static string ReadStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string ReadStringPropertyOrEmpty(JsonElement element, string propertyName)
    {
        string value = ReadStringProperty(element, propertyName);
        if (value == null)
        {
            return "";
        }

        return value;
    }
}

/// <summary>An agent found in one n8n workflow.</summary>
public class N8nAgent
{
    public N8nAgent(string workflowName, bool isActive, DetectedAgent agent)
    {
        WorkflowName = workflowName;
        IsActive = isActive;
        Agent = agent;
    }

    public string WorkflowName { get; }

    /// <summary>An active workflow runs on its triggers: the design doc counts that as Running.</summary>
    public bool IsActive { get; }

    public DetectedAgent Agent { get; }
}

/// <summary>
/// Reads workflows from a local n8n database (<c>%USERPROFILE%\.n8n\database.sqlite</c>).
/// Opened read-only, because n8n may be running and writing to it.
/// </summary>
public static class N8nDatabaseReader
{
    public static List<N8nAgent> ReadAgents(string databasePath)
    {
        List<N8nAgent> agents = new List<N8nAgent>();
        SqliteConnectionStringBuilder connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        try
        {
            using SqliteConnection connection = new SqliteConnection(connectionString.ToString());
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name, active, nodes, connections FROM workflow_entity";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string workflowName = ReadTextColumn(reader, 0, "");
                bool isActive = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                string nodesJson = ReadTextColumn(reader, 2, "[]");
                string connectionsJson = ReadTextColumn(reader, 3, "{}");

                foreach (DetectedAgent agent in N8nWorkflowParser.FindAgents(workflowName, nodesJson, connectionsJson, databasePath))
                {
                    agents.Add(new N8nAgent(workflowName, isActive, agent));
                }
            }
        }
        catch (SqliteException)
        {
            // Not an n8n database, an older schema, or locked: nothing to report from it.
        }

        return agents;
    }

    private static string ReadTextColumn(SqliteDataReader reader, int column, string valueWhenEmpty)
    {
        if (reader.IsDBNull(column))
        {
            return valueWhenEmpty;
        }

        return reader.GetString(column);
    }
}
