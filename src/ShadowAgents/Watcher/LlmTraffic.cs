using System.Text.Json;
using System.Text;

namespace ShadowAgents;

/// <summary>
/// Recognises LLM provider endpoints in proxy traffic and reads the model each request asks for
/// (Section 6.5, "Proxy correlation").
/// </summary>
public static class LlmEndpoints
{
    // Host name (exact, or ending with the value when it starts with '.') → provider.
    private static readonly string[][] HostProviders =
    {
        new[] { "api.openai.com", "openai" },
        new[] { "api.anthropic.com", "anthropic" },
        new[] { ".openai.azure.com", "azure_openai" },
        new[] { ".services.ai.azure.com", "azure_openai" },
        new[] { ".cognitiveservices.azure.com", "azure_openai" },
        new[] { "generativelanguage.googleapis.com", "google" },
        new[] { "aiplatform.googleapis.com", "google" },
        new[] { ".aiplatform.googleapis.com", "google" },
        new[] { "api.mistral.ai", "mistral" },
        new[] { "api.groq.com", "groq" },
        new[] { "api.cohere.com", "cohere" },
        new[] { "api.cohere.ai", "cohere" },
        new[] { "api.deepseek.com", "deepseek" },
        new[] { "api.x.ai", "xai" },
        new[] { "openrouter.ai", "openrouter" },
        new[] { "api.together.xyz", "together_ai" },
        new[] { "api-inference.huggingface.co", "huggingface" },
        new[] { "router.huggingface.co", "huggingface" },
    };

    private const int OllamaPort = 11434;

    /// <summary>The provider for a request URL, or null when it is not an LLM endpoint.</summary>
    public static string ProviderFor(Uri url)
    {
        string host = url.Host.ToLowerInvariant();

        bool isBedrock = host.StartsWith("bedrock-runtime.", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal);
        if (isBedrock)
        {
            return "bedrock";
        }

        bool isLocalHost = host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]";
        if (isLocalHost && url.Port == OllamaPort)
        {
            return "ollama";
        }

        foreach (string[] hostProvider in HostProviders)
        {
            string hostPattern = hostProvider[0];
            bool matches;
            if (hostPattern.StartsWith(".", StringComparison.Ordinal))
            {
                matches = host.EndsWith(hostPattern, StringComparison.Ordinal);
            }
            else
            {
                matches = host == hostPattern;
            }
            if (matches)
            {
                return hostProvider[1];
            }
        }

        return null;
    }

    /// <summary>
    /// The model a request asks for: from the URL for Azure deployments, Bedrock and Gemini, else the
    /// "model" field of the JSON body (OpenAI, Anthropic, Ollama, Mistral, ...). Null when not found.
    /// </summary>
    public static ModelRecord ExtractModel(Uri url, string requestBody)
    {
        string provider = ProviderFor(url);
        if (provider == null)
        {
            return null;
        }

        string modelId = ModelFromPath(url.AbsolutePath);
        if (modelId == null)
        {
            modelId = ModelFromBody(requestBody);
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return new ModelRecord { Id = modelId, Provider = provider, Source = "observed" };
    }

    private static string ModelFromPath(string path)
    {
        string[] segments = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            // Azure OpenAI: /openai/deployments/{deployment}/chat/completions
            // Bedrock:      /model/{modelId}/invoke  or  /model/{modelId}/converse
            if (segments[index] == "deployments" || segments[index] == "model")
            {
                return Uri.UnescapeDataString(segments[index + 1]);
            }

            // Gemini: /v1beta/models/{model}:generateContent
            if (segments[index] == "models")
            {
                string modelAndMethod = segments[index + 1];
                int colonIndex = modelAndMethod.IndexOf(':');
                if (colonIndex < 0)
                {
                    return modelAndMethod;
                }

                return modelAndMethod.Substring(0, colonIndex);
            }
        }

        return null;
    }

    private static string ModelFromBody(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using JsonDocument body = JsonDocument.Parse(requestBody);
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String)
            {
                return model.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON (streamed, compressed or binary): no model from the body.
        }

        return null;
    }
}

/// <summary>One request seen by Titanium Proxy, attributed to the process that sent it.</summary>
public class ProxyRequestEvent
{
    public int ProcessId { get; set; }
    public string Url { get; set; } = "";

    /// <summary>The request body when the proxy can read it (TLS-inspected traffic), else empty.</summary>
    public string Body { get; set; }
}

/// <summary>
/// Reads proxy events that Titanium Proxy appends to proxy-events.jsonl, one JSON object per line.
/// </summary>
/// <remarks>
/// A file keeps the proxy and the watcher loosely coupled while they are separate programs. Once the
/// watcher runs inside the endpoint agent, the proxy's request handler can call
/// <see cref="WatcherService.OnProxyRequest"/> directly and this file is not needed.
/// </remarks>
public class ProxyEventReader
{
    private const int MaxBytesPerRead = 4 * 1024 * 1024;

    private readonly string path;

    public ProxyEventReader(string path)
    {
        this.path = path;
    }

    /// <summary>Reads events written since <paramref name="offset"/> and moves the offset past them.</summary>
    public List<ProxyRequestEvent> ReadNewEvents(ref long offset)
    {
        List<ProxyRequestEvent> events = new List<ProxyRequestEvent>();
        if (!File.Exists(this.path))
        {
            return events;
        }

        using FileStream stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset)
        {
            offset = 0; // the proxy started a new file
        }

        stream.Seek(offset, SeekOrigin.Begin);
        int bytesToRead = (int)Math.Min(MaxBytesPerRead, stream.Length - offset);
        byte[] buffer = new byte[bytesToRead];
        int bytesRead = stream.Read(buffer, 0, bytesToRead);

        // Only use complete lines; a line still being written is read next time.
        int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, bytesRead - 1));
        if (bytesRead == 0 || lastNewline < 0)
        {
            return events;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        offset += lastNewline + 1;

        foreach (string rawLine in text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Some writers (such as Windows PowerShell 5) put a byte-order mark at the start of a new file.
            string line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                ProxyRequestEvent proxyEvent = JsonSerializer.Deserialize<ProxyRequestEvent>(line, ContractJson.ReadOptions);
                if (proxyEvent != null)
                {
                    events.Add(proxyEvent);
                }
            }
            catch (JsonException)
            {
                // Skip a broken line; keep reading the rest.
            }
        }

        return events;
    }
}
