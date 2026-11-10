using System.Text;

namespace ShadowAgents;

/// <summary>One property of an object literal: <c>name: value</c>, or the shorthand <c>name</c>.</summary>
public class ObjectProperty
{
    public ObjectProperty(string name, SourceSpan value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public SourceSpan Value { get; }
}

/// <summary>
/// Interprets JS/TS values: strings, object literals, arrays, model calls and tool lists. When a value
/// is a variable, follows it to its <c>const</c> declaration in the same file.
/// </summary>
internal class JavaScriptValueReader
{
    private const int MaxFollowDepth = 3;
    private const int MaxToolsPerAgent = 100;

    private static readonly string[] ModelPropertyNames =
    {
        "model", "modelName", "modelId", "model_id", "deployment", "deploymentName", "azureOpenAIApiDeploymentName",
    };

    // Calls that create a tool but say nothing useful by their own name.
    private static readonly HashSet<string> GenericToolFactories = new HashSet<string>
    {
        "tool", "createTool", "DynamicTool", "DynamicStructuredTool", "FunctionTool.from", "tool.from",
    };

    private readonly JavaScriptSource source;
    private readonly int maxScanChars;

    public JavaScriptValueReader(JavaScriptSource source, int maxScanChars)
    {
        this.source = source;
        this.maxScanChars = maxScanChars;
    }

    public string TextOf(SourceSpan span)
    {
        return this.source.TextOf(span).Trim();
    }

    public static ObjectProperty FindProperty(List<ObjectProperty> properties, string name)
    {
        foreach (ObjectProperty property in properties)
        {
            if (property.Name == name)
            {
                return property;
            }
        }

        return null;
    }

    public static bool IsIdentifier(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        bool atSegmentStart = true;
        foreach (char character in text)
        {
            if (character == '.')
            {
                if (atSegmentStart)
                {
                    return false;
                }

                atSegmentStart = true;
                continue;
            }

            if (!JavaScriptSource.IsIdentifierChar(character, atSegmentStart))
            {
                return false;
            }

            atSegmentStart = false;
        }

        return !atSegmentStart;
    }

    public static string LastSegment(string dottedName)
    {
        int lastDot = dottedName.LastIndexOf('.');
        if (lastDot < 0)
        {
            return dottedName;
        }

        return dottedName.Substring(lastDot + 1);
    }

    public static string FirstSegment(string dottedName)
    {
        int firstDot = dottedName.IndexOf('.');
        if (firstDot < 0)
        {
            return dottedName;
        }

        return dottedName.Substring(0, firstDot);
    }

    /// <summary>True for values that switch a feature off: false, null, undefined, empty array or object.</summary>
    public static bool IsFalsy(string text)
    {
        return text == "false" || text == "null" || text == "undefined" || text == "[]" || text == "{}" || text == "\"\"" || text == "''";
    }

