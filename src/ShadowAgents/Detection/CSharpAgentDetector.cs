using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace ShadowAgents;

/// <summary>
/// Finds .NET agents with Roslyn (Section 7.2, "C# (.NET) with Roslyn"): Semantic Kernel, Microsoft Agent
/// Framework, AutoGen.NET and Microsoft.Extensions.AI.
/// </summary>
/// <remarks>
/// Only the syntax tree is used: no compilation and no NuGet restore. So we see names as written
/// ("new ChatCompletionAgent"), not resolved types. That is enough for agent definitions and keeps it fast.
/// </remarks>
public class CSharpAgentDetector
{
    private const string SemanticKernel = "semantic_kernel_dotnet";
    private const string AgentFramework = "microsoft_agent_framework";
    private const string AutoGen = "autogen_dotnet";
    private const string ExtensionsAI = "microsoft_extensions_ai";

    // Namespace prefix in "using ..." lines → framework key. Order matters: the first match wins.
    private static readonly string[][] NamespaceToFramework =
    {
        new[] { "Microsoft.SemanticKernel", SemanticKernel },
        new[] { "Microsoft.Agents.AI", AgentFramework },
        new[] { "AutoGen", AutoGen },
        new[] { "Microsoft.Extensions.AI", ExtensionsAI },
    };

    private static readonly HashSet<string> SemanticKernelAgentTypes = new HashSet<string>
    {
        "ChatCompletionAgent", "OpenAIAssistantAgent", "AzureAIAgent", "OpenAIResponseAgent", "CopilotStudioAgent",
    };

    private static readonly HashSet<string> AutoGenAgentTypes = new HashSet<string>
    {
        "AssistantAgent", "ConversableAgent", "OpenAIChatAgent", "SemanticKernelAgent", "AnthropicClientAgent",
        "GeminiChatAgent", "MistralClientAgent", "OllamaAgent",
    };

    private static readonly HashSet<string> AgentFrameworkFactoryMethods = new HashSet<string>
    {
        "CreateAIAgent", "AsAIAgent",
    };

    // Argument names that hold a model or deployment name.
    private static readonly string[] ModelArgumentNames =
    {
        "modelId", "deploymentName", "model", "modelName", "modelOrDeploymentName", "defaultModel", "deployment",
    };

    // Client types, checked in order, that tell us the provider when the model name alone does not.
    private static readonly string[][] ClientTypeToProvider =
    {
        new[] { "AzureOpenAIClient", "azure_openai" },
        new[] { "AzureAIInference", "azure_openai" },
        new[] { "OpenAIClient", "openai" },
        new[] { "AnthropicClient", "anthropic" },
        new[] { "OllamaApiClient", "ollama" },
        new[] { "OllamaChatClient", "ollama" },
        new[] { "Gemini", "google" },
        new[] { "Bedrock", "bedrock" },
    };


    public CSharpAgentDetector()
    {
    }

