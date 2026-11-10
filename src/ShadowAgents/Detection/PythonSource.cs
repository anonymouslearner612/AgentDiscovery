using System.Text;

namespace ShadowAgents;

/// <summary>A Python import statement. For <c>import a.b</c>, <see cref="ImportedNames"/> is empty.</summary>
public class PythonImport
{
    public PythonImport(string module, List<string> importedNames, bool isFromImport)
    {
        Module = module;
        ImportedNames = importedNames;
        IsFromImport = isFromImport;
    }

    public string Module { get; }
    public List<string> ImportedNames { get; }
    public bool IsFromImport { get; }
}

/// <summary>
/// Python source text: which characters are code, plus the imports and the simple assignments
/// (<c>name = value</c>) every rule needs.
/// </summary>
public class PythonSource : SourceCode
{
    private const int MaxImportListChars = 4096;

    private static readonly HashSet<string> PythonKeywords = new HashSet<string>
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
        "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield", "match", "case",
    };

    private readonly List<int> bracketDepthAtLineStart;
    private List<PythonImport> imports;
    private Dictionary<string, List<int>> assignmentValueOffsets;

    public PythonSource(string text)
        : base(text, ClassifyCharacters(text))
    {
        bracketDepthAtLineStart = ComputeBracketDepthAtLineStarts();
    }

    public List<PythonImport> Imports
    {
        get
        {
            if (this.imports == null)
            {
                this.imports = ReadImports();
            }

            return this.imports;
        }
    }

    public static bool IsIdentifierChar(char character, bool isFirst)
    {
        if (character == '_' || char.IsLetter(character))
        {
            return true;
        }

        return !isFirst && char.IsDigit(character);
    }

    /// <summary>
    /// Offsets of the right-hand side of every statement-level assignment to <paramref name="name"/>
    /// (also <c>self.name = ...</c>), in file order.
    /// </summary>
    public List<int> FindAssignmentValueOffsets(string name)
    {
        Dictionary<string, List<int>> assignments = GetAssignments();
        if (assignments.TryGetValue(name, out List<int> offsets))
        {
            return offsets;
        }

        return new List<int>();
    }

    public List<string> AssignedNames()
    {
        return new List<string>(GetAssignments().Keys);
    }

    /// <summary>
    /// Yields the offset of the first non-blank character of each line that starts a statement:
    /// a line that is not inside brackets, a string or a comment.
    /// </summary>
    public IEnumerable<int> StatementStarts()
    {
        for (int lineIndex = 0; lineIndex < LineStarts.Count; lineIndex++)
        {
            if (bracketDepthAtLineStart[lineIndex] != 0)
            {
                continue;
            }

            int offset = SkipSpaces(LineStarts[lineIndex]);
            if (IsCode(offset))
            {
                yield return offset;
            }
        }
    }

    /// <summary>
    /// Finds where the expression starting at <paramref name="start"/> ends: at the end of its logical
    /// line, jumping over bracketed parts that span several lines.
    /// </summary>
    public int FindExpressionEnd(int start, int maxScanChars)
    {
        int scanLimit = (int)Math.Min(Text.Length, (long)start + maxScanChars);
        int index = start;
        while (index < scanLimit)
        {
            if (!IsCode(index))
            {
                index++;
                continue;
            }

            char character = Text[index];
            if (character == '\n')
            {
                return index;
            }

            bool isLineContinuation = character == '\\' && index + 1 < Text.Length && (Text[index + 1] == '\n' || Text[index + 1] == '\r');
            if (isLineContinuation)
            {
                index += 2;
                continue;
            }

            if (CloserFor(character) != '\0')
            {
                int closingIndex = FindClosingBracket(index, maxScanChars);
                if (closingIndex < 0)
                {
                    return EndOfLine(index);
                }

                index = closingIndex + 1;
                continue;
            }

            if (IsCloser(character))
            {
                return index;
            }

            index++;
        }

        return scanLimit;
    }

    // ------------------------------------------------------------ character classification

    private static CharKind[] ClassifyCharacters(string text)
    {
        CharKind[] charKinds = new CharKind[text.Length];
        int index = 0;
        while (index < text.Length)
        {
            char character = text[index];
            if (character == '#')
            {
                int commentEnd = text.IndexOf('\n', index);
                if (commentEnd < 0)
                {
                    commentEnd = text.Length;
                }

                ListHelpers.FillRange(charKinds, CharKind.Comment, index, commentEnd - index);
                index = commentEnd;
                continue;
            }

            if (character == '"' || character == '\'')
            {
                int literalEnd = FindStringLiteralEnd(text, index);
                ListHelpers.FillRange(charKinds, CharKind.StringLiteral, index, literalEnd - index);
                index = literalEnd;
                continue;
            }

            charKinds[index] = CharKind.Code;
            index++;
        }

        return charKinds;
    }

    /// <summary>Returns the offset just past the string literal that starts with the quote at <paramref name="quoteIndex"/>.</summary>
    private static int FindStringLiteralEnd(string text, int quoteIndex)
    {
        char quote = text[quoteIndex];
        bool isTripleQuoted = quoteIndex + 2 < text.Length
            && text[quoteIndex + 1] == quote
            && text[quoteIndex + 2] == quote;

        if (isTripleQuoted)
        {
            int index = quoteIndex + 3;
            while (index < text.Length)
            {
                if (text[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                bool closesHere = text[index] == quote
                    && index + 2 < text.Length
                    && text[index + 1] == quote
                    && text[index + 2] == quote;
                if (closesHere)
                {
                    return index + 3;
                }

                index++;
            }

            return text.Length;
        }

        int position = quoteIndex + 1;
        while (position < text.Length)
        {
            char character = text[position];
            if (character == '\\')
            {
                position += 2;
                continue;
            }

            if (character == quote)
            {
                return position + 1;
            }

            // An unterminated single-line string ends at the newline; the newline itself stays code.
            if (character == '\n')
            {
                return position;
            }

            position++;
        }

        return Math.Min(position, text.Length);
    }

    private List<int> ComputeBracketDepthAtLineStarts()
    {
        List<int> depths = new List<int>(LineStarts.Count);
        int depth = 0;
        int nextLineIndex = 0;
        for (int index = 0; index <= Text.Length; index++)
        {
            while (nextLineIndex < LineStarts.Count && LineStarts[nextLineIndex] == index)
            {
                depths.Add(depth);
                nextLineIndex++;
            }

            if (index == Text.Length || !IsCode(index))
            {
                continue;
            }

            char character = Text[index];
            if (CloserFor(character) != '\0')
            {
                depth++;
            }
            else if (IsCloser(character))
            {
                // Unbalanced input must not leave us at a negative depth for the rest of the file.
                depth = Math.Max(0, depth - 1);
            }
        }

        return depths;
    }

    // ------------------------------------------------------------ imports

    private List<PythonImport> ReadImports()
    {
        List<PythonImport> imports = new List<PythonImport>();
        foreach (int statementStart in StatementStarts())
        {
            if (StartsWithWord(statementStart, "import"))
            {
                ReadPlainImport(statementStart, imports);
            }
            else if (StartsWithWord(statementStart, "from"))
            {
                ReadFromImport(statementStart, imports);
            }
        }

        return imports;
    }

    // import a.b, c as d
    private void ReadPlainImport(int statementStart, List<PythonImport> imports)
    {
        string line = LineTextAt(statementStart).Trim();
        string moduleList = line.Substring("import".Length);
        foreach (string moduleEntry in moduleList.Split(','))
        {
            string moduleName = FirstWord(moduleEntry);
            if (moduleName.Length > 0)
            {
                imports.Add(new PythonImport(moduleName, new List<string>(), isFromImport: false));
            }
        }
    }

    // from a.b import (c, d as e)
    private void ReadFromImport(int statementStart, List<PythonImport> imports)
    {
        int moduleStart = SkipSpaces(statementStart + "from".Length);
        int moduleEnd = moduleStart;
        while (moduleEnd < Text.Length && !char.IsWhiteSpace(Text[moduleEnd]))
        {
            moduleEnd++;
        }

        string moduleName = Text.Substring(moduleStart, moduleEnd - moduleStart);
        int importKeywordStart = SkipSpaces(moduleEnd);
        bool isRelativeImport = moduleName.StartsWith(".", StringComparison.Ordinal);
        if (moduleName.Length == 0 || isRelativeImport || !StartsWithWord(importKeywordStart, "import"))
        {
            return;
        }

        int namesStart = SkipSpaces(importKeywordStart + "import".Length);
        string namesText;
        if (namesStart < Text.Length && Text[namesStart] == '(')
        {
            // Import lists never contain nested brackets, so the first ')' closes them.
            int searchLength = Math.Min(MaxImportListChars, Text.Length - namesStart);
            int closingIndex = Text.IndexOf(')', namesStart, searchLength);
            int namesEnd;
            if (closingIndex < 0)
            {
                namesEnd = EndOfLine(namesStart);
            }
            else
            {
                namesEnd = closingIndex;
            }
            namesText = Text.Substring(namesStart + 1, namesEnd - namesStart - 1);
        }
        else
        {
            int namesEnd = EndOfLine(namesStart);
            namesText = Text.Substring(namesStart, namesEnd - namesStart);
        }

        List<string> importedNames = new List<string>();
        foreach (string nameEntry in namesText.Split(','))
        {
            string importedName = FirstWord(nameEntry);
            if (importedName.Length > 0)
            {
                importedNames.Add(importedName);
            }
        }

        imports.Add(new PythonImport(moduleName, importedNames, isFromImport: true));
    }

    // ------------------------------------------------------------ assignments

    private Dictionary<string, List<int>> GetAssignments()
    {
        if (assignmentValueOffsets != null)
        {
            return assignmentValueOffsets;
        }

        Dictionary<string, List<int>> assignments = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (int statementStart in StatementStarts())
        {
            int valueOffset = TryReadAssignment(statementStart, out string assignedName);
            if (valueOffset < 0)
            {
                continue;
            }

            if (!assignments.ContainsKey(assignedName))
            {
                assignments[assignedName] = new List<int>();
            }

            assignments[assignedName].Add(valueOffset);
        }

        assignmentValueOffsets = assignments;
        return assignments;
    }

    /// <summary>
    /// Reads <c>name = value</c>, <c>name: Type = value</c> or <c>self.name = value</c> at a statement start.
    /// Returns the offset of the value, or -1 when the statement is not a simple assignment.
    /// </summary>
    private int TryReadAssignment(int statementStart, out string assignedName)
    {
        assignedName = "";
        int position = statementStart;
        if (string.CompareOrdinal(Text, position, "self.", 0, 5) == 0)
        {
            position += 5;
        }

        int nameStart = position;
        while (position < Text.Length && IsIdentifierChar(Text[position], position == nameStart))
        {
            position++;
        }

        if (position == nameStart)
        {
            return -1;
        }

        string name = Text.Substring(nameStart, position - nameStart);
        if (PythonKeywords.Contains(name))
        {
            return -1;
        }

        position = SkipSpaces(position);
        if (position < Text.Length && Text[position] == ':')
        {
            // Type annotation, as in "name: str = ...". Skip to the '=' on the same line.
            int lineEnd = EndOfLine(position);
            int equalsIndex = Text.IndexOf('=', position, lineEnd - position);
            if (equalsIndex < 0)
            {
                return -1;
            }

            position = equalsIndex;
        }

        bool isPlainAssignment = position < Text.Length
            && Text[position] == '='
            && (position + 1 >= Text.Length || Text[position + 1] != '=');
        if (!isPlainAssignment)
        {
            return -1;
        }

        assignedName = name;
        return SkipSpaces(position + 1);
    }

    // ------------------------------------------------------------ small helpers

    private bool StartsWithWord(int offset, string word)
    {
        if (string.CompareOrdinal(Text, offset, word, 0, word.Length) != 0)
        {
            return false;
        }

        int afterWord = offset + word.Length;
        return afterWord >= Text.Length || !IsIdentifierChar(Text[afterWord], isFirst: false);
    }

    private int SkipSpaces(int offset)
    {
        while (offset < Text.Length && (Text[offset] == ' ' || Text[offset] == '\t'))
        {
            offset++;
        }

        return offset;
    }

    /// <summary>"  langchain.agents as la " becomes "langchain.agents".</summary>
    private static string FirstWord(string text)
    {
        string trimmed = text.Trim().TrimEnd('\\').Trim();
        int spaceIndex = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        string word;
        if (spaceIndex < 0)
        {
            word = trimmed;
        }
        else
        {
            word = trimmed.Substring(0, spaceIndex);
        }
        return word.TrimEnd(')', ';');
    }
}

/// <summary>Reads Python literal values from argument text.</summary>
public static class PythonLiterals
{
    /// <summary>
    /// Reads a string literal, including prefixed (<c>f"..."</c>, <c>r'...'</c>), triple-quoted and
    /// implicitly concatenated ones (<c>("part one " "part two")</c>).
    /// Returns false when the expression is anything other than string literals.
    /// </summary>
    public static bool TryReadString(string expression, out string value)
    {
        value = "";
        string text = expression.Trim();
        if (text.Length >= 2 && text[0] == '(' && text[text.Length - 1] == ')')
        {
            text = text.Substring(1, text.Length - 2);
        }

        StringBuilder combined = new StringBuilder();
        int position = 0;
        bool foundLiteral = false;

        while (true)
        {
            position = SkipWhitespaceAndContinuations(text, position);
            if (position >= text.Length)
            {
                break;
            }

            int prefixStart = position;
            while (position < text.Length && position - prefixStart < 3 && "rRbBuUfF".Contains(text[position]))
            {
                position++;
            }

            bool isRaw = text.Substring(prefixStart, position - prefixStart).IndexOf("r", StringComparison.OrdinalIgnoreCase) >= 0;
            if (position >= text.Length || (text[position] != '"' && text[position] != '\''))
            {
                return false;
            }

            char quote = text[position];
            bool isTripleQuoted = position + 2 < text.Length && text[position + 1] == quote && text[position + 2] == quote;
            int quoteLength = 1;
            if (isTripleQuoted)
            {
                quoteLength = 3;
            }
            int contentStart = position + quoteLength;
            int contentEnd = FindClosingQuote(text, contentStart, quote, isTripleQuoted);
            if (contentEnd < 0)
            {
                return false;
            }

            string content = text.Substring(contentStart, contentEnd - contentStart);
            if (isRaw)
            {
                combined.Append(content);
            }
            else
            {
                combined.Append(Unescape(content));
            }
            position = contentEnd + quoteLength;
            foundLiteral = true;
        }

        value = combined.ToString();
        return foundLiteral;
    }

    public static bool IsIdentifier(string expression)
    {
        string text = expression.Trim();
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

            if (!PythonSource.IsIdentifierChar(character, atSegmentStart))
            {
                return false;
            }

            atSegmentStart = false;
        }

        return !atSegmentStart;
    }

    /// <summary>"self.support_llm" becomes "support_llm".</summary>
    public static string LastSegment(string dottedName)
    {
        int lastDot = dottedName.LastIndexOf('.');
        if (lastDot < 0)
        {
            return dottedName;
        }

        return dottedName.Substring(lastDot + 1);
    }

    /// <summary>True for values that switch a feature off: False, None, empty list, empty dict.</summary>
    public static bool IsFalsy(string expression)
    {
        string text = expression.Trim();
        string[] emptyValues = { "False", "None", "[]", "{}", "()", "0", "\"\"", "''" };
        return Array.IndexOf(emptyValues, text) >= 0;
    }

    private static int SkipWhitespaceAndContinuations(string text, int position)
    {
        while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == '\\'))
        {
            position++;
        }

        return position;
    }

    private static int FindClosingQuote(string text, int start, char quote, bool isTripleQuoted)
    {
        int position = start;
        while (position < text.Length)
        {
            char character = text[position];
            if (character == '\\')
            {
                position += 2;
                continue;
            }

            if (character == quote)
            {
                if (!isTripleQuoted)
                {
                    return position;
                }

                if (position + 2 < text.Length && text[position + 1] == quote && text[position + 2] == quote)
                {
                    return position;
                }
            }

            position++;
        }

        return -1;
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

            char escaped = content[index + 1];
            index++;
            switch (escaped)
            {
                case 'n':
                    unescaped.Append('\n');
                    break;
                case 't':
                    unescaped.Append('\t');
                    break;
                case '\\':
                case '\'':
                case '"':
                    unescaped.Append(escaped);
                    break;
                case '\n':
                    // Backslash-newline joins lines inside a string.
                    break;
                default:
                    unescaped.Append('\\').Append(escaped);
                    break;
            }
        }

        return unescaped.ToString();
    }
}
