using System.Text.RegularExpressions;
using System.Text;

namespace ShadowAgents;

/// <summary>
/// Groups an agent's tools into capability categories and derives risk flags (Section 7.3).
/// </summary>
/// <remarks>
/// Only tool names are classified, not the description. Generic description words ("help the user",
/// "read the file") matched almost every agent in early tests and drowned out real signals.
/// </remarks>
public static class CapabilityClassifier
{
    // Keywords without '_' must equal a whole token of the tool name ("send_email_tool" has the
    // tokens send, email, tool). Keywords with '_' must appear in the snake_case tool name.
    private static readonly ToolKeywordRule[] ToolKeywordRules = new ToolKeywordRule[]
    {
        new ToolKeywordRule(Capabilities.WebAccess, new string[]
            { "search", "browse", "browser", "fetch", "http", "https", "scrape", "scraper", "crawl", "crawler", "url",
             "web", "serper", "tavily", "duckduckgo", "wikipedia", "requests", "websearch", "webfetch" }),
        new ToolKeywordRule(Capabilities.EmailMessaging, new string[]
            { "email", "mail", "gmail", "outlook", "send_message", "slack", "teams", "sms", "twilio", "discord", "telegram" }),
        new ToolKeywordRule(Capabilities.Filesystem, new string[]
            { "file", "files", "read_file", "write_file", "upload", "download", "path", "directory", "folder", "fs" }),
        new ToolKeywordRule(Capabilities.CodeExecution, new string[]
            { "python_repl", "repl", "exec", "execute_code", "run_code", "shell", "bash", "terminal", "code_interpreter",
             "subprocess", "powershell" }),
        new ToolKeywordRule(Capabilities.Database, new string[]
            { "sql", "query_db", "db", "database", "mongo", "mongodb", "postgres", "mysql", "sqlite", "redis", "bigquery", "snowflake" }),
        new ToolKeywordRule(Capabilities.Financial, new string[]
            { "refund", "payment", "payments", "charge", "transfer", "payout", "invoice", "stripe", "billing" }),
        new ToolKeywordRule(Capabilities.Destructive, new string[]
            { "delete", "drop", "remove", "truncate", "wipe", "destroy", "purge" }),
        new ToolKeywordRule(Capabilities.IdentityAdmin, new string[]
            { "user", "users", "role", "roles", "permission", "permissions", "grant", "admin", "iam" }),
        new ToolKeywordRule(Capabilities.Delegation, new string[]
            { "agent_tool", "handoff", "sub_agent", "subagent", "delegate" }),
        new ToolKeywordRule(Capabilities.Mcp, new string[]
            { "mcp" }),
    };

    private static readonly string[] HumanApprovalMarkers = new string[]
    {
        "interrupt_before", "interrupt_after", "interrupt(", "human_input=True", "human_input_mode=\"ALWAYS\"",
        "human_input_mode='ALWAYS'", "needs_approval", "require_approval", "HumanApproval", "human_in_the_loop",
        "ask_for_approval",
    };

    /// <summary>Adds tool-derived capabilities to the agent and recomputes its risk flags.</summary>
    public static void Apply(DetectedAgent agent)
    {
        SortedSet<string> capabilities = new SortedSet<string>(agent.Capabilities, StringComparer.Ordinal);
        foreach (string toolName in agent.Tools)
        {
            foreach (string capability in ClassifyToolName(toolName))
            {
                capabilities.Add(capability);
            }
        }

        agent.Capabilities = new List<string>(capabilities);
        agent.RiskFlags = ComputeRiskFlags(capabilities, agent.HasHumanApproval);
    }

