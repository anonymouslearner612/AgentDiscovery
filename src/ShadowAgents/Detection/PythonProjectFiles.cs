using System.Text.Json;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ShadowAgents;

/// <summary>
/// The code cells of a notebook joined into one Python text, plus a map from each line of that text
/// back to its cell, so evidence can point at "cell 3, line 2".
/// </summary>
public sealed class NotebookCode
{
    public NotebookCode(string text, List<NotebookLine> lineMap)
    {
        Text = text;
        LineMap = lineMap;
    }

    public string Text { get; }
    public List<NotebookLine> LineMap { get; }

    public Evidence ToCellEvidence(Evidence combinedEvidence)
    {
        int lineIndex = combinedEvidence.Line - 1;
        if (lineIndex < 0 || lineIndex >= LineMap.Count)
        {
            return combinedEvidence;
        }

        NotebookLine notebookLine = LineMap[lineIndex];
        return new Evidence(combinedEvidence.File, notebookLine.LineInCell, combinedEvidence.Snippet, notebookLine.CellIndex);
    }
}

/// <summary>Cell index is 0-based (as in the .ipynb cell array); line is 1-based.</summary>
public sealed class NotebookLine
{
    public NotebookLine(int cellIndex, int lineInCell)
    {
        CellIndex = cellIndex;
        LineInCell = lineInCell;
    }

    public int CellIndex { get; }
    public int LineInCell { get; }
}

public static class NotebookReader
{
    public static NotebookCode TryRead(string notebookJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(notebookJson, new JsonDocumentOptions { MaxDepth = 64 });
            return ReadCodeCells(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NotebookCode ReadCodeCells(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("cells", out JsonElement cells)
            || cells.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        StringBuilder combinedText = new StringBuilder();
        List<NotebookLine> lineMap = new List<NotebookLine>();
        int cellIndex = -1;

        foreach (JsonElement cell in cells.EnumerateArray())
        {
            cellIndex++;
            if (!IsCodeCell(cell))
            {
                continue;
            }

            string cellSource = ReadCellSource(cell);
            string[] cellLines = cellSource.Split('\n');
            for (int lineIndex = 0; lineIndex < cellLines.Length; lineIndex++)
            {
                combinedText.Append(NeutralizeShellAndMagicLine(cellLines[lineIndex].TrimEnd('\r'))).Append('\n');
                lineMap.Add(new NotebookLine(cellIndex, lineIndex + 1));
            }
        }

        return new NotebookCode(combinedText.ToString(), lineMap);
    }

    private static bool IsCodeCell(JsonElement cell)
    {
        return cell.ValueKind == JsonValueKind.Object
            && cell.TryGetProperty("cell_type", out JsonElement cellType)
            && cellType.ValueKind == JsonValueKind.String
            && cellType.GetString() == "code";
    }

    /// <summary>A cell's source is either one string or an array of line strings.</summary>
    private static string ReadCellSource(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out JsonElement source))
        {
            return "";
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            string text = source.GetString();
            if (text == null)
            {
                return "";
            }

            return text;
        }

        if (source.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        StringBuilder joined = new StringBuilder();
        foreach (JsonElement line in source.EnumerateArray())
        {
            if (line.ValueKind == JsonValueKind.String)
            {
                joined.Append(line.GetString());
            }
        }

        return joined.ToString();
    }

    /// <summary>"!pip install crewai" and "%load_ext ..." are not Python. Turn them into comments.</summary>
    private static string NeutralizeShellAndMagicLine(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("!", StringComparison.Ordinal) || trimmed.StartsWith("%", StringComparison.Ordinal))
        {
            return "# " + line;
        }

        return line;
    }
}

/// <summary>One agent from a CrewAI scaffold's <c>config/agents.yaml</c>.</summary>
public sealed class CrewAiYamlAgent
{
    public CrewAiYamlAgent(string key, string role, string goal, string backstory, string llm)
    {
        Key = key;
        Role = role;
        Goal = goal;
        Backstory = backstory;
        Llm = llm;
    }

    public string Key { get; }
    public string Role { get; }
    public string Goal { get; }
    public string Backstory { get; }
    public string Llm { get; }
}

/// <summary>
/// Reads CrewAI scaffold agent configs. In scaffolded projects the code says
/// <c>Agent(config=self.agents_config["researcher"])</c> and the real profile lives in YAML.
/// </summary>
public static class CrewAiConfigReader
{
    public static List<CrewAiYamlAgent> ReadAgents(string yamlText)
    {
        List<CrewAiYamlAgent> agents = new List<CrewAiYamlAgent>();
        YamlMappingNode rootMapping = TryLoadRootMapping(yamlText);
        if (rootMapping == null)
        {
            return agents;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in rootMapping.Children)
        {
            YamlScalarNode keyScalar = entry.Key as YamlScalarNode;
            YamlMappingNode agentMapping = entry.Value as YamlMappingNode;
            if (keyScalar == null || keyScalar.Value == null || agentMapping == null)
            {
                continue;
            }

            string agentKey = keyScalar.Value;

            string role = ReadScalar(agentMapping, "role");
            string goal = ReadScalar(agentMapping, "goal");
            if (role == null && goal == null)
            {
                // Without role or goal this is not an agent definition.
                continue;
            }

            agents.Add(new CrewAiYamlAgent(
                agentKey,
                role,
                goal,
                ReadScalar(agentMapping, "backstory"),
                ReadScalar(agentMapping, "llm")));
        }

        return agents;
    }

    /// <summary>True when the YAML text looks like a CrewAI agents config (role and goal keys).</summary>
    public static bool LooksLikeAgentsConfig(string yamlText)
    {
        return yamlText.Contains("role:", StringComparison.Ordinal) && yamlText.Contains("goal:", StringComparison.Ordinal);
    }

    private static YamlMappingNode TryLoadRootMapping(string yamlText)
    {
        try
        {
            YamlStream yamlStream = new YamlStream();
            yamlStream.Load(new StringReader(yamlText));
            if (yamlStream.Documents.Count == 0)
            {
                return null;
            }

            return yamlStream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static string ReadScalar(YamlMappingNode mapping, string key)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode valueNode) && valueNode is YamlScalarNode scalar)
        {
            if (string.IsNullOrWhiteSpace(scalar.Value))
            {
                return null;
            }

            return scalar.Value;
        }

        return null;
    }
}