    /// <summary>
    /// Reads a string literal: '...', "..." or a template literal without ${...} parts.
    /// Returns false for anything else, including templates with placeholders (their text is dynamic).
    /// </summary>
    public static bool TryReadStringLiteral(string text, out string value)
    {
        value = "";
        if (text.Length < 2)
        {
            return false;
        }

        char quote = text[0];
        bool isQuoted = quote == '"' || quote == '\'' || quote == '`';
        if (!isQuoted || text[text.Length - 1] != quote)
        {
            return false;
        }

        string content = text.Substring(1, text.Length - 2);
        if (quote == '`' && content.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        if (quote != '`' && ContainsUnescapedQuote(content, quote))
        {
            // Something like "a" + "b": two literals, not one.
            return false;
        }

        value = Unescape(content);
        return true;
    }

    public bool TryReadStringFollowingVariables(SourceSpan span, out string value)
    {
        SourceSpan current = span;
        for (int depth = 0; depth <= MaxFollowDepth; depth++)
        {
            string text = TextOf(current);
            if (TryReadStringLiteral(text, out value))
            {
                return true;
            }

            if (!IsIdentifier(text))
            {
                break;
            }

            SourceSpan declaredValue = FindDeclaredValue(text, current.Start);
            if (declaredValue == null)
            {
                break;
            }

            current = declaredValue;
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Finds the value of <c>const NAME = ...</c>: the last declaration before the place it is used,
    /// or the first one in the file.
    /// </summary>
    public SourceSpan FindDeclaredValue(string variableName, int usedAtOffset)
    {
        List<int> valueOffsets = this.source.FindDeclarationValueOffsets(LastSegment(variableName));
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
        valueEnd = this.source.TrimBlankAndCommentsFromEnd(chosenOffset, valueEnd);
        if (valueEnd <= chosenOffset)
        {
            return null;
        }

        return new SourceSpan(chosenOffset, valueEnd);
    }

    /// <summary>
    /// Reads <c>callee(args)</c>, <c>new Callee(args)</c> or <c>await callee(args)</c> at the start of a value.
    /// </summary>
    public bool TryReadCall(SourceSpan span, out string calleeName, out List<SourceSpan> arguments)
    {
        calleeName = "";
        arguments = new List<SourceSpan>();
        string text = this.source.Text;
        int position = SkipKeyword(span.Start, span.End, "await");
        position = SkipKeyword(position, span.End, "new");

        int nameStart = position;
        while (position < span.End && (JavaScriptSource.IsIdentifierChar(text[position], position == nameStart) || text[position] == '.'))
        {
            position++;
        }

        if (position == nameStart)
        {
            return false;
        }

        string callee = text.Substring(nameStart, position - nameStart);

        // Skip TypeScript type arguments: createAgent<State>(...)
        if (position < span.End && text[position] == '<')
        {
            int typeEnd = text.IndexOf('>', position);
            if (typeEnd < 0 || typeEnd >= span.End)
            {
                return false;
            }

            position = typeEnd + 1;
        }

        while (position < span.End && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position >= span.End || text[position] != '(')
        {
            return false;
        }

        int closeIndex = this.source.FindClosingBracket(position, this.maxScanChars);
        if (closeIndex < 0)
        {
            return false;
        }

        calleeName = callee;
        arguments = this.source.SplitTopLevel(position, closeIndex);
        return true;
    }

    /// <summary>Reads an object literal that makes up the whole value.</summary>
    public List<ObjectProperty> TryReadObject(SourceSpan span)
    {
        if (span.Start >= span.End || this.source.Text[span.Start] != '{')
        {
            return null;
        }

        int closeIndex = this.source.FindClosingBracket(span.Start, this.maxScanChars);
        if (closeIndex != span.End - 1)
        {
            return null;
        }

        List<ObjectProperty> properties = new List<ObjectProperty>();
        foreach (SourceSpan part in this.source.SplitTopLevel(span.Start, closeIndex))
        {
            ObjectProperty property = ReadProperty(part);
            if (property != null)
            {
                properties.Add(property);
            }
        }

        return properties;
    }

    /// <summary>Like <see cref="TryReadObject"/>, but also follows a variable to an object declaration.</summary>
    public List<ObjectProperty> TryReadObjectFollowingVariables(SourceSpan span)
    {
        List<ObjectProperty> properties = TryReadObject(span);
        if (properties != null)
        {
            return properties;
        }

        string text = TextOf(span);
        if (!IsIdentifier(text))
        {
            return null;
        }

        SourceSpan declaredValue = FindDeclaredValue(text, span.Start);
        if (declaredValue == null)
        {
            return null;
        }

        return TryReadObject(declaredValue);
    }

    public bool TryReadArray(SourceSpan span, out List<SourceSpan> items)
    {
        items = new List<SourceSpan>();
        if (span.Start >= span.End || this.source.Text[span.Start] != '[')
        {
            return false;
        }

        int closeIndex = this.source.FindClosingBracket(span.Start, this.maxScanChars);
        if (closeIndex != span.End - 1)
        {
            return false;
        }

        items = this.source.SplitTopLevel(span.Start, closeIndex);
        return true;
    }

    // ------------------------------------------------------------ models

    public ModelReference ReadModel(SourceSpan span, out string unresolvedVariable)
    {
        return ReadModel(span, 0, out unresolvedVariable);
    }

    private ModelReference ReadModel(SourceSpan span, int depth, out string unresolvedVariable)
    {
        unresolvedVariable = null;
        string text = TextOf(span);
        if (text.Length == 0)
        {
            return null;
        }

        if (TryReadStringLiteral(text, out string modelString))
        {
            return ModelProviders.FromModelString(modelString, null);
        }

        if (IsIdentifier(text))
        {
            if (depth >= MaxFollowDepth)
            {
                return null;
            }

            SourceSpan declaredValue = FindDeclaredValue(text, span.Start);
            if (declaredValue == null)
            {
                unresolvedVariable = LastSegment(text);
                return null;
            }

            return ReadModel(declaredValue, depth + 1, out unresolvedVariable);
        }

        if (!TryReadCall(span, out string calleeName, out List<SourceSpan> arguments))
        {
            return null;
        }

        return ReadModelFromCall(calleeName, arguments, depth, out unresolvedVariable);
    }

    /// <summary>
    /// openai("gpt-4o"), anthropic("claude-sonnet-4-5"), openai.chat("gpt-4o"),
    /// new ChatOpenAI({ model: "gpt-4o" }), new AzureChatOpenAI({ azureOpenAIApiDeploymentName: "..." }).
    /// </summary>
    private ModelReference ReadModelFromCall(string calleeName, List<SourceSpan> arguments, int depth, out string unresolvedVariable)
    {
        unresolvedVariable = null;
        string classProvider = ModelProviders.FromClassName(LastSegment(calleeName));
        if (classProvider == null)
        {
            classProvider = ModelProviders.FromClassName(FirstSegment(calleeName));
        }

        if (classProvider == null && !ModelProviders.LooksLikeModelFactory(calleeName))
        {
            return null;
        }

        if (arguments.Count > 0)
        {
            string firstArgument = TextOf(arguments[0]);
            if (TryReadStringLiteral(firstArgument, out string positionalModel))
            {
                return ModelProviders.FromModelString(positionalModel, classProvider);
            }

            List<ObjectProperty> options = TryReadObject(arguments[0]);
            if (options != null)
            {
                foreach (string propertyName in ModelPropertyNames)
                {
                    ObjectProperty modelProperty = FindProperty(options, propertyName);
                    if (modelProperty == null)
                    {
                        continue;
                    }

                    ModelReference model = ReadModel(modelProperty.Value, depth + 1, out unresolvedVariable);
                    if (model != null)
                    {
                        if (model.Provider == null && classProvider != null)
                        {
                            return new ModelReference(model.Id, classProvider);
                        }

                        return model;
                    }
                }
            }
        }

        if (classProvider != null)
        {
            return new ModelReference("<default>", classProvider);
        }

        return null;
    }

    // ------------------------------------------------------------ tools

    public List<string> ReadToolNames(SourceSpan span)
    {
        List<string> toolNames = new List<string>();
        ReadToolNames(span, 0, toolNames);
        return toolNames;
    }

    private void ReadToolNames(SourceSpan span, int depth, List<string> toolNames)
    {
        string text = TextOf(span);
        if (text.Length == 0 || depth > MaxFollowDepth)
        {
            return;
        }

        // tools: [getWeather, searchTool]
        if (TryReadArray(span, out List<SourceSpan> items))
        {
            foreach (SourceSpan item in items)
            {
                AddToolNameFromItem(item, toolNames);
            }

            return;
        }

        // tools: { getWeather: tool({...}), search }   (Vercel AI SDK, Mastra): the keys are the tool names.
        List<ObjectProperty> properties = TryReadObject(span);
        if (properties != null)
        {
            foreach (ObjectProperty property in properties)
            {
                AddToolName(property.Name, toolNames);
            }

            return;
        }

        if (IsIdentifier(text))
        {
            SourceSpan declaredValue = FindDeclaredValue(text, span.Start);
            if (declaredValue != null)
            {
                ReadToolNames(declaredValue, depth + 1, toolNames);
            }

            return;
        }

        if (TryReadCall(span, out string calleeName, out List<SourceSpan> arguments))
        {
            AddToolName(ToolNameFromCall(calleeName, arguments), toolNames);
        }
    }

    private void AddToolNameFromItem(SourceSpan item, List<string> toolNames)
    {
        string text = TextOf(item);
        if (text.StartsWith("...", StringComparison.Ordinal))
        {
            return; // Spread: the contents are not known statically.
        }

        if (TryReadStringLiteral(text, out string toolString))
        {
            AddToolName(toolString, toolNames);
            return;
        }

        if (IsIdentifier(text))
        {
            AddToolName(LastSegment(text), toolNames);
            return;
        }

        if (TryReadCall(item, out string calleeName, out List<SourceSpan> arguments))
        {
            AddToolName(ToolNameFromCall(calleeName, arguments), toolNames);
        }
    }

    /// <summary>A tool made by a call: its name/id property if it has one, else the class name.</summary>
    private string ToolNameFromCall(string calleeName, List<SourceSpan> arguments)
    {
        if (arguments.Count > 0)
        {
            List<ObjectProperty> options = TryReadObject(arguments[0]);
            if (options != null)
            {
                foreach (string nameProperty in new[] { "name", "id" })
                {
                    ObjectProperty property = FindProperty(options, nameProperty);
                    if (property != null && TryReadStringLiteral(TextOf(property.Value), out string toolName))
                    {
                        return toolName;
                    }
                }
            }
        }

        if (GenericToolFactories.Contains(calleeName))
        {
            return null;
        }

        return LastSegment(calleeName);
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

    // ------------------------------------------------------------ small helpers

    private ObjectProperty ReadProperty(SourceSpan part)
    {
        string text = this.source.Text;
        int position = part.Start;
        if (string.CompareOrdinal(text, position, "...", 0, 3) == 0)
        {
            return null;
        }

        string name;
        if (text[position] == '"' || text[position] == '\'')
        {
            // "quoted-key": value
            char quote = text[position];
            int closingQuote = text.IndexOf(quote, position + 1);
            if (closingQuote < 0 || closingQuote >= part.End)
            {
                return null;
            }

            name = text.Substring(position + 1, closingQuote - position - 1);
            position = closingQuote + 1;
        }
        else
        {
            int nameStart = position;
            while (position < part.End && JavaScriptSource.IsIdentifierChar(text[position], position == nameStart))
            {
                position++;
            }

            if (position == nameStart)
            {
                return null; // computed key [x]: value, or something unusual
            }

            name = text.Substring(nameStart, position - nameStart);
        }

        int afterName = this.source.SkipBlankAndComments(position, part.End);
        if (afterName >= part.End)
        {
            // Shorthand property: { tools } means { tools: tools }
            return new ObjectProperty(name, new SourceSpan(part.Start, position));
        }

        if (text[afterName] != ':')
        {
            return null; // method shorthand such as execute() { ... }
        }

        int valueStart = this.source.SkipBlankAndComments(afterName + 1, part.End);
        return new ObjectProperty(name, new SourceSpan(valueStart, part.End));
    }

    private int SkipKeyword(int position, int end, string keyword)
    {
        string text = this.source.Text;
        bool startsWithKeyword = string.CompareOrdinal(text, position, keyword, 0, keyword.Length) == 0
            && position + keyword.Length < end
            && char.IsWhiteSpace(text[position + keyword.Length]);
        if (!startsWithKeyword)
        {
            return position;
        }

        return this.source.SkipBlankAndComments(position + keyword.Length, end);
    }

    private static bool ContainsUnescapedQuote(string content, char quote)
    {
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] == '\\')
            {
                index++;
                continue;
            }

            if (content[index] == quote)
            {
                return true;
            }
        }

        return false;
    }

    private static string Unescape(string content)
    {
        if (!content.Contains('\\'))
        {
            return content;
        }

        StringBuilder unescaped = new StringBuilder(content.Length);
        for (int index = 0; index < content.Length; index++)
        {
            char character = content[index];
            if (character != '\\' || index + 1 >= content.Length)
            {
                unescaped.Append(character);
                continue;
            }

            index++;
            char escaped = content[index];
            if (escaped == 'n')
            {
                unescaped.Append('\n');
            }
            else if (escaped == 't')
            {
                unescaped.Append('\t');
            }
            else
            {
                unescaped.Append(escaped);
            }
        }

        return unescaped.ToString();
    }
}