    public static bool ShowsHumanApproval(string code)
    {
        foreach (string marker in HumanApprovalMarkers)
        {
            if (code.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static List<string> ClassifyToolName(string toolName)
    {
        string snakeName = ToSnakeCase(toolName);
        HashSet<string> tokens = new HashSet<string>(snakeName.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        List<string> matchedCapabilities = new List<string>();
        foreach (ToolKeywordRule rule in ToolKeywordRules)
        {
            string capability = rule.Capability;
            string[] keywords = rule.Keywords;
            foreach (string keyword in keywords)
            {
                bool matches;
                if (keyword.Contains('_'))
                {
                    matches = snakeName.Contains(keyword, StringComparison.Ordinal);
                }
                else
                {
                    matches = tokens.Contains(keyword);
                }
                if (matches)
                {
                    matchedCapabilities.Add(capability);
                    break;
                }
            }
        }

        return matchedCapabilities;
    }

    private static List<string> ComputeRiskFlags(SortedSet<string> capabilities, bool hasHumanApproval)
    {
        List<string> riskFlags = new List<string>();
        if (capabilities.Contains(Capabilities.Financial) && !hasHumanApproval)
        {
            riskFlags.Add(RiskFlags.FinancialWithoutApproval);
        }

        if (capabilities.Contains(Capabilities.Destructive) && !hasHumanApproval)
        {
            riskFlags.Add(RiskFlags.DestructiveWithoutApproval);
        }

        if (capabilities.Contains(Capabilities.CodeExecution))
        {
            riskFlags.Add(RiskFlags.CodeExecution);
        }

        if (capabilities.Contains(Capabilities.WebAccess) && capabilities.Contains(Capabilities.EmailMessaging))
        {
            riskFlags.Add(RiskFlags.ExfiltrationCombo);
        }

        return riskFlags;
    }

    /// <summary>"SerperDevTool" becomes "serper_dev_tool"; "send-email" becomes "send_email".</summary>
    private static string ToSnakeCase(string name)
    {
        StringBuilder snake = new StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (!char.IsLetterOrDigit(character))
            {
                snake.Append('_');
                continue;
            }

            bool startsNewWord = char.IsUpper(character)
                && index > 0
                && (char.IsLower(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]) && char.IsUpper(name[index - 1])));
            if (startsNewWord)
            {
                snake.Append('_');
            }

            snake.Append(char.ToLowerInvariant(character));
        }

        return snake.ToString();
    }
}

/// <summary>Confidence levels from Section 7.4 of the design doc.</summary>
public static class AgentConfidence
{
    /// <summary>
    /// high: constructor matched and tools, model or description found.
    /// medium: constructor matched, nothing else found.
    /// low: a graph builder with no nodes (framework usage only).
    /// </summary>
    public static Confidence Decide(DetectedAgent agent, bool isGraphWithoutNodes)
    {
        if (isGraphWithoutNodes)
        {
            return Confidence.Low;
        }

        if (HasExtractedAttributes(agent))
        {
            return Confidence.High;
        }

        return Confidence.Medium;
    }

    /// <summary>Called after the scanner adds attributes from other files (models, CrewAI YAML).</summary>
    public static void UpgradeIfAttributesFound(DetectedAgent agent)
    {
        if (agent.Confidence == Confidence.Medium && HasExtractedAttributes(agent))
        {
            agent.Confidence = Confidence.High;
        }
    }

    private static bool HasExtractedAttributes(DetectedAgent agent)
    {
        bool hasRealDescription = agent.Description != null && agent.Description != "<dynamic>";
        return agent.Tools.Count > 0 || agent.Models.Count > 0 || hasRealDescription;
    }
}

/// <summary>Works out which LLM provider a model string or model class belongs to.</summary>
public static class ModelProviders
{
    // Prefixes used in provider-qualified model strings: "openai:gpt-4o" (Pydantic AI),
    // "openai/gpt-4o" (LiteLLM), "anthropic:claude-sonnet-4-5" and so on.
    private static readonly Dictionary<string, string> ProviderPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "openai",
        ["azure"] = "azure_openai",
        ["azure_openai"] = "azure_openai",
        ["anthropic"] = "anthropic",
        ["claude"] = "anthropic",
        ["google"] = "google",
        ["google-gla"] = "google",
        ["google-vertex"] = "google",
        ["gemini"] = "google",
        ["vertex_ai"] = "google",
        ["bedrock"] = "bedrock",
        ["ollama"] = "ollama",
        ["ollama_chat"] = "ollama",
        ["groq"] = "groq",
        ["mistral"] = "mistral",
        ["cohere"] = "cohere",
        ["deepseek"] = "deepseek",
        ["xai"] = "xai",
        ["openrouter"] = "openrouter",
        ["huggingface"] = "huggingface",
        ["together_ai"] = "together_ai",
    };

