using System.Text.RegularExpressions;

namespace ShadowAgents;

/// <summary>
/// One import in a JS/TS file. <see cref="ImportedNames"/> holds every name the import brings in,
/// both the exported names (<c>Agent</c>) and local aliases (<c>agents</c> in <c>import * as agents</c>).
/// </summary>
public class JavaScriptImport
{
    public JavaScriptImport(string module, List<string> importedNames)
    {
        Module = module;
        ImportedNames = importedNames;
    }

    public string Module { get; }
    public List<string> ImportedNames { get; }
}

/// <summary>
/// TypeScript or JavaScript source text: which characters are code, plus imports and
/// <c>const</c>/<c>let</c>/<c>var</c> declarations.
/// </summary>
public class JavaScriptSource : SourceCode
{
    private List<JavaScriptImport> imports;
    private Dictionary<string, List<int>> declarationValueOffsets;

    public JavaScriptSource(string text)
        : base(text, ClassifyCharacters(text))
    {
    }

    public List<JavaScriptImport> Imports
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
        if (character == '_' || character == '$' || char.IsLetter(character))
        {
            return true;
        }

        return !isFirst && char.IsDigit(character);
    }

    /// <summary>Offsets of the value of each <c>const NAME = value</c> (or let/var), in file order.</summary>
    public List<int> FindDeclarationValueOffsets(string name)
    {
        Dictionary<string, List<int>> declarations = GetDeclarations();
        if (declarations.TryGetValue(name, out List<int> offsets))
        {
            return offsets;
        }

        return new List<int>();
    }

    public List<string> DeclaredNames()
    {
        return new List<string>(GetDeclarations().Keys);
    }

    /// <summary>
    /// Where the expression starting at <paramref name="start"/> ends: at a ';', a ',' or a line end
    /// outside brackets, jumping over bracketed parts that span lines.
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
            if (character == ';' || character == '\n' || character == ',')
            {
                return index;
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
            char nextCharacter = '\0';
            if (index + 1 < text.Length)
            {
                nextCharacter = text[index + 1];
            }
            int end;

            if (character == '/' && nextCharacter == '/')
            {
                end = text.IndexOf('\n', index);
                if (end < 0)
                {
                    end = text.Length;
                }

                ListHelpers.FillRange(charKinds, CharKind.Comment, index, end - index);
                index = end;
                continue;
            }

            if (character == '/' && nextCharacter == '*')
            {
                int closeIndex = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (closeIndex < 0)
                {
                    end = text.Length;
                }
                else
                {
                    end = closeIndex + 2;
                }
                ListHelpers.FillRange(charKinds, CharKind.Comment, index, end - index);
                index = end;
                continue;
            }

            if (character == '"' || character == '\'')
            {
                end = FindQuotedStringEnd(text, index);
                ListHelpers.FillRange(charKinds, CharKind.StringLiteral, index, end - index);
                index = end;
                continue;
            }

            // Template literal. The ${...} parts inside are treated as string too: a simplification
            // that is fine for finding agent constructors.
            if (character == '`')
            {
                end = FindTemplateEnd(text, index);
                ListHelpers.FillRange(charKinds, CharKind.StringLiteral, index, end - index);
                index = end;
                continue;
            }

            if (character == '/' && LooksLikeRegexStart(text, charKinds, index))
            {
                end = FindRegexEnd(text, index);
                ListHelpers.FillRange(charKinds, CharKind.StringLiteral, index, end - index);
                index = end;
                continue;
            }

            charKinds[index] = CharKind.Code;
            index++;
        }

        return charKinds;
    }

    private static int FindQuotedStringEnd(string text, int quoteIndex)
    {
        char quote = text[quoteIndex];
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

            if (character == '\n')
            {
                return position;
            }

            position++;
        }

        return Math.Min(position, text.Length);
    }

    private static int FindTemplateEnd(string text, int backtickIndex)
    {
        int position = backtickIndex + 1;
        while (position < text.Length)
        {
            if (text[position] == '\\')
            {
                position += 2;
                continue;
            }

            if (text[position] == '`')
            {
                return position + 1;
            }

            position++;
        }

        return text.Length;
    }

    /// <summary>
    /// A '/' starts a regular expression (not a division) when the code before it cannot end a value:
    /// after "(", ",", "=", ":", "[", "!", "&amp;", "|", "?", "{", "}", ";" or "return", or at the file start.
    /// </summary>
    private static bool LooksLikeRegexStart(string text, CharKind[] charKindsSoFar, int slashIndex)
    {
        int position = slashIndex - 1;
        while (position >= 0 && (char.IsWhiteSpace(text[position]) || charKindsSoFar[position] == CharKind.Comment))
        {
            position--;
        }

        if (position < 0)
        {
            return true;
        }

        char previous = text[position];
        if ("(,=:[!&|?{};+-*%<>~^".Contains(previous))
        {
            return true;
        }

        bool followsReturn = position >= 5 && string.CompareOrdinal(text, position - 5, "return", 0, 6) == 0;
        return followsReturn;
    }

    private static int FindRegexEnd(string text, int slashIndex)
    {
        bool insideCharacterClass = false;
        int position = slashIndex + 1;
        while (position < text.Length)
        {
            char character = text[position];
            if (character == '\\')
            {
                position += 2;
                continue;
            }

            if (character == '\n')
            {
                // Not a real regex after all; stop at the line end so the next line is read normally.
                return position;
            }

            if (character == '[')
            {
                insideCharacterClass = true;
            }
            else if (character == ']')
            {
                insideCharacterClass = false;
            }
            else if (character == '/' && !insideCharacterClass)
            {
                position++;
                while (position < text.Length && char.IsLetter(text[position]))
                {
                    position++; // flags such as /gi
                }

                return position;
            }

            position++;
        }

        return text.Length;
    }

    // ------------------------------------------------------------ imports and declarations

    private List<JavaScriptImport> ReadImports()
    {
        List<JavaScriptImport> imports = new List<JavaScriptImport>();
        AddImports(StaticImportPattern.Matches(Text), imports);
        AddImports(SideEffectImportPattern.Matches(Text), imports);
        AddImports(RequireOrDynamicImportPattern.Matches(Text), imports);
        return imports;
    }

    private void AddImports(MatchCollection matches, List<JavaScriptImport> imports)
    {
        foreach (Match match in matches)
        {
            if (!IsCode(match.Index))
            {
                continue;
            }

            string module = match.Groups["module"].Value;
            List<string> importedNames = SplitImportedNames(match.Groups["names"].Value);
            imports.Add(new JavaScriptImport(module, importedNames));
        }
    }

    /// <summary>"Mastra, { Agent, tool as t }" gives Mastra, Agent, tool and t.</summary>
    private static List<string> SplitImportedNames(string namesText)
    {
        List<string> names = new List<string>();
        string[] words = namesText.Split(new[] { ' ', '\t', '\r', '\n', ',', '{', '}', '*', ':' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            if (word == "as" || word == "type" || word == "typeof")
            {
                continue;
            }

            names.Add(word);
        }

        return names;
    }

    private Dictionary<string, List<int>> GetDeclarations()
    {
        if (declarationValueOffsets != null)
        {
            return declarationValueOffsets;
        }

        Dictionary<string, List<int>> declarations = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (Match match in DeclarationPattern.Matches(Text))
        {
            if (!IsCode(match.Index))
            {
                continue;
            }

            string name = match.Groups["name"].Value;
            int valueOffset = SkipBlankAndComments(match.Index + match.Length, Text.Length);
            if (!declarations.ContainsKey(name))
            {
                declarations[name] = new List<int>();
            }

            declarations[name].Add(valueOffset);
        }

        declarationValueOffsets = declarations;
        return declarations;
    }

    // import { Agent } from "@openai/agents";   import Anthropic from '@anthropic-ai/sdk'
    private static readonly Regex StaticImportPattern = new Regex("""\bimport\s+(?:type\s+)?(?<names>[\w$\s{},*]{1,500}?)\s*from\s*['"](?<module>[^'"\r\n]{1,200})['"]""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    // import "dotenv/config"
    private static readonly Regex SideEffectImportPattern = new Regex("""\bimport\s*['"](?<module>[^'"\r\n]{1,200})['"]""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    // const { Agent } = require("@mastra/core/agent");   const ai = await import("ai")
    private static readonly Regex RequireOrDynamicImportPattern = new Regex("""(?:\b(?:const|let|var)\s+(?<names>\{[\w$\s,:]{0,500}\}|[A-Za-z_$][\w$]*)\s*=\s*)?(?:await\s+)?\b(?:require|import)\s*\(\s*['"](?<module>[^'"\r\n]{1,200})['"]\s*\)""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    // const weatherAgent = ...   export let model: LanguageModel = ...
    private static readonly Regex DeclarationPattern = new Regex("""\b(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?::[^=;\r\n]{1,200})?=(?![=>])""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));
}
