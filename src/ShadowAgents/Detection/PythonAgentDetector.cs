using System.Text.RegularExpressions;

namespace ShadowAgents;

/// <summary>
/// Finds agent definitions in one Python file (Stage 3, "Implemented") and profiles them:
/// name, description, model, tools and capabilities (Section 7).
/// </summary>
/// <remarks>
/// The steps, in order:
/// 1. Cheap pre-check for any framework name; most files stop here.
/// 2. Read the imports to learn which frameworks the file really uses.
/// 3. Find constructor anchors such as <c>Agent(</c>, match their brackets and split their arguments.
/// 4. Read name, description, model and tools from the arguments, following variables where needed.
/// 5. Attach tools defined elsewhere in the file (decorators, registration calls), merge wrappers
///    such as <c>AgentExecutor(agent=...)</c> into one agent, and classify capabilities.
/// </remarks>
public sealed class PythonAgentDetector
{
    private const int MaxModelAssignmentsPerFile = 200;
    private const int MaxHelperCallsPerFile = 32;
    private const int MaxEnclosingFunctionSearchChars = 64 * 1024;

    private readonly IReadOnlyList<FrameworkRule> rules;
    private readonly FrameworkPreCheck preCheck;

    public PythonAgentDetector(IReadOnlyList<FrameworkRule> rules = null)
    {
        this.rules = rules;
        if (this.rules == null)
        {
            this.rules = PythonFrameworkRules.All;
        }
        preCheck = new FrameworkPreCheck(this.rules);
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

    /// <summary>Runs detection on the code cells of a Jupyter notebook and maps evidence back to cells.</summary>
    public FileDetectionResult DetectNotebook(string notebookJson, string filePath)
    {
        if (!preCheck.MentionsAnyFramework(notebookJson))
        {
            return FileDetectionResult.Empty();
        }

        NotebookCode notebookCode = NotebookReader.TryRead(notebookJson);
        if (notebookCode == null)
        {
            return FileDetectionResult.Empty();
        }

        FileDetectionResult result = Detect(notebookCode.Text, filePath);
        foreach (DetectedAgent agent in result.Agents)
        {
            agent.Evidence = notebookCode.ToCellEvidence(agent.Evidence);
        }

        return result;
    }

    private FileDetectionResult DetectFrameworkFile(string sourceText, string filePath)
    {
        PythonSource source = new PythonSource(sourceText);
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

        PythonValueReader valueReader = new PythonValueReader(source, Settings.MaxConstructorScanChars);
        result.ModelAssignments = CollectModelAssignments(source, valueReader);

        List<AgentCandidate> candidates = FindAgentCandidates(source, valueReader, importedRules, filePath, result);
        bool fileShowsHumanApproval = CapabilityClassifier.ShowsHumanApproval(sourceText);
        foreach (AgentCandidate candidate in candidates)
        {
            candidate.Agent.HasHumanApproval = fileShowsHumanApproval;
            if (candidate.Constructor.IsGraphBuilder)
            {
                FillGraphBuilder(source, valueReader, candidate, result.ModelAssignments);
            }
        }

        List<DecoratedFunction> decoratedFunctions = FindDecoratedFunctions(source);
        result.DecoratedTools = FindPlainToolFunctions(decoratedFunctions, importedRules);
        AttachAgentBoundTools(candidates, decoratedFunctions);
        AttachRegisteredTools(source, valueReader, candidates, importedRules);
        candidates = MergeWrappedAgents(source, candidates);

        foreach (AgentCandidate candidate in candidates)
        {
            CapabilityClassifier.Apply(candidate.Agent);
            candidate.Agent.Confidence = AgentConfidence.Decide(candidate.Agent, candidate.IsGraphWithoutNodes);
            result.Agents.Add(candidate.Agent);
        }

        return result;
    }

    private List<FrameworkRule> FindImportedRules(PythonSource source)
    {
        List<FrameworkRule> importedRules = new List<FrameworkRule>();
        foreach (FrameworkRule rule in this.rules)
        {
            foreach (PythonImport pythonImport in source.Imports)
            {
                if (IsImportOfRule(pythonImport, rule))
                {
                    importedRules.Add(rule);
                    break;
                }
            }
        }

        return importedRules;
    }

    private static bool IsImportOfRule(PythonImport pythonImport, FrameworkRule rule)
    {
        if (!rule.MatchesModule(pythonImport.Module))
        {
            return false;
        }

        if (rule.RequiredImportedNames.Count == 0)
        {
            return true;
        }

        if (!pythonImport.IsFromImport)
        {
            return false;
        }

        foreach (string importedName in pythonImport.ImportedNames)
        {
            if (rule.RequiredImportedNames.Contains(importedName))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- constructor anchors

    private List<AgentCandidate> FindAgentCandidates(
        PythonSource source,
        PythonValueReader valueReader,
        List<FrameworkRule> importedRules,
        string filePath,
        FileDetectionResult result)
    {
        // Which imported rules offer each constructor name. "Agent" may map to several frameworks.
        Dictionary<string, List<RuleConstructor>> constructorsByCallName = new Dictionary<string, List<RuleConstructor>>(StringComparer.Ordinal);
        foreach (FrameworkRule rule in importedRules)
        {
            foreach (AgentConstructor constructor in rule.Constructors)
            {
                if (!constructorsByCallName.TryGetValue(constructor.CallName, out List<RuleConstructor> options))
                {
                    options = new List<RuleConstructor>();
                    constructorsByCallName.Add(constructor.CallName, options);
                }

                options.Add(new RuleConstructor(rule, constructor));
            }
        }

        int maxAttempts = Settings.MaxConstructorAttemptsPerFile;
        List<CallSite> anchors = new List<CallSite>();
        foreach (string callName in constructorsByCallName.Keys)
        {
            anchors.AddRange(FindCallSites(source, callName, maxAttempts + 1));
        }

        anchors.Sort(CompareByNameStart);
        if (anchors.Count > maxAttempts)
        {
            result.DetectionCapHit = true;
            anchors = anchors.GetRange(0, maxAttempts);
        }

        List<AgentCandidate> candidates = new List<AgentCandidate>();
        foreach (CallSite anchor in anchors)
        {
            RuleConstructor chosen = ChooseRule(source, constructorsByCallName[anchor.CallName], anchor);
            if (chosen == null)
            {
                continue;
            }

            AgentCandidate candidate = BuildCandidate(source, valueReader, chosen, anchor, filePath);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Decides which framework a constructor belongs to when several imported frameworks share the
    /// name (Section 7.2, "Ambiguity"). Returns null when the call is not a framework constructor.
    /// </summary>
    private static RuleConstructor ChooseRule(PythonSource source, List<RuleConstructor> options, CallSite anchor)
    {
        // crewai.Agent(...) or langgraph.prebuilt.create_react_agent(...)
        if (anchor.Qualifier.Length > 0)
        {
            foreach (RuleConstructor option in options)
            {
                if (option.Rule.MatchesModule(anchor.Qualifier))
                {
                    return option;
                }
            }

            // obj.Agent(...) or self.create_agent(...): a method of something else.
            return null;
        }

        string importedName = anchor.CallName.Split('.')[0];
        bool importedFromElsewhere = false;
        foreach (PythonImport pythonImport in source.Imports)
        {
            if (!pythonImport.ImportedNames.Contains(importedName))
            {
                continue;
            }

            foreach (RuleConstructor option in options)
            {
                if (option.Rule.MatchesModule(pythonImport.Module))
                {
                    return option;
                }
            }

            importedFromElsewhere = true;
        }

        // "from myapp.models import Agent" next to an unrelated framework import: not a framework agent.
        if (importedFromElsewhere)
        {
            return null;
        }

        return options[0];
    }

    private AgentCandidate BuildCandidate(
        PythonSource source,
        PythonValueReader valueReader,
        RuleConstructor chosen,
        CallSite anchor,
        string filePath)
    {
        int closeParen = source.FindClosingBracket(anchor.OpenParen, Settings.MaxConstructorScanChars);
        if (closeParen < 0)
        {
            return null;
        }

        FrameworkRule rule = chosen.Rule;
        AgentConstructor constructor = chosen.Constructor;
        List<CallArgument> arguments = CallArguments.Split(source, anchor.OpenParen, closeParen);
        Dictionary<ArgumentRole, CallArgument> positionalArguments = MapPositionalArguments(constructor, arguments);
        int line = source.LineNumberAt(anchor.NameStart);

        DetectedAgent agent = new DetectedAgent
        {
            FrameworkKey = rule.Key,
            AnchorOffset = anchor.NameStart,
            ConstructorEndOffset = closeParen + 1,
            VariableName = FindAssignedVariable(source, anchor),
            Evidence = new Evidence(filePath, line, TextCleanup.MakeSnippet(source.LineTextAt(anchor.NameStart))),
        };

        AgentCandidate candidate = new AgentCandidate(agent, rule, constructor, arguments);
        NameAgent(source, valueReader, candidate, positionalArguments, anchor, filePath, line);
        agent.Description = ReadDescription(valueReader, rule, arguments, ListHelpers.ValueOrDefault(positionalArguments, ArgumentRole.Description));
        ReadModel(valueReader, rule, arguments, ListHelpers.ValueOrDefault(positionalArguments, ArgumentRole.Model), agent);
        ReadTools(valueReader, rule, arguments, ListHelpers.ValueOrDefault(positionalArguments, ArgumentRole.Tools), agent);
        AddCapabilitiesFromArguments(source, rule, constructor, arguments, agent);
        return candidate;
    }

    private static Dictionary<ArgumentRole, CallArgument> MapPositionalArguments(AgentConstructor constructor, List<CallArgument> arguments)
    {
        Dictionary<ArgumentRole, CallArgument> positionalArguments = new Dictionary<ArgumentRole, CallArgument>();
        for (int position = 0; position < arguments.Count && position < constructor.PositionalArgs.Count; position++)
        {
            CallArgument argument = arguments[position];
            if (argument.IsKeyword)
            {
                break;
            }

            ArgumentRole role = constructor.PositionalArgs[position];
            if (role != ArgumentRole.Ignored)
            {
                positionalArguments[role] = argument;
            }
        }

        return positionalArguments;
    }

    // ---------------------------------------------------------------- profile fields

    /// <summary>Name priority (Section 7.1 step 3): name/role argument, variable, enclosing function, fallback.</summary>
    private static void NameAgent(
        PythonSource source,
        PythonValueReader valueReader,
        AgentCandidate candidate,
        Dictionary<ArgumentRole, CallArgument> positionalArguments,
        CallSite anchor,
        string filePath,
        int line)
    {
        DetectedAgent agent = candidate.Agent;
        string explicitName = ReadFirstString(valueReader, candidate.Rule.NameArgs, candidate.Arguments, ListHelpers.ValueOrDefault(positionalArguments, ArgumentRole.Name));
        explicitName = SecretFilter.DropIfSecret(explicitName);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            agent.Name = TextCleanup.CollapseWhitespace(explicitName, 200);
            candidate.NameSource = NameSource.ExplicitArgument;
            return;
        }

        if (agent.VariableName != null)
        {
            agent.Name = agent.VariableName;
            candidate.NameSource = NameSource.Variable;
            return;
        }

        // CrewAI scaffolds: "@agent def researcher(self): return Agent(...)"
        string enclosingFunction = FindEnclosingFunctionOfReturn(source, anchor);
        if (enclosingFunction != null)
        {
            agent.Name = enclosingFunction;
            candidate.NameSource = NameSource.EnclosingFunction;
            return;
        }

        agent.Name = $"{candidate.Rule.Key}@{Path.GetFileName(filePath)}:{line}";
        candidate.NameSource = NameSource.Fallback;
    }

    private static string ReadFirstString(
        PythonValueReader valueReader,
        IReadOnlyList<string> keywordNames,
        List<CallArgument> arguments,
        CallArgument positionalArgument)
    {
        foreach (string keywordName in keywordNames)
        {
            CallArgument argument = PythonValueReader.FindKeyword(arguments, keywordName);
            if (argument != null && valueReader.TryReadString(argument, out string value))
            {
                return value;
            }
        }

        if (positionalArgument != null && valueReader.TryReadString(positionalArgument, out string positionalValue))
        {
            return positionalValue;
        }

        return null;
    }

    /// <summary>
    /// Description (Section 7.1 step 6): the first literal description argument, or all of them joined
    /// when the rule says so. "&lt;dynamic&gt;" when only non-literal values exist.
    /// </summary>
    private string ReadDescription(
        PythonValueReader valueReader,
        FrameworkRule rule,
        List<CallArgument> arguments,
        CallArgument positionalArgument)
    {
        List<CallArgument> descriptionArguments = new List<CallArgument>();
        foreach (string keywordName in rule.DescriptionArgs)
        {
            CallArgument argument = PythonValueReader.FindKeyword(arguments, keywordName);
            if (argument != null)
            {
                descriptionArguments.Add(argument);
            }
        }

        if (positionalArgument != null)
        {
            descriptionArguments.Add(positionalArgument);
        }

        List<string> literalParts = new List<string>();
        bool sawDynamicValue = false;
        foreach (CallArgument argument in descriptionArguments)
        {
            bool isLiteral = valueReader.TryReadStringFollowingVariables(argument.ValueStart, argument.ValueEnd, out string text);
            if (!isLiteral || string.IsNullOrWhiteSpace(text))
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

    private static void ReadModel(
        PythonValueReader valueReader,
        FrameworkRule rule,
        List<CallArgument> arguments,
        CallArgument positionalArgument,
        DetectedAgent agent)
    {
        List<CallArgument> modelArguments = new List<CallArgument>();
        foreach (string keywordName in rule.ModelArgs)
        {
            CallArgument argument = PythonValueReader.FindKeyword(arguments, keywordName);
            if (argument != null)
            {
                modelArguments.Add(argument);
            }
        }

        if (positionalArgument != null)
        {
            modelArguments.Add(positionalArgument);
        }

        foreach (CallArgument argument in modelArguments)
        {
            ModelReference model = valueReader.ReadModel(argument.ValueStart, argument.ValueEnd, out string unresolvedVariable);
            if (model != null)
            {
                AddDistinct(agent.Models, model);
                agent.UnresolvedModelVariable = null;
                return;
            }

            if (agent.UnresolvedModelVariable == null)
            {
                agent.UnresolvedModelVariable = unresolvedVariable;
            }
        }
    }

    private static void ReadTools(
        PythonValueReader valueReader,
        FrameworkRule rule,
        List<CallArgument> arguments,
        CallArgument positionalArgument,
        DetectedAgent agent)
    {
        foreach (string keywordName in rule.ToolArgs)
        {
            CallArgument argument = PythonValueReader.FindKeyword(arguments, keywordName);
            if (argument != null)
            {
                AddDistinct(agent.Tools, valueReader.ReadToolNames(argument.ValueStart, argument.ValueEnd));
            }
        }

        if (positionalArgument != null)
        {
            AddDistinct(agent.Tools, valueReader.ReadToolNames(positionalArgument.ValueStart, positionalArgument.ValueEnd));
        }
    }

    private static void AddCapabilitiesFromArguments(
        PythonSource source,
        FrameworkRule rule,
        AgentConstructor constructor,
        List<CallArgument> arguments,
        DetectedAgent agent)
    {
        foreach (string capability in constructor.ImpliedCapabilities)
        {
            AddDistinct(agent.Capabilities, capability);
        }

        foreach (KeyValuePair<string, string> capabilityArg in rule.CapabilityArgs)
        {
            string argumentName = capabilityArg.Key;
            string capability = capabilityArg.Value;
            CallArgument argument = PythonValueReader.FindKeyword(arguments, argumentName);
            if (argument != null && !PythonLiterals.IsFalsy(argument.ValueText(source)))
            {
                AddDistinct(agent.Capabilities, capability);
            }
        }
    }

    /// <summary>
    /// Graph builders (<c>StateGraph(State)</c>) take no model or tools. Fill them from the file:
    /// tool nodes, <c>.bind_tools([...])</c> calls and model variables.
    /// </summary>
    private void FillGraphBuilder(
        PythonSource source,
        PythonValueReader valueReader,
        AgentCandidate candidate,
        Dictionary<string, ModelReference> modelAssignments)
    {
        DetectedAgent agent = candidate.Agent;
        foreach (string callName in candidate.Rule.GraphToolCalls)
        {
            foreach (CallSite call in FindCallSites(source, callName, MaxHelperCallsPerFile))
            {
                List<CallArgument> arguments = SplitCallArguments(source, call);
                if (arguments == null || arguments.Count == 0 || arguments[0].IsKeyword)
                {
                    continue;
                }

                AddDistinct(agent.Tools, valueReader.ReadToolNames(arguments[0].ValueStart, arguments[0].ValueEnd));
            }
        }

        foreach (ModelReference model in modelAssignments.Values)
        {
            AddDistinct(agent.Models, model);
        }

        bool hasNodes = source.Text.Contains("." + candidate.Rule.GraphNodeCall + "(", StringComparison.Ordinal);
        candidate.IsGraphWithoutNodes = !hasNodes;
    }

    // ---------------------------------------------------------------- tools defined outside the constructor

    private static List<DecoratedFunction> FindDecoratedFunctions(PythonSource source)
    {
        List<DecoratedFunction> decoratedFunctions = new List<DecoratedFunction>();
        List<string> pendingDecorators = new List<string>();
        string text = source.Text;

        foreach (int statementStart in source.StatementStarts())
        {
            if (text[statementStart] == '@')
            {
                pendingDecorators.Add(ReadDottedName(text, statementStart + 1));
                continue;
            }

            string functionName = ReadDefinedFunctionName(text, statementStart);
            if (functionName != null)
            {
                foreach (string decorator in pendingDecorators)
                {
                    decoratedFunctions.Add(new DecoratedFunction(decorator, functionName));
                }
            }

            pendingDecorators.Clear();
        }

        return decoratedFunctions;
    }

    private static List<string> FindPlainToolFunctions(List<DecoratedFunction> decoratedFunctions, List<FrameworkRule> importedRules)
    {
        List<string> toolFunctions = new List<string>();
        foreach (DecoratedFunction decoratedFunction in decoratedFunctions)
        {
            foreach (FrameworkRule rule in importedRules)
            {
                if (rule.ToolDecorators.Contains(decoratedFunction.Decorator))
                {
                    AddDistinct(toolFunctions, decoratedFunction.FunctionName);
                    break;
                }
            }
        }

        return toolFunctions;
    }

    /// <summary>Pydantic AI's <c>@support_agent.tool</c> and AutoGen's <c>@assistant.register_for_llm()</c>.</summary>
    private static void AttachAgentBoundTools(List<AgentCandidate> candidates, List<DecoratedFunction> decoratedFunctions)
    {
        foreach (DecoratedFunction decoratedFunction in decoratedFunctions)
        {
            int lastDot = decoratedFunction.Decorator.LastIndexOf('.');
            if (lastDot <= 0)
            {
                continue;
            }

            string agentVariable = decoratedFunction.Decorator.Substring(0, lastDot);
            string decoratorMethod = decoratedFunction.Decorator.Substring(lastDot + 1);
            foreach (AgentCandidate candidate in candidates)
            {
                bool isBoundToThisAgent = candidate.Agent.VariableName == agentVariable
                    && candidate.Rule.AgentBoundToolDecorators.Contains(decoratorMethod);
                if (isBoundToThisAgent)
                {
                    AddDistinct(candidate.Agent.Tools, decoratedFunction.FunctionName);
                }
            }
        }
    }

    /// <summary>AutoGen 0.2: <c>register_function(get_weather, caller=assistant, executor=user_proxy)</c>.</summary>
    private void AttachRegisteredTools(
        PythonSource source,
        PythonValueReader valueReader,
        List<AgentCandidate> candidates,
        List<FrameworkRule> importedRules)
    {
        foreach (FrameworkRule rule in importedRules)
        {
            foreach (ToolRegistrationCall registration in rule.ToolRegistrationCalls)
            {
                foreach (CallSite call in FindCallSites(source, registration.CallName, MaxHelperCallsPerFile))
                {
                    List<CallArgument> arguments = SplitCallArguments(source, call);
                    if (arguments == null || registration.ToolPosition >= arguments.Count)
                    {
                        continue;
                    }

                    CallArgument toolArgument = arguments[registration.ToolPosition];
                    CallArgument agentArgument = PythonValueReader.FindKeyword(arguments, registration.AgentArgument);
                    if (toolArgument.IsKeyword || agentArgument == null)
                    {
                        continue;
                    }

                    string agentVariable = PythonLiterals.LastSegment(agentArgument.ValueText(source).Trim());
                    string toolText = toolArgument.ValueText(source).Trim();

                    // The registered tool is usually a plain function name, not a list of tools.
                    List<string> toolNames;
                    if (PythonLiterals.IsIdentifier(toolText))
                    {
                        toolNames = new List<string> { PythonLiterals.LastSegment(toolText) };
                    }
                    else
                    {
                        toolNames = valueReader.ReadToolNames(toolArgument.ValueStart, toolArgument.ValueEnd);
                    }
                    foreach (AgentCandidate candidate in candidates)
                    {
                        if (candidate.Rule == rule && candidate.Agent.VariableName == agentVariable)
                        {
                            AddDistinct(candidate.Agent.Tools, toolNames);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>AgentExecutor(agent=create_tool_calling_agent(...))</c> or
    /// <c>ClaudeSDKClient(options=options)</c> describe one agent, not two. Merge the inner one into the wrapper.
    /// </summary>
    private static List<AgentCandidate> MergeWrappedAgents(PythonSource source, List<AgentCandidate> candidates)
    {
        HashSet<AgentCandidate> mergedAway = new HashSet<AgentCandidate>();
        foreach (AgentCandidate wrapper in candidates)
        {
            foreach (string wrappedArgumentName in wrapper.Rule.WrappedAgentArgs)
            {
                CallArgument wrappedArgument = PythonValueReader.FindKeyword(wrapper.Arguments, wrappedArgumentName);
                if (wrappedArgument == null)
                {
                    continue;
                }

                AgentCandidate wrapped = FindWrappedCandidate(source, candidates, wrapper, wrappedArgument);
                if (wrapped == null || mergedAway.Contains(wrapped))
                {
                    continue;
                }

                MergeInto(wrapper, wrapped);
                mergedAway.Add(wrapped);
            }
        }

        List<AgentCandidate> remaining = new List<AgentCandidate>();
        foreach (AgentCandidate candidate in candidates)
        {
            if (!mergedAway.Contains(candidate))
            {
                remaining.Add(candidate);
            }
        }

        return remaining;
    }

    private static AgentCandidate FindWrappedCandidate(
        PythonSource source,
        List<AgentCandidate> candidates,
        AgentCandidate wrapper,
        CallArgument wrappedArgument)
    {
        string valueText = wrappedArgument.ValueText(source).Trim();
        bool isVariable = PythonLiterals.IsIdentifier(valueText);
        string variableName = "";
        if (isVariable)
        {
            variableName = PythonLiterals.LastSegment(valueText);
        }

        foreach (AgentCandidate candidate in candidates)
        {
            if (candidate == wrapper || candidate.Rule != wrapper.Rule)
            {
                continue;
            }

            if (isVariable && candidate.Agent.VariableName == variableName)
            {
                return candidate;
            }

            bool isInline = candidate.Agent.AnchorOffset >= wrappedArgument.ValueStart
                && candidate.Agent.AnchorOffset < wrappedArgument.ValueEnd;
            if (isInline)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void MergeInto(AgentCandidate wrapper, AgentCandidate wrapped)
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

        AddDistinct(target.Models, source.Models);
        AddDistinct(target.Tools, source.Tools);
        AddDistinct(target.Capabilities, source.Capabilities);
        if (target.UnresolvedModelVariable == null)
        {
            target.UnresolvedModelVariable = source.UnresolvedModelVariable;
        }
        if (target.Models.Count > 0)
        {
            target.UnresolvedModelVariable = null;
        }
    }

    // ---------------------------------------------------------------- model variables for cross-file linking

    private static Dictionary<string, ModelReference> CollectModelAssignments(PythonSource source, PythonValueReader valueReader)
    {
        Dictionary<string, ModelReference> modelAssignments = new Dictionary<string, ModelReference>(StringComparer.Ordinal);
        foreach (string assignedName in source.AssignedNames())
        {
            if (modelAssignments.Count >= MaxModelAssignmentsPerFile)
            {
                break;
            }

            IReadOnlyList<int> valueOffsets = source.FindAssignmentValueOffsets(assignedName);
            int lastValueStart = valueOffsets[valueOffsets.Count - 1];

            // Cheap checks first: only calls to model-like classes, or string constants named like a model.
            string calleeName = valueReader.PeekCalleeName(lastValueStart, source.Length);
            bool isModelFactoryCall = calleeName.Length > 0 && ModelProviders.LooksLikeModelFactory(calleeName);
            bool isNamedLikeModel = calleeName.Length == 0 && NameSuggestsModel(assignedName);
            if (!isModelFactoryCall && !isNamedLikeModel)
            {
                continue;
            }

            SourceSpan assignedValue = valueReader.FindAssignedValue(assignedName, int.MaxValue);
            if (assignedValue == null)
            {
                continue;
            }

            ModelReference model = valueReader.ReadModel(assignedValue.Start, assignedValue.End, out string unresolvedVariable);
            if (model != null)
            {
                modelAssignments[assignedName] = model;
            }
        }

        return modelAssignments;
    }

    private static bool NameSuggestsModel(string variableName)
    {
        string lowerName = variableName.ToLowerInvariant();
        return lowerName.Contains("model", StringComparison.Ordinal) || lowerName.Contains("llm", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- small text helpers

    /// <summary>Finds code occurrences of <c>callName(</c> at a word boundary, with any qualifier before them.</summary>
    private static List<CallSite> FindCallSites(PythonSource source, string callName, int maxResults)
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

            bool startsAtWordBoundary = nameStart == 0 || !PythonSource.IsIdentifierChar(text[nameStart - 1], isFirst: false);
            int nameEnd = nameStart + callName.Length;
            bool endsAtWordBoundary = nameEnd >= text.Length || !PythonSource.IsIdentifierChar(text[nameEnd], isFirst: false);
            if (!startsAtWordBoundary || !endsAtWordBoundary)
            {
                continue;
            }

            int openParen = nameEnd;
            while (openParen < text.Length && (text[openParen] == ' ' || text[openParen] == '\t'))
            {
                openParen++;
            }

            if (openParen >= text.Length || text[openParen] != '(' || IsDefinition(source, nameStart))
            {
                continue;
            }

            callSites.Add(new CallSite(callName, nameStart, openParen, ReadQualifier(text, nameStart)));
        }

        return callSites;
    }

    private List<CallArgument> SplitCallArguments(PythonSource source, CallSite call)
    {
        int closeParen = source.FindClosingBracket(call.OpenParen, Settings.MaxConstructorScanChars);
        if (closeParen < 0)
        {
            return null;
        }

        return CallArguments.Split(source, call.OpenParen, closeParen);
    }

    /// <summary>"crewai.Agent" gives "crewai"; a plain "Agent" gives "".</summary>
    private static string ReadQualifier(string text, int nameStart)
    {
        if (nameStart == 0 || text[nameStart - 1] != '.')
        {
            return "";
        }

        int qualifierEnd = nameStart - 1;
        int qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && (PythonSource.IsIdentifierChar(text[qualifierStart - 1], isFirst: false) || text[qualifierStart - 1] == '.'))
        {
            qualifierStart--;
        }

        return text.Substring(qualifierStart, qualifierEnd - qualifierStart);
    }

    /// <summary>True for "def Agent(" and "class Agent(": a definition, not a call.</summary>
    private static bool IsDefinition(PythonSource source, int nameStart)
    {
        int lineStart = source.LineStartAt(nameStart);
        string linePrefix = source.Text.Substring(lineStart, nameStart - lineStart).Trim();
        return linePrefix == "def" || linePrefix == "class" || linePrefix == "async def";
    }

    /// <summary>"support_agent = Agent(" gives "support_agent"; "self.agent: Agent = Agent(" gives "agent".</summary>
    private static string FindAssignedVariable(PythonSource source, CallSite anchor)
    {
        int qualifiedStart = anchor.NameStart - anchor.Qualifier.Length;
        if (anchor.Qualifier.Length > 0)
        {
            qualifiedStart = qualifiedStart - 1; // the dot between qualifier and name
        }
        int lineStart = source.LineStartAt(qualifiedStart);
        string linePrefix = source.Text.Substring(lineStart, qualifiedStart - lineStart).TrimEnd();

        if (linePrefix.EndsWith("await", StringComparison.Ordinal))
        {
            linePrefix = linePrefix.Substring(0, linePrefix.Length - "await".Length).TrimEnd();
        }

        bool endsWithAssignment = linePrefix.EndsWith("=", StringComparison.Ordinal)
            && !linePrefix.EndsWith("==", StringComparison.Ordinal)
            && !linePrefix.EndsWith("!=", StringComparison.Ordinal)
            && !linePrefix.EndsWith("<=", StringComparison.Ordinal)
            && !linePrefix.EndsWith(">=", StringComparison.Ordinal);
        if (!endsWithAssignment)
        {
            return null;
        }

        string target = linePrefix.Substring(0, linePrefix.Length - 1).Trim();
        int annotationStart = target.IndexOf(':');
        if (annotationStart >= 0)
        {
            target = target.Substring(0, annotationStart).Trim();
        }

        if (PythonLiterals.IsIdentifier(target))
        {
            return PythonLiterals.LastSegment(target);
        }

        return null;
    }

    /// <summary>For "return Agent(...)" inside "def researcher(self):", gives "researcher".</summary>
    private static string FindEnclosingFunctionOfReturn(PythonSource source, CallSite anchor)
    {
        int lineStart = source.LineStartAt(anchor.NameStart);
        string linePrefix = source.Text.Substring(lineStart, anchor.NameStart - lineStart).Trim();
        if (linePrefix != "return")
        {
            return null;
        }

        string text = source.Text;
        int searchLimit = Math.Max(0, anchor.NameStart - MaxEnclosingFunctionSearchChars);
        int searchFrom = lineStart;
        while (searchFrom > searchLimit)
        {
            int defIndex = text.LastIndexOf("def ", searchFrom - 1, searchFrom - searchLimit, StringComparison.Ordinal);
            if (defIndex < 0)
            {
                return null;
            }

            int defLineStart = source.LineStartAt(defIndex);
            string beforeDef = text.Substring(defLineStart, defIndex - defLineStart).Trim();
            bool isFunctionDefinition = source.IsCode(defIndex) && (beforeDef.Length == 0 || beforeDef == "async");
            if (isFunctionDefinition)
            {
                return ReadDefinedFunctionName(text, defIndex);
            }

            searchFrom = defIndex;
        }

        return null;
    }

    private static string ReadDefinedFunctionName(string text, int statementStart)
    {
        int position = statementStart;
        if (string.CompareOrdinal(text, position, "async ", 0, 6) == 0)
        {
            position += 6;
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
            {
                position++;
            }
        }

        if (string.CompareOrdinal(text, position, "def ", 0, 4) != 0)
        {
            return null;
        }

        position += 4;
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
        {
            position++;
        }

        string functionName = ReadDottedName(text, position);
        if (functionName.Length == 0)
        {
            return null;
        }

        return functionName;
    }

    private static string ReadDottedName(string text, int start)
    {
        int position = start;
        while (position < text.Length && (PythonSource.IsIdentifierChar(text[position], position == start) || text[position] == '.'))
        {
            position++;
        }

        return text.Substring(start, position - start);
    }

    private static void AddDistinct<T>(List<T> target, T item)
    {
        if (!target.Contains(item))
        {
            target.Add(item);
        }
    }

    private static void AddDistinct<T>(List<T> target, IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            AddDistinct(target, item);
        }
    }

    // ---------------------------------------------------------------- private types

    private sealed class RuleConstructor

    {

        public RuleConstructor(FrameworkRule rule, AgentConstructor constructor)

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

    private sealed class DecoratedFunction

    {

        public DecoratedFunction(string decorator, string functionName)

        {

            Decorator = decorator;

            FunctionName = functionName;

        }


        public string Decorator { get; }

        public string FunctionName { get; }

    }

    /// <summary>Lower values are better names.</summary>
    private enum NameSource
    {
        ExplicitArgument,
        Variable,
        EnclosingFunction,
        Fallback,
    }

    /// <summary>An agent being built, plus the facts about it that are only needed during detection.</summary>
    private sealed class AgentCandidate
    {
        public AgentCandidate(DetectedAgent agent, FrameworkRule rule, AgentConstructor constructor, List<CallArgument> arguments)
        {
            Agent = agent;
            Rule = rule;
            Constructor = constructor;
            Arguments = arguments;
        }

        public DetectedAgent Agent { get; }
        public FrameworkRule Rule { get; }
        public AgentConstructor Constructor { get; }
        public List<CallArgument> Arguments { get; }
        public NameSource NameSource { get; set; } = NameSource.Fallback;
        public bool IsGraphWithoutNodes { get; set; }
    }
}
