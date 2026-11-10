using System.Text.RegularExpressions;

namespace ShadowAgents;

/// <summary>
/// Finds agent definitions in one TypeScript or JavaScript file and profiles them.
/// Works the same way as <see cref="PythonAgentDetector"/>:
/// 1. cheap pre-check, 2. imports decide which frameworks are used, 3. find constructor calls,
/// 4. read name, description, model and tools from the options object, 5. merge wrappers and classify.
/// </summary>
public class JavaScriptAgentDetector
{
    private const int MaxModelAssignmentsPerFile = 200;
    private const int MaxHelperCallsPerFile = 32;

    private readonly IReadOnlyList<FrameworkRule> rules;
    private readonly FrameworkPreCheck preCheck;

    public JavaScriptAgentDetector()
    {
        rules = JavaScriptFrameworkRules.All;
        preCheck = new FrameworkPreCheck(rules);
    }

    public FileDetectionResult Detect(string sourceText, string filePath)
    {
        if (!preCheck.MentionsAnyFramework(sourceText))
        {
            return FileDetectionResult.Empty();
        }

        try
        {
            return DetectFrameworkFile(sourceText, filePath);
        }
        catch (RegexMatchTimeoutException)
        {
            // Hostile input made a regex too slow. Report the cap instead of failing the scan.
            return new FileDetectionResult { DetectionCapHit = true };
        }
    }

    private FileDetectionResult DetectFrameworkFile(string sourceText, string filePath)
    {
        JavaScriptSource source = new JavaScriptSource(sourceText);
        List<FrameworkRule> importedRules = FindImportedRules(source);
        if (importedRules.Count == 0)
        {
            return FileDetectionResult.Empty();
        }

        FileDetectionResult result = new FileDetectionResult();
        foreach (FrameworkRule rule in importedRules)
        {
            result.ReferencedFrameworks.Add(rule.Key);
        }

        JavaScriptValueReader valueReader = new JavaScriptValueReader(source, Settings.MaxConstructorScanChars);
        result.ModelAssignments = CollectModelAssignments(source, valueReader);

        List<Candidate> candidates = FindCandidates(source, valueReader, importedRules, filePath, result);
        bool fileShowsHumanApproval = CapabilityClassifier.ShowsHumanApproval(sourceText);
        foreach (Candidate candidate in candidates)
        {
            candidate.Agent.HasHumanApproval = fileShowsHumanApproval;
            if (candidate.Constructor.IsGraphBuilder)
            {
                FillGraphBuilder(source, valueReader, candidate, result.ModelAssignments);
            }
        }

        candidates = MergeWrappedAgents(source, valueReader, candidates);
        AttachToolMethodCalls(source, valueReader, candidates);
        foreach (Candidate candidate in candidates)
        {
            CapabilityClassifier.Apply(candidate.Agent);
            candidate.Agent.Confidence = AgentConfidence.Decide(candidate.Agent, candidate.IsGraphWithoutNodes);
            result.Agents.Add(candidate.Agent);
        }

        return result;
    }

    private List<FrameworkRule> FindImportedRules(JavaScriptSource source)
    {
        List<FrameworkRule> importedRules = new List<FrameworkRule>();
        foreach (FrameworkRule rule in rules)
        {
            foreach (JavaScriptImport javaScriptImport in source.Imports)
            {
                if (rule.MatchesModule(javaScriptImport.Module))
                {
                    importedRules.Add(rule);
                    break;
                }
            }
        }

        return importedRules;
    }

    // ------------------------------------------------------------ constructor calls