    // Checked in order against the lower-cased class name, so "azure" must come before "openai".
    private static readonly ClassNameProvider[] ClassNameFragments = new ClassNameProvider[]
    {
        new ClassNameProvider("azure", "azure_openai"),
        new ClassNameProvider("openai", "openai"),
        new ClassNameProvider("anthropic", "anthropic"),
        new ClassNameProvider("claude", "anthropic"),
        new ClassNameProvider("bedrock", "bedrock"),
        new ClassNameProvider("gemini", "google"),
        new ClassNameProvider("googlegenerativeai", "google"),
        new ClassNameProvider("vertex", "google"),
        new ClassNameProvider("googlemodel", "google"),
        new ClassNameProvider("ollama", "ollama"),
        new ClassNameProvider("groq", "groq"),
        new ClassNameProvider("mistral", "mistral"),
        new ClassNameProvider("cohere", "cohere"),
        new ClassNameProvider("deepseek", "deepseek"),
        new ClassNameProvider("huggingface", "huggingface"),
        new ClassNameProvider("inferenceclient", "huggingface"),
        new ClassNameProvider("hfapi", "huggingface"),
        new ClassNameProvider("litellm", "litellm"),
        new ClassNameProvider("google", "google"),
    };

    /// <summary>
    /// Parses a model string such as "gpt-4o", "openai:gpt-4o" or "openai/gpt-4o".
    /// <paramref name="classProvider"/> is used when the string itself does not name a provider.
    /// </summary>
    public static ModelReference FromModelString(string rawModel, string classProvider)
    {
        string model = rawModel.Trim();
        if (model.Length == 0 || model.Length > 200 || SecretFilter.ContainsSecret(model))
        {
            return null;
        }

        int separatorIndex = model.IndexOfAny(new char[] { ':', '/' });
        if (separatorIndex > 0)
        {
            string prefix = model.Substring(0, separatorIndex);
            if (ProviderPrefixes.TryGetValue(prefix, out string prefixProvider))
            {
                return new ModelReference(model.Substring(separatorIndex + 1), prefixProvider);
            }
        }

        string provider = classProvider;
        if (provider == null)
        {
            provider = FromModelName(model);
        }
        return new ModelReference(model, provider);
    }

