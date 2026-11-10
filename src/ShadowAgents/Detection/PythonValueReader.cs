using System.Text.RegularExpressions;

namespace ShadowAgents;

/// <summary>
/// Interprets argument values: string literals, model objects and tool lists. When a value is a
/// variable, follows it to its assignment in the same file (Section 7.1, steps 4 and 5).
/// </summary>
internal sealed class PythonValueReader
{
    // How many variable-to-assignment hops we follow before giving up. Prevents cycles like "a = b; b = a".
    private const int MaxFollowDepth = 3;
    private const int MaxToolsPerAgent = 100;

    private static readonly string[] ModelKeywordNames = new string[]
    {
        "model", "model_name", "model_id", "deployment_name", "azure_deployment", "ai_model_id", "modelId", "deployment",
    };

    private static readonly string[] ToolFunctionKeywordNames = new string[] { "fn", "func", "function", "coroutine" };

    private readonly PythonSource source;
    private readonly int maxScanChars;

    public PythonValueReader(PythonSource source, int maxScanChars)
    {
        this.source = source;
        this.maxScanChars = maxScanChars;
    }

    public static CallArgument FindKeyword(IReadOnlyList<CallArgument> arguments, string keywordName)
    {
        foreach (CallArgument argument in arguments)
        {
            if (argument.Name == keywordName)
            {
                return argument;
            }
        }

        return null;
    }

    public bool TryReadString(CallArgument argument, out string value)
    {
        return PythonLiterals.TryReadString(argument.ValueText(this.source), out value);
    }

    /// <summary>Reads a string literal, following a variable to a string constant such as <c>PROMPT = """..."""</c>.</summary>
    public bool TryReadStringFollowingVariables(int valueStart, int valueEnd, out string value)
    {
        int start = valueStart;
        int end = valueEnd;
        for (int depth = 0; depth <= MaxFollowDepth; depth++)
        {
            string valueText = this.source.Text.Substring(start, end - start);
            if (PythonLiterals.TryReadString(valueText, out value))
            {
                return true;
            }

            if (!PythonLiterals.IsIdentifier(valueText))
            {
                break;
            }

            SourceSpan assignedValue = FindAssignedValue(valueText.Trim(), start);
            if (assignedValue == null)
            {
                break;
            }

            start = assignedValue.Start;
            end = assignedValue.End;
        }

        value = "";
        return false;
    }

    /// <summary>Reads <c>Callee(args)</c> at the start of a value. Trailing method chains are ignored.</summary>
    public bool TryReadCall(int valueStart, int valueEnd, out string calleeName, out List<CallArgument> arguments)
    {
        calleeName = "";
        arguments = new List<CallArgument>();
        string text = this.source.Text;

        int position = valueStart;
        while (position < valueEnd && (PythonSource.IsIdentifierChar(text[position], position == valueStart) || text[position] == '.'))
        {
            position++;
        }

        if (position == valueStart)
        {
            return false;
        }

        string callee = text.Substring(valueStart, position - valueStart);
        while (position < valueEnd && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position >= valueEnd || text[position] != '(')
        {
            return false;
        }

        int closeIndex = this.source.FindClosingBracket(position, this.maxScanChars);
        if (closeIndex < 0)
        {
            return false;
        }

        calleeName = callee;
        arguments = CallArguments.Split(this.source, position, closeIndex);
        return true;
    }

    /// <summary>Peeks at the called name without matching brackets. Cheap; used before the expensive checks.</summary>
    public string PeekCalleeName(int valueStart, int valueEnd)
    {
        string text = this.source.Text;
        int position = valueStart;
        while (position < valueEnd && (PythonSource.IsIdentifierChar(text[position], position == valueStart) || text[position] == '.'))
        {
            position++;
        }

        bool isFollowedByParen = position < valueEnd && text[position] == '(';
        if (isFollowedByParen)
        {
            return text.Substring(valueStart, position - valueStart);
        }

        return "";
    }

