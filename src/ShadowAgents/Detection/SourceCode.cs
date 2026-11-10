namespace ShadowAgents;

/// <summary>What one character of a source file is part of.</summary>
public enum CharKind : byte
{
    Code,
    StringLiteral,
    Comment,
}

/// <summary>A range of characters in a source file: <see cref="Start"/> included, <see cref="End"/> excluded.</summary>
public class SourceSpan
{
    public SourceSpan(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
}

/// <summary>
/// Source text plus a map that says, for every character, whether it is code, inside a string literal,
/// or inside a comment. Language-specific subclasses (<c>PythonSource</c>, <c>JavaScriptSource</c>) build
/// that map; this class offers the helpers that only need it.
/// </summary>
/// <remarks>
/// This is deliberately not a full parser. It only needs to be right enough to (1) ignore framework names
/// inside strings and comments and (2) find where a call ends, in linear time on any input.
/// </remarks>
public class SourceCode
{
    private const int MaxNestingDepth = 256;

    private readonly CharKind[] charKinds;
    private readonly List<int> lineStarts;

    protected SourceCode(string text, CharKind[] charKinds)
    {
        Text = text;
        this.charKinds = charKinds;
        this.lineStarts = FindLineStarts(text);
    }

    public string Text { get; }

    public int Length
    {
        get { return Text.Length; }
    }

    /// <summary>Offsets where each line starts. Line 1 starts at offset 0.</summary>
    protected List<int> LineStarts
    {
        get { return this.lineStarts; }
    }

    public bool IsCode(int index)
    {
        return index >= 0 && index < this.charKinds.Length && this.charKinds[index] == CharKind.Code;
    }

    public bool IsComment(int index)
    {
        return index >= 0 && index < this.charKinds.Length && this.charKinds[index] == CharKind.Comment;
    }

    /// <summary>1-based line number of a character offset.</summary>
    public int LineNumberAt(int offset)
    {
        return FindLineIndex(offset) + 1;
    }

    public int LineStartAt(int offset)
    {
        return this.lineStarts[FindLineIndex(offset)];
    }

    public int EndOfLine(int offset)
    {
        int lineEnd = Text.IndexOf('\n', offset);
        if (lineEnd < 0)
        {
            return Text.Length;
        }

        return lineEnd;
    }

    public string LineTextAt(int offset)
    {
        int lineStart = LineStartAt(offset);
        int lineEnd = EndOfLine(lineStart);
        return Text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');
    }

    /// <summary>
    /// Finds the bracket that closes the one at <paramref name="openIndex"/>, skipping strings and comments.
    /// Returns -1 when brackets do not match, nesting is too deep, or the scan limit is reached.
    /// The limit protects against hostile input such as thousands of unclosed "Agent(" tokens.
    /// </summary>
    public int FindClosingBracket(int openIndex, int maxScanChars)
    {
        char[] expectedClosers = new char[MaxNestingDepth];
        int depth = 0;
        long scanLimitLong = Math.Min(Text.Length, (long)openIndex + maxScanChars);
        int scanLimit = (int)scanLimitLong;

        for (int index = openIndex; index < scanLimit; index++)
        {
            if (!IsCode(index))
            {
                continue;
            }

            char character = Text[index];
            char closer = CloserFor(character);
            if (closer != '\0')
            {
                if (depth == MaxNestingDepth)
                {
                    return -1;
                }

                expectedClosers[depth] = closer;
                depth++;
                continue;
            }

            if (IsCloser(character))
            {
                if (depth == 0 || expectedClosers[depth - 1] != character)
                {
                    return -1;
                }

                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits the text between an opening bracket and its closer into parts at top-level commas,
    /// for example the arguments of a call or the items of a list. Blank space and comments around
    /// each part are trimmed; empty parts (a trailing comma) are dropped.
    /// </summary>
    public List<SourceSpan> SplitTopLevel(int openIndex, int closeIndex)
    {
        List<SourceSpan> parts = new List<SourceSpan>();
        int partStart = openIndex + 1;
        int depth = 0;

        for (int index = openIndex + 1; index < closeIndex; index++)
        {
            if (!IsCode(index))
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
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                AddTrimmedPart(partStart, index, parts);
                partStart = index + 1;
            }
        }

        AddTrimmedPart(partStart, closeIndex, parts);
        return parts;
    }

    public string TextOf(SourceSpan span)
    {
        return Text.Substring(span.Start, span.End - span.Start);
    }

    public int SkipBlankAndComments(int start, int end)
    {
        int position = start;
        while (position < end && (char.IsWhiteSpace(Text[position]) || IsComment(position)))
        {
            position++;
        }

        return position;
    }

    public int TrimBlankAndCommentsFromEnd(int start, int end)
    {
        int position = end;
        while (position > start && (char.IsWhiteSpace(Text[position - 1]) || IsComment(position - 1)))
        {
            position--;
        }

        return position;
    }

    /// <summary>For an opening bracket, the matching closer; '\0' for any other character.</summary>
    public static char CloserFor(char opener)
    {
        if (opener == '(')
        {
            return ')';
        }

        if (opener == '[')
        {
            return ']';
        }

        if (opener == '{')
        {
            return '}';
        }

        return '\0';
    }

    public static bool IsCloser(char character)
    {
        return character == ')' || character == ']' || character == '}';
    }

    private void AddTrimmedPart(int start, int end, List<SourceSpan> parts)
    {
        int trimmedStart = SkipBlankAndComments(start, end);
        int trimmedEnd = TrimBlankAndCommentsFromEnd(trimmedStart, end);
        if (trimmedStart < trimmedEnd)
        {
            parts.Add(new SourceSpan(trimmedStart, trimmedEnd));
        }
    }

    private int FindLineIndex(int offset)
    {
        int searchResult = this.lineStarts.BinarySearch(offset);
        if (searchResult >= 0)
        {
            return searchResult;
        }

        // When the exact value is not in the list, BinarySearch returns the bitwise complement (~) of the
        // index of the next larger line start. The line we want is the one before that.
        int nextLineIndex = ~searchResult;
        return nextLineIndex - 1;
    }

    private static List<int> FindLineStarts(string text)
    {
        List<int> lineStarts = new List<int>();
        lineStarts.Add(0);
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        return lineStarts;
    }
}