    public static string FromClassName(string className)
    {
        string lowerName = className.ToLowerInvariant();
        foreach (ClassNameProvider fragment in ClassNameFragments)
        {
            string nameFragment = fragment.NameFragment;
            string provider = fragment.Provider;
            if (lowerName.Contains(nameFragment, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>Infers the provider from a bare model name such as "gpt-4o" or "claude-sonnet-4-5".</summary>
    public static string FromModelName(string model)
    {
        string lowerModel = model.ToLowerInvariant();
        if (lowerModel.Contains("anthropic.") || lowerModel.StartsWith("amazon.", StringComparison.Ordinal))
        {
            // Bedrock model ids: "anthropic.claude-3-5-sonnet-...", "us.anthropic.claude-...", "amazon.nova-pro-v1:0"
            return "bedrock";
        }

        if (lowerModel.StartsWith("gpt-", StringComparison.Ordinal)
            || lowerModel.StartsWith("o1", StringComparison.Ordinal)
            || lowerModel.StartsWith("o3", StringComparison.Ordinal)
            || lowerModel.StartsWith("o4", StringComparison.Ordinal)
            || lowerModel.StartsWith("chatgpt", StringComparison.Ordinal))
        {
            return "openai";
        }

        if (lowerModel.StartsWith("claude", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (lowerModel.StartsWith("gemini", StringComparison.Ordinal))
        {
            return "google";
        }

        return null;
    }

    /// <summary>True when a called name looks like something that creates or wraps a model.</summary>
    public static bool LooksLikeModelFactory(string calleeName)
    {
        if (FromClassName(calleeName) != null)
        {
            return true;
        }

        string[] modelWords = new string[] { "Model", "LLM", "Llm", "Chat", "Completion", "Generator", "init_chat_model", "ModelClient" };
        foreach (string modelWord in modelWords)
        {
            if (calleeName.Contains(modelWord, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Secrets must never leave the machine (Section 10). Argument values that look like a secret are
/// dropped; snippets have the secret part replaced.
/// </summary>
public static class SecretFilter
{
    private const string Redacted = "<redacted>";

    public static bool ContainsSecret(string value)
    {
        try
        {
            return SecretPattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: if we cannot tell, treat the value as a secret.
            return true;
        }
    }

    /// <summary>Returns the value, or null when it contains a secret.</summary>
    public static string DropIfSecret(string value)
    {
        if (value == null)
        {
            return null;
        }

        if (ContainsSecret(value))
        {
            return null;
        }

        return value;
    }

    public static string Redact(string value)
    {
        try
        {
            return SecretPattern.Replace(value, Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    // OpenAI / Anthropic keys, AWS access keys, GitHub tokens, Slack tokens, Google API keys,
    // Hugging Face tokens, private key blocks, JWTs, and generic "api_key=..." style assignments.
    private static readonly Regex SecretPattern = new Regex("""
        sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}
        |(?:AKIA|ASIA)[0-9A-Z]{16}
        |gh[pousr]_[A-Za-z0-9]{36,}
        |github_pat_[A-Za-z0-9_]{22,}
        |xox[abprs]-[A-Za-z0-9\-]{10,}
        |AIza[0-9A-Za-z_\-]{35}
        |hf_[A-Za-z0-9]{30,}
        |-----BEGIN[A-Z ]*PRIVATE\ KEY-----
        |eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}
        |(?i:(?:api[_-]?key|secret|password|passwd|token)\s*[:=]\s*['"][^'"\s]{8,}['"])
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
}

public static class TextCleanup
{
    public const int MaxSnippetChars = 200;

    /// <summary>Collapses runs of whitespace (including newlines) into single spaces and caps the length.</summary>
    public static string CollapseWhitespace(string text, int maxChars)
    {
        StringBuilder collapsed = new StringBuilder(Math.Min(text.Length, maxChars));
        bool lastWasWhitespace = false;
        foreach (char character in text)
        {
            if (collapsed.Length >= maxChars)
            {
                break;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace && collapsed.Length > 0)
                {
                    collapsed.Append(' ');
                }

                lastWasWhitespace = true;
                continue;
            }

            collapsed.Append(character);
            lastWasWhitespace = false;
        }

        return collapsed.ToString().TrimEnd();
    }

    /// <summary>A one-line evidence snippet: trimmed, capped at 200 characters, secrets redacted.</summary>
    public static string MakeSnippet(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length > MaxSnippetChars)
        {
            trimmed = trimmed.Substring(0, MaxSnippetChars);
        }

        return SecretFilter.Redact(trimmed);
    }
}

/// <summary>Tool-name keywords that show an agent has a capability.</summary>
public class ToolKeywordRule
{
    public ToolKeywordRule(string capability, string[] keywords)
    {
        Capability = capability;
        Keywords = keywords;
    }

    public string Capability { get; }
    public string[] Keywords { get; }
}

/// <summary>A fragment of a model class name (such as "azure") and the provider it means.</summary>
public class ClassNameProvider
{
    public ClassNameProvider(string nameFragment, string provider)
    {
        NameFragment = nameFragment;
        Provider = provider;
    }

    public string NameFragment { get; }
    public string Provider { get; }
}