    /// <summary>Reads a list or tuple literal that makes up the whole value.</summary>
    public bool TryReadList(int valueStart, int valueEnd, out List<CallArgument> items)
    {
        items = new List<CallArgument>();
        if (valueStart >= valueEnd || (this.source.Text[valueStart] != '[' && this.source.Text[valueStart] != '('))
        {
            return false;
        }

        int closeIndex = this.source.FindClosingBracket(valueStart, this.maxScanChars);
        if (closeIndex != valueEnd - 1)
        {
            return false;
        }

        items = CallArguments.Split(this.source, valueStart, closeIndex);
        return true;
    }

    /// <summary>
    /// Finds the value assigned to a variable: the last assignment before the place it is used,
    /// or the first assignment in the file if there is none before.
    /// </summary>
    public SourceSpan FindAssignedValue(string variableName, int usedAtOffset)
    {
        string name = PythonLiterals.LastSegment(variableName);
        IReadOnlyList<int> valueOffsets = this.source.FindAssignmentValueOffsets(name);
        if (valueOffsets.Count == 0)
        {
            return null;
        }

        int chosenOffset = valueOffsets[0];
        foreach (int valueOffset in valueOffsets)
        {
            if (valueOffset < usedAtOffset)
            {
                chosenOffset = valueOffset;
            }
        }

        int valueEnd = this.source.FindExpressionEnd(chosenOffset, this.maxScanChars);
        while (valueEnd > chosenOffset && char.IsWhiteSpace(this.source.Text[valueEnd - 1]))
        {
            valueEnd--;
        }

        if (valueEnd <= chosenOffset)
        {
            return null;
        }

        return new SourceSpan(chosenOffset, valueEnd);
    }

    public ModelReference ReadModel(int valueStart, int valueEnd, out string unresolvedVariable)
    {
        return ReadModel(valueStart, valueEnd, 0, out unresolvedVariable);
    }

    /// <summary>Reads tool names from a list, a variable holding a list, or a tool factory call.</summary>
    public List<string> ReadToolNames(int valueStart, int valueEnd)
    {
        List<string> toolNames = new List<string>();
        ReadToolNames(valueStart, valueEnd, depth: 0, toolNames);
        return toolNames;
    }

    private ModelReference ReadModel(int valueStart, int valueEnd, int depth, out string unresolvedVariable)
    {
        unresolvedVariable = null;
        string valueText = this.source.Text.Substring(valueStart, valueEnd - valueStart).Trim();
        if (valueText.Length == 0)
        {
            return null;
        }

        if (PythonLiterals.TryReadString(valueText, out string modelString))
        {
            return ModelProviders.FromModelString(modelString, classProvider: null);
        }

        if (PythonLiterals.IsIdentifier(valueText))
        {
            if (depth >= MaxFollowDepth)
            {
                return null;
            }

            SourceSpan assignedValue = FindAssignedValue(valueText, valueStart);
            if (assignedValue == null)
            {
                unresolvedVariable = PythonLiterals.LastSegment(valueText);
                return null;
            }

            return ReadModel(assignedValue.Start, assignedValue.End, depth + 1, out unresolvedVariable);
        }

        if (valueText[0] == '{')
        {
            return ReadModelFromDictionary(valueText);
        }

        string calleeName = PeekCalleeName(valueStart, valueEnd);
        if (calleeName.Length == 0)
        {
            return null;
        }

        if (!TryReadCall(valueStart, valueEnd, out calleeName, out List<CallArgument> arguments))
        {
            return null;
        }

        return ReadModelFromCall(calleeName, arguments, depth, out unresolvedVariable);
    }