    private List<Candidate> FindCandidates(
        JavaScriptSource source,
        JavaScriptValueReader valueReader,
        List<FrameworkRule> importedRules,
        string filePath,
        FileDetectionResult result)
    {
        // Which imported rules offer each constructor name. "Agent" may belong to several frameworks.
        Dictionary<string, List<RuleAndConstructor>> optionsByCallName = new Dictionary<string, List<RuleAndConstructor>>();
        foreach (FrameworkRule rule in importedRules)
        {
            foreach (AgentConstructor constructor in rule.Constructors)
            {
                if (!optionsByCallName.ContainsKey(constructor.CallName))
                {
                    optionsByCallName[constructor.CallName] = new List<RuleAndConstructor>();
                }

                optionsByCallName[constructor.CallName].Add(new RuleAndConstructor(rule, constructor));
            }
        }

        int maxAttempts = Settings.MaxConstructorAttemptsPerFile;
        List<CallSite> callSites = new List<CallSite>();
        foreach (string callName in optionsByCallName.Keys)
        {
            callSites.AddRange(FindCallSites(source, callName, maxAttempts + 1));
        }

        callSites.Sort(CompareByNameStart);
        if (callSites.Count > maxAttempts)
        {
            result.DetectionCapHit = true;
            callSites = callSites.GetRange(0, maxAttempts);
        }

        List<Candidate> candidates = new List<Candidate>();
        foreach (CallSite callSite in callSites)
        {
            RuleAndConstructor chosen = ChooseRule(source, optionsByCallName[callSite.CallName], callSite);
            if (chosen == null)
            {
                continue;
            }

            Candidate candidate = BuildCandidate(source, valueReader, chosen, callSite, filePath);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>Decides which framework a call belongs to. Returns null when it is not a framework constructor.</summary>
    private static RuleAndConstructor ChooseRule(JavaScriptSource source, List<RuleAndConstructor> options, CallSite callSite)
    {
        // agents.Agent(...) after "import * as agents from '@openai/agents'"
        if (callSite.Qualifier.Length > 0)
        {
            string qualifierName = JavaScriptValueReader.FirstSegment(callSite.Qualifier);
            foreach (RuleAndConstructor option in options)
            {
                if (IsNameImportedFromRule(source, qualifierName, option.Rule))
                {
                    return option;
                }
            }

            return null; // a method of something else, such as client.query(...)
        }

        string importedName = JavaScriptValueReader.FirstSegment(callSite.CallName);
        foreach (RuleAndConstructor option in options)
        {
            if (IsNameImportedFromRule(source, importedName, option.Rule))
            {
                return option;
            }
        }

        // "import { Agent } from './my-agent'" next to a framework import: not a framework agent.
        foreach (JavaScriptImport javaScriptImport in source.Imports)
        {
            if (javaScriptImport.ImportedNames.Contains(importedName))
            {
                return null;
            }
        }

        RuleAndConstructor firstOption = options[0];
        if (firstOption.Constructor.RequiresExplicitImport)
        {
            return null;
        }

        return firstOption;
    }

    private static bool IsNameImportedFromRule(JavaScriptSource source, string name, FrameworkRule rule)
    {
        foreach (JavaScriptImport javaScriptImport in source.Imports)
        {
            if (rule.MatchesModule(javaScriptImport.Module) && javaScriptImport.ImportedNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private Candidate BuildCandidate(
        JavaScriptSource source,
        JavaScriptValueReader valueReader,
        RuleAndConstructor chosen,
        CallSite callSite,
        string filePath)
    {
        int closeParen = source.FindClosingBracket(callSite.OpenParen, Settings.MaxConstructorScanChars);
        if (closeParen < 0)
        {
            return null;
        }

        FrameworkRule rule = chosen.Rule;
        AgentConstructor constructor = chosen.Constructor;
        List<SourceSpan> arguments = source.SplitTopLevel(callSite.OpenParen, closeParen);
        List<ObjectProperty> properties = ReadOptionProperties(valueReader, rule, arguments);
        Dictionary<ArgumentRole, SourceSpan> positionalArguments = MapPositionalArguments(constructor, arguments);

        List<SourceSpan> toolValues = FindValues(properties, rule.ToolArgs, positionalArguments, ArgumentRole.Tools);
        if (constructor.RequiresTools && toolValues.Count == 0)
        {
            return null;
        }

        int line = source.LineNumberAt(callSite.NameStart);
        DetectedAgent agent = new DetectedAgent
        {
            FrameworkKey = rule.Key,
            AnchorOffset = callSite.NameStart,
            ConstructorEndOffset = closeParen + 1,
            VariableName = FindAssignedVariable(source, callSite),
            Evidence = new Evidence(filePath, line, TextCleanup.MakeSnippet(source.LineTextAt(callSite.NameStart))),
        };

        Candidate candidate = new Candidate(agent, rule, constructor, properties);
        NameAgent(valueReader, candidate, positionalArguments, filePath, line);
        agent.Description = ReadDescription(valueReader, FindValues(properties, rule.DescriptionArgs, positionalArguments, ArgumentRole.Description), rule);

        foreach (SourceSpan modelValue in FindValues(properties, rule.ModelArgs, positionalArguments, ArgumentRole.Model))
        {
            ModelReference model = valueReader.ReadModel(modelValue, out string unresolvedVariable);
            if (model != null)
            {
                ListHelpers.AddDistinct(agent.Models, model);
                agent.UnresolvedModelVariable = null;
                break;
            }

            if (agent.UnresolvedModelVariable == null)
            {
                agent.UnresolvedModelVariable = unresolvedVariable;
            }
        }

        foreach (SourceSpan toolValue in toolValues)
        {
            ListHelpers.AddDistinct(agent.Tools, valueReader.ReadToolNames(toolValue));
        }

        foreach (string capability in constructor.ImpliedCapabilities)
        {
            ListHelpers.AddDistinct(agent.Capabilities, capability);
        }

        foreach (KeyValuePair<string, string> capabilityArg in rule.CapabilityArgs)
        {
            ObjectProperty property = JavaScriptValueReader.FindProperty(properties, capabilityArg.Key);
            if (property != null && !JavaScriptValueReader.IsFalsy(valueReader.TextOf(property.Value)))
            {
                ListHelpers.AddDistinct(agent.Capabilities, capabilityArg.Value);
            }
        }

        return candidate;
    }

    /// <summary>
    /// The properties of the first object-literal argument, plus those of a nested options object
    /// when the rule has one (Claude Agent SDK: <c>query({ prompt, options: {...} })</c>).
    /// </summary>
    private static List<ObjectProperty> ReadOptionProperties(JavaScriptValueReader valueReader, FrameworkRule rule, List<SourceSpan> arguments)
    {
        List<ObjectProperty> properties = new List<ObjectProperty>();
        foreach (SourceSpan argument in arguments)
        {
            List<ObjectProperty> objectProperties = valueReader.TryReadObjectFollowingVariables(argument);
            if (objectProperties != null)
            {
                properties.AddRange(objectProperties);
                break;
            }
        }

        if (rule.NestedOptionsProperty != null)
        {
            ObjectProperty nested = JavaScriptValueReader.FindProperty(properties, rule.NestedOptionsProperty);
            if (nested != null)
            {
                List<ObjectProperty> nestedProperties = valueReader.TryReadObjectFollowingVariables(nested.Value);
                if (nestedProperties != null)
                {
                    properties.AddRange(nestedProperties);
                }
            }
        }

        return properties;
    }

    private static Dictionary<ArgumentRole, SourceSpan> MapPositionalArguments(AgentConstructor constructor, List<SourceSpan> arguments)
    {
        Dictionary<ArgumentRole, SourceSpan> positionalArguments = new Dictionary<ArgumentRole, SourceSpan>();
        for (int position = 0; position < arguments.Count && position < constructor.PositionalArgs.Count; position++)
        {
            ArgumentRole role = constructor.PositionalArgs[position];
            if (role != ArgumentRole.Ignored)
            {
                positionalArguments[role] = arguments[position];
            }
        }

        return positionalArguments;
    }

    /// <summary>The values of the named properties that exist, plus the positional argument with that role.</summary>
    private static List<SourceSpan> FindValues(
        List<ObjectProperty> properties,
        IReadOnlyList<string> propertyNames,
        Dictionary<ArgumentRole, SourceSpan> positionalArguments,
        ArgumentRole role)
    {
        List<SourceSpan> values = new List<SourceSpan>();
        foreach (string propertyName in propertyNames)
        {
            ObjectProperty property = JavaScriptValueReader.FindProperty(properties, propertyName);
            if (property != null)
            {
                values.Add(property.Value);
            }
        }

        if (positionalArguments.TryGetValue(role, out SourceSpan positionalValue))
        {
            values.Add(positionalValue);
        }

        return values;
    }

    // ------------------------------------------------------------ profile fields

    private static void NameAgent(
        JavaScriptValueReader valueReader,
        Candidate candidate,
        Dictionary<ArgumentRole, SourceSpan> positionalArguments,
        string filePath,
        int line)
    {
        DetectedAgent agent = candidate.Agent;
        foreach (SourceSpan nameValue in FindValues(candidate.Properties, candidate.Rule.NameArgs, positionalArguments, ArgumentRole.Name))
        {
            if (JavaScriptValueReader.TryReadStringLiteral(valueReader.TextOf(nameValue), out string explicitName)
                && !string.IsNullOrWhiteSpace(explicitName)
                && !SecretFilter.ContainsSecret(explicitName))
            {
                agent.Name = TextCleanup.CollapseWhitespace(explicitName, 200);
                candidate.NameSource = NameSource.ExplicitArgument;
                return;
            }
        }

        if (agent.VariableName != null)
        {
            agent.Name = agent.VariableName;
            candidate.NameSource = NameSource.Variable;
            return;
        }

        agent.Name = candidate.Rule.Key + "@" + Path.GetFileName(filePath) + ":" + line;
        candidate.NameSource = NameSource.Fallback;
    }

    private string ReadDescription(JavaScriptValueReader valueReader, List<SourceSpan> descriptionValues, FrameworkRule rule)
    {
        List<string> literalParts = new List<string>();
        bool sawDynamicValue = false;
        foreach (SourceSpan value in descriptionValues)
        {
            if (!valueReader.TryReadStringFollowingVariables(value, out string text) || string.IsNullOrWhiteSpace(text))
            {
                sawDynamicValue = true;
                continue;
            }

            literalParts.Add(text);
            if (!rule.CombineDescriptionArgs)
            {
                break;
            }
        }

        if (literalParts.Count == 0)
        {
            if (sawDynamicValue)
            {
                return "<dynamic>";
            }

            return null;
        }

        string description = TextCleanup.CollapseWhitespace(string.Join(" ", literalParts), Settings.MaxDescriptionChars);
        return SecretFilter.DropIfSecret(description);
    }

    /// <summary>Graph builders take no model or tools; fill them from ToolNode/bindTools calls and model variables.</summary>
    private void FillGraphBuilder(
        JavaScriptSource source,
        JavaScriptValueReader valueReader,
        Candidate candidate,
        Dictionary<string, ModelReference> modelAssignments)
    {
        DetectedAgent agent = candidate.Agent;
        foreach (string callName in candidate.Rule.GraphToolCalls)
        {
            foreach (CallSite call in FindCallSites(source, callName, MaxHelperCallsPerFile))
            {
                int closeParen = source.FindClosingBracket(call.OpenParen, Settings.MaxConstructorScanChars);
                if (closeParen < 0)
                {
                    continue;
                }

                List<SourceSpan> arguments = source.SplitTopLevel(call.OpenParen, closeParen);
                if (arguments.Count > 0)
                {
                    ListHelpers.AddDistinct(agent.Tools, valueReader.ReadToolNames(arguments[0]));
                }
            }
        }

        foreach (ModelReference model in modelAssignments.Values)
        {
            ListHelpers.AddDistinct(agent.Models, model);
        }

        bool hasNodes = source.Text.Contains("." + candidate.Rule.GraphNodeCall + "(", StringComparison.Ordinal);
        candidate.IsGraphWithoutNodes = !hasNodes;
    }

    /// <summary>
    /// <c>new AgentExecutor({ agent, tools })</c> wraps an agent built a few lines earlier.
    /// Report one agent, not two: merge the inner one into the wrapper.
    /// </summary>
    private static List<Candidate> MergeWrappedAgents(JavaScriptSource source, JavaScriptValueReader valueReader, List<Candidate> candidates)
    {
        HashSet<Candidate> mergedAway = new HashSet<Candidate>();
        foreach (Candidate wrapper in candidates)
        {
            foreach (string wrappedPropertyName in wrapper.Rule.WrappedAgentArgs)
            {
                ObjectProperty wrappedProperty = JavaScriptValueReader.FindProperty(wrapper.Properties, wrappedPropertyName);
                if (wrappedProperty == null)
                {
                    continue;
                }

                string valueText = valueReader.TextOf(wrappedProperty.Value);
                Candidate wrapped = null;
                foreach (Candidate other in candidates)
                {
                    if (other == wrapper || other.Rule != wrapper.Rule || mergedAway.Contains(other))
                    {
                        continue;
                    }

                    bool isSameVariable = other.Agent.VariableName == JavaScriptValueReader.LastSegment(valueText);
                    bool isInline = other.Agent.AnchorOffset >= wrappedProperty.Value.Start && other.Agent.AnchorOffset < wrappedProperty.Value.End;
                    if (isSameVariable || isInline)
                    {
                        wrapped = other;
                        break;
                    }
                }

                if (wrapped == null)
                {
                    continue;
                }

                MergeInto(wrapper, wrapped);
                mergedAway.Add(wrapped);
            }
        }

        List<Candidate> remaining = new List<Candidate>();
        foreach (Candidate candidate in candidates)
        {
            if (!mergedAway.Contains(candidate))
            {
                remaining.Add(candidate);
            }
        }

        return remaining;
    }

    private static void MergeInto(Candidate wrapper, Candidate wrapped)
    {
        DetectedAgent target = wrapper.Agent;
        DetectedAgent source = wrapped.Agent;
        if (wrapped.NameSource < wrapper.NameSource)
        {
            target.Name = source.Name;
            wrapper.NameSource = wrapped.NameSource;
        }

        if (target.Description == null || target.Description == "<dynamic>")
        {
            if (source.Description != null)
            {
                target.Description = source.Description;
            }
        }

        ListHelpers.AddDistinct(target.Models, source.Models);
        ListHelpers.AddDistinct(target.Tools, source.Tools);
        ListHelpers.AddDistinct(target.Capabilities, source.Capabilities);
        if (target.Models.Count > 0)
        {
            target.UnresolvedModelVariable = null;
        }
        else if (target.UnresolvedModelVariable == null)
        {
            target.UnresolvedModelVariable = source.UnresolvedModelVariable;
        }
    }

    /// <summary>Variables that hold a model, such as <c>const model = openai("gpt-4o")</c>, for cross-file linking.</summary>
    private static Dictionary<string, ModelReference> CollectModelAssignments(JavaScriptSource source, JavaScriptValueReader valueReader)
    {
        Dictionary<string, ModelReference> modelAssignments = new Dictionary<string, ModelReference>(StringComparer.Ordinal);
        foreach (string declaredName in source.DeclaredNames())
        {
            if (modelAssignments.Count >= MaxModelAssignmentsPerFile)
            {
                break;
            }

            string lowerName = declaredName.ToLowerInvariant();
            bool isNamedLikeModel = lowerName.Contains("model") || lowerName.Contains("llm");
            if (!isNamedLikeModel)
            {
                continue;
            }

            SourceSpan declaredValue = valueReader.FindDeclaredValue(declaredName, int.MaxValue);
            if (declaredValue == null)
            {
                continue;
            }

            ModelReference model = valueReader.ReadModel(declaredValue, out string unresolvedVariable);
            if (model != null)
            {
                modelAssignments[declaredName] = model;
            }
        }

        return modelAssignments;
    }

    // ------------------------------------------------------------ call sites and variables

    /// <summary>Finds code occurrences of <c>callName(</c> (with optional <c>new</c>) at a word boundary.</summary>
    private static List<CallSite> FindCallSites(JavaScriptSource source, string callName, int maxResults)
    {
        List<CallSite> callSites = new List<CallSite>();
        string text = source.Text;
        int searchFrom = 0;

        while (callSites.Count < maxResults)
        {
            int nameStart = text.IndexOf(callName, searchFrom, StringComparison.Ordinal);
            if (nameStart < 0)
            {
                break;
            }

            searchFrom = nameStart + callName.Length;
            if (!source.IsCode(nameStart))
            {
                continue;
            }

            bool startsAtWordBoundary = nameStart == 0 || !JavaScriptSource.IsIdentifierChar(text[nameStart - 1], isFirst: false);
            int nameEnd = nameStart + callName.Length;
            bool endsAtWordBoundary = nameEnd >= text.Length || !JavaScriptSource.IsIdentifierChar(text[nameEnd], isFirst: false);
            if (!startsAtWordBoundary || !endsAtWordBoundary)
            {
                continue;
            }

            int openParen = SkipTypeArguments(text, nameEnd);
            if (openParen >= text.Length || text[openParen] != '(' || IsDefinition(source, nameStart))
            {
                continue;
            }

            callSites.Add(new CallSite(callName, nameStart, openParen, ReadQualifier(text, nameStart)));
        }

        return callSites;
    }

    /// <summary>Skips spaces and TypeScript type arguments: "createReactAgent&lt;State&gt; (" gives the '(' position.</summary>
    private static int SkipTypeArguments(string text, int position)
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
        {
            position++;
        }

        if (position < text.Length && text[position] == '<')
        {
            int typeEnd = text.IndexOf('>', position);
            if (typeEnd < 0 || typeEnd - position > 200)
            {
                return text.Length;
            }

            position = typeEnd + 1;
        }

        return position;
    }

    private static string ReadQualifier(string text, int nameStart)
    {
        if (nameStart == 0 || text[nameStart - 1] != '.')
        {
            return "";
        }

        int qualifierEnd = nameStart - 1;
        int qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && (JavaScriptSource.IsIdentifierChar(text[qualifierStart - 1], isFirst: false) || text[qualifierStart - 1] == '.'))
        {
            qualifierStart--;
        }

        return text.Substring(qualifierStart, qualifierEnd - qualifierStart);
    }

    /// <summary>True for "function Agent(", "class Agent", or a method "query(...) {" declaration.</summary>
    private static bool IsDefinition(JavaScriptSource source, int nameStart)
    {
        string linePrefix = source.Text.Substring(source.LineStartAt(nameStart), nameStart - source.LineStartAt(nameStart)).Trim();
        return linePrefix.EndsWith("function", StringComparison.Ordinal)
            || linePrefix.EndsWith("class", StringComparison.Ordinal)
            || linePrefix.EndsWith("async", StringComparison.Ordinal);
    }

    /// <summary>
    /// "export const weatherAgent = new Agent(" gives "weatherAgent";
    /// "researcher: new Agent(" (an object property) gives "researcher".
    /// </summary>
    private static string FindAssignedVariable(JavaScriptSource source, CallSite callSite)
    {
        int qualifiedStart = callSite.NameStart;
        if (callSite.Qualifier.Length > 0)
        {
            qualifiedStart = callSite.NameStart - callSite.Qualifier.Length - 1;
        }

        int lineStart = source.LineStartAt(qualifiedStart);
        string linePrefix = source.Text.Substring(lineStart, qualifiedStart - lineStart).TrimEnd();
        linePrefix = RemoveTrailingWord(linePrefix, "new");
        linePrefix = RemoveTrailingWord(linePrefix, "await");

        string target;
        if (linePrefix.EndsWith(":", StringComparison.Ordinal))
        {
            target = linePrefix.Substring(0, linePrefix.Length - 1).Trim();
        }
        else if (linePrefix.EndsWith("=", StringComparison.Ordinal) && !linePrefix.EndsWith("==") && !linePrefix.EndsWith("!=")
                 && !linePrefix.EndsWith("<=") && !linePrefix.EndsWith(">="))
        {
            target = linePrefix.Substring(0, linePrefix.Length - 1).Trim();
            int annotationStart = target.IndexOf(':');
            if (annotationStart >= 0)
            {
                target = target.Substring(0, annotationStart).Trim();
            }
        }
        else
        {
            return null;
        }

        // Keep only the last word: "export const weatherAgent" gives "weatherAgent".
        int lastSpace = target.LastIndexOfAny(new[] { ' ', '\t', '{', '(', ',' });
        if (lastSpace >= 0)
        {
            target = target.Substring(lastSpace + 1);
        }

        if (!JavaScriptValueReader.IsIdentifier(target))
        {
            return null;
        }

        return JavaScriptValueReader.LastSegment(target);
    }

    private static string RemoveTrailingWord(string text, string word)
    {
        if (!text.EndsWith(word, StringComparison.Ordinal))
        {
            return text;
        }

        int wordStart = text.Length - word.Length;
        bool isWholeWord = wordStart == 0 || !JavaScriptSource.IsIdentifierChar(text[wordStart - 1], isFirst: false);
        if (isWholeWord)
        {
            return text.Substring(0, wordStart).TrimEnd();
        }

        return text;
    }

    // ------------------------------------------------------------ private types

    private sealed class RuleAndConstructor
    {
        public RuleAndConstructor(FrameworkRule rule, AgentConstructor constructor)
        {
            Rule = rule;
            Constructor = constructor;
        }

        public FrameworkRule Rule { get; }
        public AgentConstructor Constructor { get; }
    }

    private static int CompareByNameStart(CallSite first, CallSite second)
    {
        return first.NameStart.CompareTo(second.NameStart);
    }

    private sealed class CallSite
    {
        public CallSite(string callName, int nameStart, int openParen, string qualifier)
        {
            CallName = callName;
            NameStart = nameStart;
            OpenParen = openParen;
            Qualifier = qualifier;
        }

        public string CallName { get; }
        public int NameStart { get; }
        public int OpenParen { get; }
        public string Qualifier { get; }
    }

    /// <summary>Lower values are better names.</summary>
    private enum NameSource
    {
        ExplicitArgument,
        Variable,
        Fallback,
    }

    /// <summary>MCP SDK: <c>server.tool("get_forecast", ...)</c> adds a tool named "get_forecast" to <c>server</c>.</summary>
    private static void AttachToolMethodCalls(JavaScriptSource source, JavaScriptValueReader valueReader, List<Candidate> candidates)
    {
        foreach (Candidate candidate in candidates)
        {
            if (candidate.Agent.VariableName == null)
            {
                continue;
            }

            foreach (string methodName in candidate.Rule.ToolMethodCalls)
            {
                foreach (CallSite call in FindCallSites(source, methodName, MaxHelperCallsPerFile))
                {
                    if (call.Qualifier != candidate.Agent.VariableName)
                    {
                        continue;
                    }

                    string toolName = ReadFirstStringArgument(source, valueReader, call);
                    if (toolName != null)
                    {
                        ListHelpers.AddDistinct(candidate.Agent.Tools, toolName);
                    }
                }
            }
        }
    }

    private static string ReadFirstStringArgument(JavaScriptSource source, JavaScriptValueReader valueReader, CallSite call)
    {
        int closeParen = source.FindClosingBracket(call.OpenParen, Settings.MaxConstructorScanChars);
        if (closeParen < 0)
        {
            return null;
        }

        List<SourceSpan> arguments = source.SplitTopLevel(call.OpenParen, closeParen);
        if (arguments.Count == 0)
        {
            return null;
        }

        string value;
        if (!JavaScriptValueReader.TryReadStringLiteral(valueReader.TextOf(arguments[0]).Trim(), out value))
        {
            return null;
        }

        return value;
    }

    private sealed class Candidate
    {
        public Candidate(DetectedAgent agent, FrameworkRule rule, AgentConstructor constructor, List<ObjectProperty> properties)
        {
            Agent = agent;
            Rule = rule;
            Constructor = constructor;
            Properties = properties;
        }

        public DetectedAgent Agent { get; }
        public FrameworkRule Rule { get; }
        public AgentConstructor Constructor { get; }
        public List<ObjectProperty> Properties { get; }
        public NameSource NameSource { get; set; } = NameSource.Fallback;
        public bool IsGraphWithoutNodes { get; set; }
    }
}