    public FileDetectionResult Detect(string sourceText, string filePath)
    {
        if (!MentionsAnyFramework(sourceText))
        {
            return FileDetectionResult.Empty();
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        FileDetectionResult result = new FileDetectionResult();
        result.ReferencedFrameworks = FindReferencedFrameworks(root, sourceText);
        if (result.ReferencedFrameworks.Count == 0)
        {
            return result;
        }

        FileFacts facts = CollectFileFacts(root, sourceText);
        FindSemanticKernelAgents(root, facts, filePath, result);
        FindAgentFrameworkAgents(root, facts, filePath, result);
        FindAutoGenAgents(root, facts, filePath, result);
        FindExtensionsAIAgents(root, facts, filePath, result);

        foreach (DetectedAgent agent in result.Agents)
        {
            agent.HasHumanApproval = CapabilityClassifier.ShowsHumanApproval(sourceText);
            CapabilityClassifier.Apply(agent);
            agent.Confidence = AgentConfidence.Decide(agent, isGraphWithoutNodes: false);
        }

        return result;
    }

    private static bool MentionsAnyFramework(string sourceText)
    {
        return sourceText.Contains("SemanticKernel", StringComparison.Ordinal)
            || sourceText.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)
            || sourceText.Contains("AutoGen", StringComparison.Ordinal)
            || sourceText.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal);
    }

    private static List<string> FindReferencedFrameworks(CompilationUnitSyntax root, string sourceText)
    {
        List<string> frameworks = new List<string>();
        foreach (UsingDirectiveSyntax usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            string namespaceName = usingDirective.NamespaceOrType.ToString();
            string framework = FrameworkForNamespace(namespaceName);
            if (framework != null)
            {
                ListHelpers.AddDistinct(frameworks, framework);
            }
        }

        // "global using" lines often live in another file, so also accept fully qualified names in code.
        foreach (string[] mapping in NamespaceToFramework)
        {
            if (sourceText.Contains(mapping[0] + ".", StringComparison.Ordinal))
            {
                ListHelpers.AddDistinct(frameworks, mapping[1]);
            }
        }

        return frameworks;
    }

    private static string FrameworkForNamespace(string namespaceName)
    {
        foreach (string[] mapping in NamespaceToFramework)
        {
            if (namespaceName == mapping[0] || namespaceName.StartsWith(mapping[0] + ".", StringComparison.Ordinal))
            {
                return mapping[1];
            }
        }

        return null;
    }

    // ------------------------------------------------------------ facts shared by all agents in a file

    /// <summary>Things defined once per file that agents in the file use: models, tool methods, plugins, constants.</summary>
    private sealed class FileFacts
    {
        public List<ModelReference> Models { get; } = new List<ModelReference>();
        public List<string> KernelFunctions { get; } = new List<string>();
        public List<string> PluginTypes { get; } = new List<string>();
        public List<string> AutoGenFunctions { get; } = new List<string>();
        public Dictionary<string, string> StringConstants { get; } = new Dictionary<string, string>();
        public string FileProvider { get; set; }
    }

    private FileFacts CollectFileFacts(CompilationUnitSyntax root, string sourceText)
    {
        FileFacts facts = new FileFacts();
        facts.FileProvider = FindFileProvider(sourceText);

        // const string Model = "gpt-4o";  or  var model = "gpt-4o";
        foreach (VariableDeclaratorSyntax declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer != null && declarator.Initializer.Value is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                facts.StringConstants[declarator.Identifier.ValueText] = literal.Token.ValueText;
            }
        }

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (HasAttribute(method, "KernelFunction"))
            {
                facts.KernelFunctions.Add(method.Identifier.ValueText);
            }

            if (HasAttribute(method, "Function"))
            {
                facts.AutoGenFunctions.Add(method.Identifier.ValueText);
            }
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string methodName = MethodName(invocation);
            ReadPluginType(invocation, methodName, facts);
            ModelReference model = ReadModelFromCall(methodName, invocation.ArgumentList, facts);
            if (model != null)
            {
                ListHelpers.AddDistinct(facts.Models, model);
            }
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            string typeName = CreatedTypeName(creation);
            bool createsModelClient = typeName.EndsWith("ChatClient", StringComparison.Ordinal)
                || typeName.EndsWith("ApiClient", StringComparison.Ordinal)
                || typeName == "OpenAIConfig" || typeName == "AzureOpenAIConfig";
            if (!createsModelClient)
            {
                continue;
            }

            ModelReference model = ReadModelFromCall(typeName, creation.ArgumentList, facts);
            if (model != null)
            {
                ListHelpers.AddDistinct(facts.Models, model);
            }
        }

        return facts;
    }

    /// <summary>
    /// Model from calls such as builder.AddOpenAIChatCompletion("gpt-4o", key),
    /// client.GetChatClient("gpt-4o-mini") or new OllamaChatClient(uri, "llama3.1").
    /// </summary>
    private ModelReference ReadModelFromCall(string name, ArgumentListSyntax arguments, FileFacts facts)
    {
        bool isModelCall = (name.StartsWith("Add", StringComparison.Ordinal) && name.Contains("ChatCompletion", StringComparison.Ordinal))
            || name == "GetChatClient" || name == "AsChatClient" || name == "AsIChatClient" || name == "GetResponsesClient"
            || name.EndsWith("ChatClient", StringComparison.Ordinal) || name.EndsWith("ApiClient", StringComparison.Ordinal)
            || name == "OpenAIConfig" || name == "AzureOpenAIConfig";
        if (!isModelCall || arguments == null)
        {
            return null;
        }

        string modelId = null;
        foreach (string argumentName in ModelArgumentNames)
        {
            ExpressionSyntax namedValue = FindNamedArgument(arguments, argumentName);
            if (namedValue != null)
            {
                modelId = ReadString(namedValue, facts);
                if (modelId != null)
                {
                    break;
                }
            }
        }

        if (modelId == null)
        {
            modelId = FirstNonUrlStringArgument(arguments, facts);
        }

        if (modelId == null)
        {
            return null;
        }

        string provider = ModelProviders.FromClassName(name);
        if (provider == null)
        {
            provider = facts.FileProvider;
        }

        return ModelProviders.FromModelString(modelId, provider);
    }

    private static void ReadPluginType(InvocationExpressionSyntax invocation, string methodName, FileFacts facts)
    {
        // kernel.Plugins.AddFromType<MenuPlugin>() / ImportPluginFromType<T>() / CreatePluginFromType<T>()
        bool isPluginFromType = methodName == "AddFromType" || methodName == "ImportPluginFromType" || methodName == "CreatePluginFromType";
        if (isPluginFromType && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name is GenericNameSyntax genericName && genericName.TypeArgumentList.Arguments.Count > 0)
        {
            ListHelpers.AddDistinct(facts.PluginTypes, genericName.TypeArgumentList.Arguments[0].ToString());
            return;
        }

        // kernel.Plugins.AddFromObject(new MenuPlugin())
        bool isPluginFromObject = methodName == "AddFromObject" || methodName == "ImportPluginFromObject";
        if (isPluginFromObject && invocation.ArgumentList.Arguments.Count > 0
            && invocation.ArgumentList.Arguments[0].Expression is BaseObjectCreationExpressionSyntax creation)
        {
            ListHelpers.AddDistinct(facts.PluginTypes, CreatedTypeName(creation));
        }
    }

    private static string FindFileProvider(string sourceText)
    {
        foreach (string[] mapping in ClientTypeToProvider)
        {
            if (sourceText.Contains(mapping[0], StringComparison.Ordinal))
            {
                return mapping[1];
            }
        }

        return null;
    }

    // ------------------------------------------------------------ Semantic Kernel

    /// <summary>
    /// <c>new ChatCompletionAgent { Name = "Host", Instructions = "...", Kernel = kernel }</c>.
    /// Also a bare <c>Kernel.CreateBuilder()</c> with kernel functions or plugins: that kernel calls tools
    /// on its own, so it acts as an agent even without an agent class.
    /// </summary>
    private void FindSemanticKernelAgents(CompilationUnitSyntax root, FileFacts facts, string filePath, FileDetectionResult result)
    {
        bool foundAgentClass = false;
        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            string typeName = CreatedTypeName(creation);
            if (!SemanticKernelAgentTypes.Contains(typeName))
            {
                continue;
            }

            DetectedAgent agent = NewAgent(SemanticKernel, creation, filePath);
            ExpressionSyntax nameValue = FindInitializerValue(creation.Initializer, "Name");
            if (nameValue == null)
            {
                nameValue = FindNamedArgument(creation.ArgumentList, "name");
            }

            string explicitName = ReadString(nameValue, facts);
            SetName(agent, explicitName, creation, typeName);
            agent.Description = ReadDescription(facts,
                FindInitializerValue(creation.Initializer, "Instructions"),
                FindInitializerValue(creation.Initializer, "Description"),
                FindNamedArgument(creation.ArgumentList, "instructions"));
            ListHelpers.AddDistinct(agent.Models, facts.Models);
            ListHelpers.AddDistinct(agent.Tools, facts.KernelFunctions);
            ListHelpers.AddDistinct(agent.Tools, facts.PluginTypes);
            result.Agents.Add(agent);
            foundAgentClass = true;
        }

        if (foundAgentClass || (facts.KernelFunctions.Count == 0 && facts.PluginTypes.Count == 0))
        {
            return;
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression.ToString() != "Kernel.CreateBuilder")
            {
                continue;
            }

            DetectedAgent kernelAgent = NewAgent(SemanticKernel, invocation, filePath);
            SetName(kernelAgent, null, invocation, "kernel");
            ListHelpers.AddDistinct(kernelAgent.Models, facts.Models);
            ListHelpers.AddDistinct(kernelAgent.Tools, facts.KernelFunctions);
            ListHelpers.AddDistinct(kernelAgent.Tools, facts.PluginTypes);
            result.Agents.Add(kernelAgent);
            return; // one kernel agent per file is enough
        }
    }

    // ------------------------------------------------------------ Microsoft Agent Framework

    /// <summary>
    /// <c>chatClient.CreateAIAgent(instructions: "...", name: "...", tools: [...])</c> or
    /// <c>new ChatClientAgent(chatClient, instructions: "...", name: "...", tools: [...])</c>.
    /// </summary>
    private void FindAgentFrameworkAgents(CompilationUnitSyntax root, FileFacts facts, string filePath, FileDetectionResult result)
    {
        if (!result.ReferencedFrameworks.Contains(AgentFramework))
        {
            return;
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string methodName = MethodName(invocation);
            if (!AgentFrameworkFactoryMethods.Contains(methodName))
            {
                continue;
            }

            // Positional form: CreateAIAgent("instructions", "name")
            ExpressionSyntax positionalInstructions = PositionalArgument(invocation.ArgumentList, 0);
            ExpressionSyntax positionalName = PositionalArgument(invocation.ArgumentList, 1);
            AddAgentFrameworkAgent(invocation, invocation.ArgumentList, positionalInstructions, positionalName, facts, filePath, result);
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (CreatedTypeName(creation) != "ChatClientAgent")
            {
                continue;
            }

            // Positional form: new ChatClientAgent(chatClient, "instructions", "name")
            ExpressionSyntax positionalInstructions = PositionalArgument(creation.ArgumentList, 1);
            ExpressionSyntax positionalName = PositionalArgument(creation.ArgumentList, 2);
            AddAgentFrameworkAgent(creation, creation.ArgumentList, positionalInstructions, positionalName, facts, filePath, result);
        }
    }

    private void AddAgentFrameworkAgent(
        SyntaxNode node,
        ArgumentListSyntax arguments,
        ExpressionSyntax positionalInstructions,
        ExpressionSyntax positionalName,
        FileFacts facts,
        string filePath,
        FileDetectionResult result)
    {
        DetectedAgent agent = NewAgent(AgentFramework, node, filePath);
        ExpressionSyntax nameValue = FindNamedArgument(arguments, "name");
        if (nameValue == null)
        {
            nameValue = positionalName;
        }

        ExpressionSyntax instructionsValue = FindNamedArgument(arguments, "instructions");
        if (instructionsValue == null)
        {
            instructionsValue = positionalInstructions;
        }

        string explicitName = ReadString(nameValue, facts);
        SetName(agent, explicitName, node, "agent");
        agent.Description = ReadDescription(facts, instructionsValue, FindNamedArgument(arguments, "description"));
        ListHelpers.AddDistinct(agent.Models, facts.Models);
        ListHelpers.AddDistinct(agent.Tools, ReadToolList(FindNamedArgument(arguments, "tools")));
        result.Agents.Add(agent);
    }

    // ------------------------------------------------------------ AutoGen.NET

    /// <summary><c>new AssistantAgent(name: "assistant", systemMessage: "...", llmConfig: ...)</c> and friends.</summary>
    private void FindAutoGenAgents(CompilationUnitSyntax root, FileFacts facts, string filePath, FileDetectionResult result)
    {
        if (!result.ReferencedFrameworks.Contains(AutoGen))
        {
            return;
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            string typeName = CreatedTypeName(creation);
            if (!AutoGenAgentTypes.Contains(typeName))
            {
                continue;
            }

            DetectedAgent agent = NewAgent(AutoGen, creation, filePath);
            SetName(agent, ReadString(FindNamedArgument(creation.ArgumentList, "name"), facts), creation, typeName);
            agent.Description = ReadDescription(facts, FindNamedArgument(creation.ArgumentList, "systemMessage"));

            string modelName = ReadString(FindNamedArgument(creation.ArgumentList, "modelName"), facts);
            if (modelName != null)
            {
                ModelReference model = ModelProviders.FromModelString(modelName, facts.FileProvider);
                if (model != null)
                {
                    agent.Models.Add(model);
                }
            }
            else
            {
                ListHelpers.AddDistinct(agent.Models, facts.Models);
            }

            // functionMap: new Dictionary<string, Func<...>> { { nameof(GetWeather), ... } }
            ExpressionSyntax functionMap = FindNamedArgument(creation.ArgumentList, "functionMap");
            if (functionMap != null)
            {
                ListHelpers.AddDistinct(agent.Tools, ReadNameofNames(functionMap));
            }

            ListHelpers.AddDistinct(agent.Tools, facts.AutoGenFunctions);
            result.Agents.Add(agent);
        }
    }

    // ------------------------------------------------------------ Microsoft.Extensions.AI

    /// <summary>
    /// <c>new ChatOptions { Tools = [AIFunctionFactory.Create(GetWeather)] }</c>. Counts as an agent only
    /// when tools are attached; without tools it is a plain LLM call (Section 7.2).
    /// </summary>
    private void FindExtensionsAIAgents(CompilationUnitSyntax root, FileFacts facts, string filePath, FileDetectionResult result)
    {
        bool fileHasAgentFrameworkAgent = false;
        foreach (DetectedAgent existing in result.Agents)
        {
            if (existing.FrameworkKey == AgentFramework)
            {
                fileHasAgentFrameworkAgent = true;
            }
        }

        // Agent Framework agents use ChatOptions internally; do not report them twice.
        if (fileHasAgentFrameworkAgent || !result.ReferencedFrameworks.Contains(ExtensionsAI))
        {
            return;
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (CreatedTypeName(creation) != "ChatOptions")
            {
                continue;
            }

            List<string> tools = ReadToolList(FindInitializerValue(creation.Initializer, "Tools"));
            if (tools.Count == 0)
            {
                continue;
            }

            DetectedAgent agent = NewAgent(ExtensionsAI, creation, filePath);
            SetName(agent, null, creation, "chat_options");
            ListHelpers.AddDistinct(agent.Models, facts.Models);
            ListHelpers.AddDistinct(agent.Tools, tools);
            result.Agents.Add(agent);
        }
    }

    // ------------------------------------------------------------ reading values

    private string ReadDescription(FileFacts facts, params ExpressionSyntax[] candidates)
    {
        bool sawDynamicValue = false;
        foreach (ExpressionSyntax candidate in candidates)
        {
            if (candidate == null)
            {
                continue;
            }

            string text = ReadString(candidate, facts);
            if (string.IsNullOrWhiteSpace(text))
            {
                sawDynamicValue = true;
                continue;
            }

            string description = TextCleanup.CollapseWhitespace(text, Settings.MaxDescriptionChars);
            return SecretFilter.DropIfSecret(description);
        }

        if (sawDynamicValue)
        {
            return "<dynamic>";
        }

        return null;
    }

    /// <summary>A string literal, "a" + "b", or a name that refers to a string constant in the file.</summary>
    private static string ReadString(ExpressionSyntax expression, FileFacts facts)
    {
        if (expression == null)
        {
            return null;
        }

        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        {
            string left = ReadString(binary.Left, facts);
            string right = ReadString(binary.Right, facts);
            if (left != null && right != null)
            {
                return left + right;
            }

            return null;
        }

        if (expression is IdentifierNameSyntax identifier
            && facts.StringConstants.TryGetValue(identifier.Identifier.ValueText, out string constantValue))
        {
            return constantValue;
        }

        return null;
    }

    /// <summary>
    /// Tool names from [a, b], new[] { a, b } or new List&lt;AITool&gt; { ... }, where each item is
    /// AIFunctionFactory.Create(GetWeather), a method group, or another tool object.
    /// </summary>
    private static List<string> ReadToolList(ExpressionSyntax expression)
    {
        List<string> toolNames = new List<string>();
        if (expression == null)
        {
            return toolNames;
        }

        List<ExpressionSyntax> items = new List<ExpressionSyntax>();
        if (expression is CollectionExpressionSyntax collection)
        {
            foreach (CollectionElementSyntax element in collection.Elements)
            {
                if (element is ExpressionElementSyntax expressionElement)
                {
                    items.Add(expressionElement.Expression);
                }
            }
        }
        else if (expression is ImplicitArrayCreationExpressionSyntax implicitArray)
        {
            items.AddRange(implicitArray.Initializer.Expressions);
        }
        else if (expression is ArrayCreationExpressionSyntax array && array.Initializer != null)
        {
            items.AddRange(array.Initializer.Expressions);
        }
        else if (expression is BaseObjectCreationExpressionSyntax listCreation && listCreation.Initializer != null)
        {
            items.AddRange(listCreation.Initializer.Expressions);
        }
        else
        {
            items.Add(expression);
        }

        foreach (ExpressionSyntax item in items)
        {
            string toolName = ToolNameOf(item);
            if (toolName != null && !SecretFilter.ContainsSecret(toolName))
            {
                ListHelpers.AddDistinct(toolNames, toolName);
            }
        }

        return toolNames;
    }

    private static string ToolNameOf(ExpressionSyntax item)
    {
        // AIFunctionFactory.Create(GetWeather) or AIFunctionFactory.Create(..., name: "get_weather")
        if (item is InvocationExpressionSyntax invocation)
        {
            ExpressionSyntax explicitName = FindNamedArgument(invocation.ArgumentList, "name");
            if (explicitName is LiteralExpressionSyntax nameLiteral)
            {
                return nameLiteral.Token.ValueText;
            }

            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                ExpressionSyntax firstArgument = invocation.ArgumentList.Arguments[0].Expression;
                if (firstArgument is IdentifierNameSyntax || firstArgument is MemberAccessExpressionSyntax)
                {
                    return LastName(firstArgument.ToString());
                }
            }

            return MethodName(invocation);
        }

        if (item is IdentifierNameSyntax || item is MemberAccessExpressionSyntax)
        {
            return LastName(item.ToString());
        }

        if (item is BaseObjectCreationExpressionSyntax creation)
        {
            return CreatedTypeName(creation);
        }

        return null;
    }

    private static List<string> ReadNameofNames(ExpressionSyntax expression)
    {
        List<string> names = new List<string>();
        foreach (InvocationExpressionSyntax invocation in expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            bool isNameof = invocation.Expression.ToString() == "nameof";
            if (isNameof && invocation.ArgumentList.Arguments.Count == 1)
            {
                names.Add(LastName(invocation.ArgumentList.Arguments[0].Expression.ToString()));
            }
        }

        return names;
    }

    private static string FirstNonUrlStringArgument(ArgumentListSyntax arguments, FileFacts facts)
    {
        foreach (ArgumentSyntax argument in arguments.Arguments)
        {
            string value = ReadString(argument.Expression, facts);
            bool looksLikeUrl = value != null && value.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (value != null && !looksLikeUrl && value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    // ------------------------------------------------------------ syntax helpers

    private static ExpressionSyntax FindNamedArgument(ArgumentListSyntax arguments, string name)
    {
        if (arguments == null)
        {
            return null;
        }

        foreach (ArgumentSyntax argument in arguments.Arguments)
        {
            if (argument.NameColon != null && argument.NameColon.Name.Identifier.ValueText == name)
            {
                return argument.Expression;
            }
        }

        return null;
    }

    /// <summary>The argument at a position, if it is positional (has no "name:").</summary>
    private static ExpressionSyntax PositionalArgument(ArgumentListSyntax arguments, int position)
    {
        if (arguments == null || position >= arguments.Arguments.Count)
        {
            return null;
        }

        ArgumentSyntax argument = arguments.Arguments[position];
        if (argument.NameColon == null)
        {
            return argument.Expression;
        }

        return null;
    }

    /// <summary>For <c>new X { Name = "a" }</c>, the value assigned to <paramref name="propertyName"/>.</summary>
    private static ExpressionSyntax FindInitializerValue(InitializerExpressionSyntax initializer, string propertyName)
    {
        if (initializer == null)
        {
            return null;
        }

        foreach (ExpressionSyntax expression in initializer.Expressions)
        {
            if (expression is AssignmentExpressionSyntax assignment
                && assignment.Left is IdentifierNameSyntax left
                && left.Identifier.ValueText == propertyName)
            {
                return assignment.Right;
            }
        }

        return null;
    }

    private static bool HasAttribute(MethodDeclarationSyntax method, string attributeName)
    {
        foreach (AttributeListSyntax attributeList in method.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                string name = LastName(attribute.Name.ToString());
                if (name == attributeName || name == attributeName + "Attribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>"builder.Services.AddOpenAIChatCompletion" gives "AddOpenAIChatCompletion"; generic arguments are dropped.</summary>
    private static string MethodName(InvocationExpressionSyntax invocation)
    {
        ExpressionSyntax expression = invocation.Expression;
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.ValueText;
        }

        if (expression is SimpleNameSyntax simpleName)
        {
            return simpleName.Identifier.ValueText;
        }

        return LastName(expression.ToString());
    }

    /// <summary>
    /// "new Microsoft.SemanticKernel.Agents.ChatCompletionAgent()" gives "ChatCompletionAgent".
    /// For a target-typed "new()", the type comes from the declaration: "ChatCompletionAgent agent = new() {...}".
    /// Returns "" when the type cannot be seen.
    /// </summary>
    private static string CreatedTypeName(BaseObjectCreationExpressionSyntax creation)
    {
        TypeSyntax type = null;
        if (creation is ObjectCreationExpressionSyntax objectCreation)
        {
            type = objectCreation.Type;
        }
        else if (creation.Parent is EqualsValueClauseSyntax equalsValue)
        {
            type = DeclaredTypeOf(equalsValue);
        }

        if (type == null || type.IsVar)
        {
            return "";
        }

        string typeName = type.ToString();
        int genericStart = typeName.IndexOf('<');
        if (genericStart >= 0)
        {
            typeName = typeName.Substring(0, genericStart);
        }

        return LastName(typeName);
    }

    /// <summary>The written type of a local, field or property whose initializer is <paramref name="equalsValue"/>.</summary>
    private static TypeSyntax DeclaredTypeOf(EqualsValueClauseSyntax equalsValue)
    {
        if (equalsValue.Parent is VariableDeclaratorSyntax declarator && declarator.Parent is VariableDeclarationSyntax declaration)
        {
            return declaration.Type;
        }

        if (equalsValue.Parent is PropertyDeclarationSyntax property)
        {
            return property.Type;
        }

        return null;
    }

    private static string LastName(string dottedName)
    {
        int lastDot = dottedName.LastIndexOf('.');
        if (lastDot < 0)
        {
            return dottedName;
        }

        return dottedName.Substring(lastDot + 1);
    }

    private static DetectedAgent NewAgent(string frameworkKey, SyntaxNode node, string filePath)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        int line = lineSpan.StartLinePosition.Line + 1;
        string lineText = node.SyntaxTree.GetText().Lines[lineSpan.StartLinePosition.Line].ToString();
        return new DetectedAgent
        {
            FrameworkKey = frameworkKey,
            Evidence = new Evidence(filePath, line, TextCleanup.MakeSnippet(lineText)),
        };
    }

    /// <summary>Name priority: explicit name, then the variable or member it is assigned to, then the enclosing method.</summary>
    private static void SetName(DetectedAgent agent, string explicitName, SyntaxNode node, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName) && !SecretFilter.ContainsSecret(explicitName))
        {
            agent.Name = TextCleanup.CollapseWhitespace(explicitName, 200);
            return;
        }

        string variableName = FindAssignedName(node);
        if (variableName != null)
        {
            agent.VariableName = variableName;
            agent.Name = variableName;
            return;
        }

        agent.Name = fallbackName + "@" + Path.GetFileName(agent.Evidence.File) + ":" + agent.Evidence.Line;
    }

    private static string FindAssignedName(SyntaxNode node)
    {
        SyntaxNode current = node.Parent;
        while (current != null)
        {
            if (current is VariableDeclaratorSyntax declarator)
            {
                return declarator.Identifier.ValueText;
            }

            if (current is AssignmentExpressionSyntax assignment)
            {
                return LastName(assignment.Left.ToString());
            }

            if (current is PropertyDeclarationSyntax property)
            {
                return property.Identifier.ValueText;
            }

            if (current is MethodDeclarationSyntax method)
            {
                return method.Identifier.ValueText;
            }

            // Stop at statement boundaries: anything further up is not this agent's name.
            if (current is StatementSyntax && !(current is LocalDeclarationStatementSyntax) && !(current is ExpressionStatementSyntax) && !(current is ReturnStatementSyntax))
            {
                return null;
            }

            current = current.Parent;
        }

        return null;
    }
}