    private ModelReference ReadModelFromCall(string calleeName, List<CallArgument> arguments, int depth, out string unresolvedVariable)
    {
        unresolvedVariable = null;

        // model=os.getenv("MODEL_NAME", "gpt-4o"): the default value is the best static guess.
        if (IsEnvironmentLookup(calleeName))
        {
            if (arguments.Count >= 2 && !arguments[1].IsKeyword && TryReadString(arguments[1], out string defaultModel))
            {
                return ModelProviders.FromModelString(defaultModel, classProvider: null);
            }

            return null;
        }

        if (!ModelProviders.LooksLikeModelFactory(calleeName))
        {
            return null;
        }

        string classProvider = ModelProviders.FromClassName(PythonLiterals.LastSegment(calleeName));

        foreach (string keywordName in ModelKeywordNames)
        {
            CallArgument modelArgument = FindKeyword(arguments, keywordName);
            if (modelArgument == null)
            {
                continue;
            }

            if (TryReadString(modelArgument, out string modelString))
            {
                return ModelProviders.FromModelString(modelString, classProvider);
            }

            ModelReference nestedModel = ReadModel(modelArgument.ValueStart, modelArgument.ValueEnd, depth + 1, out unresolvedVariable);
            if (nestedModel != null)
            {
                if (nestedModel.Provider == null)
                {
                    return new ModelReference(nestedModel.Id, classProvider, nestedModel.Source);
                }

                return nestedModel;
            }
        }

        // Positional model name: OpenAIModel("gpt-4o"), init_chat_model("openai:gpt-4o"), LLM("gpt-4o")
        if (arguments.Count > 0 && !arguments[0].IsKeyword && TryReadString(arguments[0], out string positionalModel))
        {
            return ModelProviders.FromModelString(positionalModel, classProvider);
        }

        // ChatOpenAI() with no model argument: the provider is still worth knowing.
        if (classProvider != null)
        {
            return new ModelReference("<default>", classProvider);
        }

        return null;
    }

    private static ModelReference ReadModelFromDictionary(string dictionaryText)
    {
        Match modelMatch = DictionaryModelKey.Match(dictionaryText);
        if (!modelMatch.Success)
        {
            return null;
        }

        return ModelProviders.FromModelString(modelMatch.Groups["model"].Value, classProvider: null);
    }

    private static bool IsEnvironmentLookup(string calleeName)
    {
        return calleeName == "os.getenv" || calleeName == "getenv" || calleeName == "os.environ.get" || calleeName == "environ.get";
    }

    private void ReadToolNames(int valueStart, int valueEnd, int depth, List<string> toolNames)
    {
        string valueText = this.source.Text.Substring(valueStart, valueEnd - valueStart).Trim();
        if (valueText.Length == 0)
        {
            return;
        }

        if (TryReadList(valueStart, valueEnd, out List<CallArgument> items))
        {
            foreach (CallArgument item in items)
            {
                AddToolNameFromItem(item, toolNames);
            }

            return;
        }

        if (PythonLiterals.IsIdentifier(valueText))
        {
            if (depth >= MaxFollowDepth)
            {
                return;
            }

            SourceSpan assignedValue = FindAssignedValue(valueText, valueStart);
            if (assignedValue != null)
            {
                ReadToolNames(assignedValue.Start, assignedValue.End, depth + 1, toolNames);
            }

            return;
        }

        if (!TryReadCall(valueStart, valueEnd, out string calleeName, out List<CallArgument> arguments))
        {
            return;
        }

        // load_tools(["serpapi", "llm-math"], llm=llm) or similar: the first argument lists the tools.
        if (arguments.Count > 0 && !arguments[0].IsKeyword && TryReadList(arguments[0].ValueStart, arguments[0].ValueEnd, out List<CallArgument> listItems))
        {
            ReadToolNames(arguments[0].ValueStart, arguments[0].ValueEnd, depth + 1, toolNames);
            return;
        }

        AddToolName(ToolNameFromCall(calleeName, arguments), toolNames);
    }

    private void AddToolNameFromItem(CallArgument item, List<string> toolNames)
    {
        string itemText = item.ValueText(this.source).Trim();
        if (itemText.StartsWith("*", StringComparison.Ordinal))
        {
            // "*other_tools": the contents are not known statically.
            return;
        }

        if (PythonLiterals.TryReadString(itemText, out string toolString))
        {
            AddToolName(toolString, toolNames);
            return;
        }

        if (PythonLiterals.IsIdentifier(itemText))
        {
            AddToolName(PythonLiterals.LastSegment(itemText), toolNames);
            return;
        }

        if (TryReadCall(item.ValueStart, item.ValueEnd, out string calleeName, out List<CallArgument> arguments))
        {
            AddToolName(ToolNameFromCall(calleeName, arguments), toolNames);
        }
    }

    /// <summary>
    /// Names a tool built by a call. Prefers an explicit <c>name=</c>, then the wrapped function
    /// (<c>FunctionTool.from_defaults(fn=multiply)</c>), then the class name (<c>SerperDevTool()</c>).
    /// </summary>
    private string ToolNameFromCall(string calleeName, List<CallArgument> arguments)
    {
        CallArgument nameArgument = FindKeyword(arguments, "name");
        if (nameArgument != null && TryReadString(nameArgument, out string explicitName))
        {
            return explicitName;
        }

        foreach (string keywordName in ToolFunctionKeywordNames)
        {
            CallArgument functionArgument = FindKeyword(arguments, keywordName);
            if (functionArgument != null && PythonLiterals.IsIdentifier(functionArgument.ValueText(this.source)))
            {
                return PythonLiterals.LastSegment(functionArgument.ValueText(this.source).Trim());
            }
        }

        string lastSegment = PythonLiterals.LastSegment(calleeName);
        bool isFactoryMethod = lastSegment.Length > 0 && char.IsLower(lastSegment[0]);
        if (isFactoryMethod && arguments.Count > 0 && !arguments[0].IsKeyword
            && PythonLiterals.IsIdentifier(arguments[0].ValueText(this.source)))
        {
            return PythonLiterals.LastSegment(arguments[0].ValueText(this.source).Trim());
        }

        if (isFactoryMethod)
        {
            return calleeName;
        }

        return lastSegment;
    }

    private static void AddToolName(string toolName, List<string> toolNames)
    {
        if (string.IsNullOrWhiteSpace(toolName) || toolNames.Count >= MaxToolsPerAgent)
        {
            return;
        }

        string cleanName = TextCleanup.CollapseWhitespace(toolName, 200);
        if (SecretFilter.ContainsSecret(cleanName) || toolNames.Contains(cleanName))
        {
            return;
        }

        toolNames.Add(cleanName);
    }

    // AutoGen 0.2 llm_config: {"config_list": [{"model": "gpt-4", ...}]} or {"model": "gpt-4"}
    private static readonly Regex DictionaryModelKey = new Regex("""["']model["']\s*:\s*["'](?<model>[^"'\r\n]{1,200})["']""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// One argument of a call or one item of a list. <see cref="Name"/> is set for keyword arguments
/// (<c>role="x"</c>) and null for positional ones. Offsets index into the whole file.
/// </summary>
public class CallArgument
{
    public CallArgument(string name, int valueStart, int valueEnd)
    {
        Name = name;
        ValueStart = valueStart;
        ValueEnd = valueEnd;
    }

    public string Name { get; }
    public int ValueStart { get; }
    public int ValueEnd { get; }

    public bool IsKeyword
    {
        get { return Name != null; }
    }

    public string ValueText(PythonSource source)
    {
        return source.Text.Substring(ValueStart, ValueEnd - ValueStart);
    }
}

public static class CallArguments
{
    /// <summary>
    /// Splits the text between a bracket at <paramref name="openIndex"/> and its closer at
    /// <paramref name="closeIndex"/> into arguments, and recognises keyword arguments (<c>name=value</c>).
    /// </summary>
    public static List<CallArgument> Split(PythonSource source, int openIndex, int closeIndex)
    {
        List<CallArgument> arguments = new List<CallArgument>();
        foreach (SourceSpan part in source.SplitTopLevel(openIndex, closeIndex))
        {
            string keywordName = TryReadKeyword(source, part.Start, part.End, out int valueStart);
            if (keywordName == null)
            {
                arguments.Add(new CallArgument(null, part.Start, part.End));
            }
            else
            {
                arguments.Add(new CallArgument(keywordName, valueStart, part.End));
            }
        }

        return arguments;
    }

    /// <summary>Reads <c>name=</c> at <paramref name="start"/>. Returns null when the argument is positional.</summary>
    private static string TryReadKeyword(PythonSource source, int start, int end, out int valueStart)
    {
        valueStart = start;
        string text = source.Text;
        int position = start;
        while (position < end && PythonSource.IsIdentifierChar(text[position], position == start))
        {
            position++;
        }

        if (position == start)
        {
            return null;
        }

        int nameEnd = position;
        while (position < end && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        // "name=value" is a keyword argument; "name == value" is a comparison.
        bool isKeyword = position < end
            && text[position] == '='
            && (position + 1 >= end || text[position + 1] != '=');
        if (!isKeyword)
        {
            return null;
        }

        valueStart = source.SkipBlankAndComments(position + 1, end);
        return text.Substring(start, nameEnd - start);
    }
}
