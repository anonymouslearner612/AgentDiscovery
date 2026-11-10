using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace ShadowAgents;

/// <summary>One MCP server entry read from an AI client's configuration file (design doc Section 12).</summary>
public class ConfiguredMcpServer
{
    public string Name { get; set; } = "";

    /// <summary>"stdio", "http" or "sse".</summary>
    public string Transport { get; set; } = "stdio";

    public string Command { get; set; }
    public List<string> Arguments { get; set; } = new List<string>();
    public string Url { get; set; }
    public string Package { get; set; }
    public List<string> EnvironmentVariableNames { get; set; } = new List<string>();

    /// <summary>1-based line of the entry in the file, or 0 when unknown.</summary>
    public int Line { get; set; }
}

/// <summary>A configuration file of one AI client, such as Cursor's mcp.json.</summary>
public class McpConfigLocation
{
    public McpConfigLocation(string client, string path, string projectFolder)
    {
        Client = client;
        Path = path;
        ProjectFolder = projectFolder;
    }

    /// <summary>"claude_desktop", "claude_code", "cursor", "vscode", "windsurf", "cline" or "continue".</summary>
    public string Client { get; }

    public string Path { get; }

    /// <summary>The project a project-level file belongs to (".mcp.json" in a repository); null for per-user files.</summary>
    public string ProjectFolder { get; }
}

/// <summary>
/// Where AI clients keep their MCP server lists on Windows (Section 12). Check these paths against current
/// client versions now and then; shadow-mcp keeps an up-to-date list.
/// </summary>
public static class McpClientConfigs
{
    // Per user, relative to the profile folder (C:\Users\alice).
    private static readonly string[][] UserConfigFiles =
    {
        new string[] { "claude_desktop", @"AppData\Roaming\Claude\claude_desktop_config.json" },
        new string[] { "claude_code", @".claude.json" },
        new string[] { "cursor", @".cursor\mcp.json" },
        new string[] { "vscode", @"AppData\Roaming\Code\User\mcp.json" },
        new string[] { "vscode", @"AppData\Roaming\Code\User\settings.json" },
        new string[] { "windsurf", @".codeium\windsurf\mcp_config.json" },
        new string[] { "cline", @"AppData\Roaming\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json" },
        new string[] { "continue", @".continue\config.yaml" },
    };

    // Per project, relative to the project folder.
    private static readonly string[][] ProjectConfigFiles =
    {
        new string[] { "claude_code", @".mcp.json" },
        new string[] { "cursor", @".cursor\mcp.json" },
        new string[] { "vscode", @".vscode\mcp.json" },
    };

    // Folders under X:\Users that are not a person's profile.
    private static readonly string[] SharedProfileNames = { "Public", "Default", "Default User", "All Users" };

    /// <summary>The per-user configuration files that exist in one profile.</summary>
    public static List<McpConfigLocation> UserConfigsIn(string profileFolder)
    {
        List<McpConfigLocation> locations = new List<McpConfigLocation>();
        foreach (string[] entry in UserConfigFiles)
        {
            string path = System.IO.Path.Combine(profileFolder, entry[1]);
            if (File.Exists(path))
            {
                locations.Add(new McpConfigLocation(entry[0], path, null));
            }
        }

        return locations;
    }

    /// <summary>The project-level configuration files that exist in one project folder.</summary>
    public static List<McpConfigLocation> ProjectConfigsIn(string projectFolder)
    {
        List<McpConfigLocation> locations = new List<McpConfigLocation>();
        foreach (string[] entry in ProjectConfigFiles)
        {
            string path = System.IO.Path.Combine(projectFolder, entry[1]);
            if (File.Exists(path))
            {
                locations.Add(new McpConfigLocation(entry[0], path, projectFolder));
            }
        }

        return locations;
    }

    /// <summary>Every person's profile folder on a drive: D:\Users\alice, D:\Users\bob, ...</summary>
    public static List<string> UserProfilesOnDrive(string drive)
    {
        List<string> profiles = new List<string>();
        string usersFolder = drive.TrimEnd('\\', ':') + @":\Users";
        if (!Directory.Exists(usersFolder))
        {
            return profiles;
        }

        try
        {
            foreach (DirectoryInfo folder in FolderListing.ListFolders(usersFolder))
            {
                if (Array.IndexOf(SharedProfileNames, folder.Name) < 0)
                {
                    profiles.Add(folder.FullName);
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // Without access to the Users folder there are no per-user files to read.
        }

        return profiles;
    }

    /// <summary>
    /// Which client a file belongs to, or null when it is not a known MCP configuration file.
    /// Works for both per-user and project-level files. Used for job files and by the watcher's journal.
    /// </summary>
    public static McpConfigLocation Identify(string path)
    {
        foreach (string[] entry in UserConfigFiles)
        {
            if (path.EndsWith(@"\" + entry[1], StringComparison.OrdinalIgnoreCase) && IsDirectlyInProfile(path, entry[1]))
            {
                return new McpConfigLocation(entry[0], path, null);
            }
        }

        foreach (string[] entry in ProjectConfigFiles)
        {
            if (path.EndsWith(@"\" + entry[1], StringComparison.OrdinalIgnoreCase))
            {
                string projectFolder = path.Substring(0, path.Length - entry[1].Length - 1);
                return new McpConfigLocation(entry[0], path, projectFolder);
            }
        }

        return null;
    }

    /// <summary>
    /// A cheap first check on a bare file name, before any path is built. Every MCP configuration file
    /// has one of these names.
    /// </summary>
    public static bool MightBeConfigFileName(string fileName)
    {
        foreach (string[] entry in UserConfigFiles)
        {
            if (System.IO.Path.GetFileName(entry[1]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string[] entry in ProjectConfigFiles)
        {
            if (System.IO.Path.GetFileName(entry[1]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the part of the path before <paramref name="relativePath"/> is X:\Users\&lt;name&gt;.</summary>
    private static bool IsDirectlyInProfile(string path, string relativePath)
    {
        string profileFolder = path.Substring(0, path.Length - relativePath.Length - 1);
        string[] segments = profileFolder.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Reads the MCP servers out of a client configuration file. The servers are never started: everything
/// comes from the file (Section 12, "Never launch a discovered MCP server").
/// </summary>
public static class McpConfigReader
{
    // Options that take a value in "docker run ...", so the value is not mistaken for the image name.
    private static readonly string[] DockerOptionsWithValue =
    {
        "-e", "--env", "--env-file", "-v", "--volume", "--name", "-p", "--publish", "--network", "-w", "--workdir",
        "-u", "--user", "--mount", "--entrypoint", "-l", "--label", "--platform",
    };

    // Words in an argument like "API_KEY=abc" that mean the value is a secret.
    private static readonly string[] SecretNameWords = { "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PWD", "CREDENTIAL", "AUTH" };

    // "postgresql://user:password@host/db": everything between "://" and "@" is a login.
    private static readonly Regex UrlLogin = new Regex(@"://[^/@\s]+@", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly JsonDocumentOptions ForgivingJson = new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The servers in one file. A file that cannot be parsed gives an empty list.</summary>
    public static List<ConfiguredMcpServer> Read(string path, string text)
    {
        if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return ReadContinueYaml(text);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, ForgivingJson);
            return ReadJson(document.RootElement, text);
        }
        catch (JsonException)
        {
            return new List<ConfiguredMcpServer>();
        }
    }

    // ------------------------------------------------------------ JSON clients

    private static List<ConfiguredMcpServer> ReadJson(JsonElement root, string text)
    {
        List<ConfiguredMcpServer> servers = new List<ConfiguredMcpServer>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return servers;
        }

        // Claude Desktop, Claude Code, Cursor, Windsurf, Cline and project .mcp.json: { "mcpServers": { ... } }
        if (root.TryGetProperty("mcpServers", out JsonElement mcpServers))
        {
            AddServersFromObject(mcpServers, text, servers);
        }

        // VS Code mcp.json: { "servers": { ... } }
        if (root.TryGetProperty("servers", out JsonElement vsCodeServers))
        {
            AddServersFromObject(vsCodeServers, text, servers);
        }

        // VS Code settings.json: { "mcp": { "servers": { ... } } }
        if (root.TryGetProperty("mcp", out JsonElement mcpSettings)
            && mcpSettings.ValueKind == JsonValueKind.Object
            && mcpSettings.TryGetProperty("servers", out JsonElement settingsServers))
        {
            AddServersFromObject(settingsServers, text, servers);
        }

        // Claude Code also keeps servers per project: { "projects": { "C:\\code\\bot": { "mcpServers": { ... } } } }
        if (root.TryGetProperty("projects", out JsonElement projects) && projects.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty project in projects.EnumerateObject())
            {
                if (project.Value.ValueKind == JsonValueKind.Object && project.Value.TryGetProperty("mcpServers", out JsonElement projectServers))
                {
                    AddServersFromObject(projectServers, text, servers);
                }
            }
        }

        return servers;
    }

    private static void AddServersFromObject(JsonElement serversObject, string text, List<ConfiguredMcpServer> servers)
    {
        if (serversObject.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty entry in serversObject.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ConfiguredMcpServer server = new ConfiguredMcpServer
            {
                Name = entry.Name,
                Command = ReadString(entry.Value, "command"),
                Url = FirstString(entry.Value, "url", "serverUrl", "httpUrl"),
                Line = LineOfName(text, "\"" + entry.Name + "\""),
            };

            if (entry.Value.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array)
            {
                List<string> rawArguments = new List<string>();
                foreach (JsonElement argument in args.EnumerateArray())
                {
                    if (argument.ValueKind == JsonValueKind.String)
                    {
                        rawArguments.Add(argument.GetString());
                    }
                }

                server.Arguments = rawArguments;
            }

            if (entry.Value.TryGetProperty("env", out JsonElement environment) && environment.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty variable in environment.EnumerateObject())
                {
                    server.EnvironmentVariableNames.Add(variable.Name);
                }
            }

            string declaredTransport = FirstString(entry.Value, "type", "transport");
            Finish(server, declaredTransport);
            servers.Add(server);
        }
    }

    // ------------------------------------------------------------ Continue (config.yaml)

    /// <summary>Continue: <c>mcpServers:</c> is a list of entries with name, command, args, env and url.</summary>
    private static List<ConfiguredMcpServer> ReadContinueYaml(string text)
    {
        List<ConfiguredMcpServer> servers = new List<ConfiguredMcpServer>();
        YamlStream yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(text));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return servers;
        }

        if (yaml.Documents.Count == 0)
        {
            return servers;
        }

        YamlMappingNode root = yaml.Documents[0].RootNode as YamlMappingNode;
        if (root == null)
        {
            return servers;
        }

        YamlNode serverListNode;
        if (!root.Children.TryGetValue(new YamlScalarNode("mcpServers"), out serverListNode))
        {
            return servers;
        }

        YamlSequenceNode serverList = serverListNode as YamlSequenceNode;
        if (serverList == null)
        {
            return servers;
        }

        foreach (YamlNode item in serverList.Children)
        {
            YamlMappingNode entry = item as YamlMappingNode;
            if (entry == null)
            {
                continue;
            }

            ConfiguredMcpServer server = new ConfiguredMcpServer
            {
                Name = YamlScalar(entry, "name"),
                Command = YamlScalar(entry, "command"),
                Url = YamlScalar(entry, "url"),
                Line = (int)entry.Start.Line,
            };

            YamlNode argsNode;
            entry.Children.TryGetValue(new YamlScalarNode("args"), out argsNode);
            YamlSequenceNode argsList = argsNode as YamlSequenceNode;
            if (argsList != null)
            {
                List<string> rawArguments = new List<string>();
                foreach (YamlNode argument in argsList.Children)
                {
                    YamlScalarNode scalar = argument as YamlScalarNode;
                    if (scalar != null && scalar.Value != null)
                    {
                        rawArguments.Add(scalar.Value);
                    }
                }

                server.Arguments = rawArguments;
            }

            YamlNode envNode;
            entry.Children.TryGetValue(new YamlScalarNode("env"), out envNode);
            YamlMappingNode envMapping = envNode as YamlMappingNode;
            if (envMapping != null)
            {
                foreach (KeyValuePair<YamlNode, YamlNode> variable in envMapping.Children)
                {
                    YamlScalarNode variableName = variable.Key as YamlScalarNode;
                    if (variableName != null && variableName.Value != null)
                    {
                        server.EnvironmentVariableNames.Add(variableName.Value);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = "unnamed@line" + server.Line;
            }

            Finish(server, YamlScalar(entry, "type"));
            servers.Add(server);
        }

        return servers;
    }

    private static string YamlScalar(YamlMappingNode mapping, string key)
    {
        YamlNode valueNode;
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out valueNode))
        {
            return null;
        }

        YamlScalarNode scalar = valueNode as YamlScalarNode;
        if (scalar == null || string.IsNullOrWhiteSpace(scalar.Value))
        {
            return null;
        }

        return scalar.Value;
    }

    // ------------------------------------------------------------ shared clean-up

    /// <summary>Works out the transport and package, and removes anything that could be a secret.</summary>
    private static void Finish(ConfiguredMcpServer server, string declaredTransport)
    {
        server.Arguments = WithoutSecrets(server.Arguments);
        server.Url = CleanUrl(server.Url);
        server.Transport = DecideTransport(server, declaredTransport);
        server.Package = FindPackage(server.Command, server.Arguments);
    }

    private static string DecideTransport(ConfiguredMcpServer server, string declaredTransport)
    {
        if (server.Url == null)
        {
            return "stdio";
        }

        if (declaredTransport != null && declaredTransport.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            return "sse";
        }

        return "http";
    }

    /// <summary>
    /// Keeps at most <see cref="Settings.MaxMcpServerArguments"/> arguments and drops every one that is or
    /// assigns a secret: "sk-...", "GITHUB_TOKEN=ghp_...", "--api-key=..."; and the value after "--api-key".
    /// </summary>
    public static List<string> WithoutSecrets(List<string> arguments)
    {
        List<string> safeArguments = new List<string>();
        bool previousNamedASecret = false;
        foreach (string argument in arguments)
        {
            if (safeArguments.Count >= Settings.MaxMcpServerArguments)
            {
                break;
            }

            bool isSecret = previousNamedASecret || SecretFilter.ContainsSecret(argument) || AssignsSecret(argument);
            previousNamedASecret = argument.StartsWith("-", StringComparison.Ordinal) && NamesASecret(argument) && argument.IndexOf('=') < 0;
            if (isSecret)
            {
                continue;
            }

            // Connection strings: keep the address, hide the login ("postgresql://<redacted>@db.local/app").
            string withoutLogin = UrlLogin.Replace(argument, "://<redacted>@");
            safeArguments.Add(TextCleanup.CollapseWhitespace(withoutLogin, 200));
        }

        return safeArguments;
    }

    /// <summary>
    /// "API_KEY=abc", "--token=abc", and "Host=db;Username=app;Password=abc" (any secret name followed by '='
    /// anywhere in the argument).
    /// </summary>
    private static bool AssignsSecret(string argument)
    {
        string upperArgument = argument.ToUpperInvariant();
        foreach (string word in SecretNameWords)
        {
            if (upperArgument.IndexOf(word + "=", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        int equalsIndex = argument.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        return NamesASecret(argument.Substring(0, equalsIndex));
    }

    private static bool NamesASecret(string name)
    {
        string upperName = name.ToUpperInvariant();
        foreach (string word in SecretNameWords)
        {
            if (upperName.IndexOf(word, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"https://user:pw@mcp.example.com/sse?key=abc" becomes "https://mcp.example.com/sse".</summary>
    public static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        Uri parsedUrl;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsedUrl))
        {
            return null;
        }

        // Authority is host and port only: user name and password are not part of it.
        return parsedUrl.Scheme + "://" + parsedUrl.Authority + parsedUrl.AbsolutePath;
    }

    /// <summary>
    /// The package a server is started from: "npx -y @modelcontextprotocol/server-github" gives
    /// "@modelcontextprotocol/server-github", "uvx mcp-server-fetch" gives "mcp-server-fetch",
    /// "docker run -i --rm ghcr.io/github/github-mcp-server" gives the image name.
    /// </summary>
    public static string FindPackage(string command, List<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string program = System.IO.Path.GetFileNameWithoutExtension(command.Trim()).ToLowerInvariant();
        if (program == "npx" || program == "bunx" || program == "uvx" || program == "pipx")
        {
            return FirstNonOption(arguments, 0, new string[] { "--from", "-p", "--package", "--python" });
        }

        if ((program == "pnpm" || program == "yarn") && arguments.Count > 0 && arguments[0] == "dlx")
        {
            return FirstNonOption(arguments, 1, new string[0]);
        }

        if (program == "docker" && arguments.Count > 0 && arguments[0] == "run")
        {
            return FirstNonOption(arguments, 1, DockerOptionsWithValue);
        }

        return null;
    }

    /// <summary>The first argument from <paramref name="startIndex"/> on that is not an option or an option's value.</summary>
    private static string FirstNonOption(List<string> arguments, int startIndex, string[] optionsWithValue)
    {
        for (int index = startIndex; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "run" && index == startIndex)
            {
                continue; // "pipx run name"
            }

            if (Array.IndexOf(optionsWithValue, argument) >= 0)
            {
                index++; // skip the option's value too
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return argument;
        }

        return null;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            string value = ReadString(element, propertyName);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The 1-based line where <paramref name="quotedName"/> first appears as a JSON key, or 0.</summary>
    private static int LineOfName(string text, string quotedName)
    {
        int index = text.IndexOf(quotedName + ":", StringComparison.Ordinal);
        if (index < 0)
        {
            index = text.IndexOf(quotedName, StringComparison.Ordinal);
        }

        if (index < 0)
        {
            return 0;
        }

        int line = 1;
        for (int position = 0; position < index; position++)
        {
            if (text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}

/// <summary>Capabilities and process matching for configured MCP servers.</summary>
public static class McpServerProfile
{
    /// <summary>
    /// Capabilities from the server's name and package, for example "filesystem" for
    /// "@modelcontextprotocol/server-filesystem". The server's tool list is unknown without starting it,
    /// so these words are all there is.
    /// </summary>
    public static DetectedAgent Classify(ConfiguredMcpServer server)
    {
        DetectedAgent profile = new DetectedAgent { Name = server.Name };
        profile.Tools.Add(AsToolName(server.Name));
        if (server.Package != null)
        {
            profile.Tools.Add(AsToolName(server.Package));
        }

        CapabilityClassifier.Apply(profile);

        // Every server's package says "mcp"; that capability means "uses MCP servers" and fits agents, not servers.
        profile.Capabilities.Remove(Capabilities.Mcp);
        return profile;
    }

    /// <summary>
    /// The text that identifies this server's process on a command line: its package, or else a script
    /// path among its arguments. Null when nothing specific enough is known ("node" alone matches too much).
    /// </summary>
    public static string ProcessSignature(McpServerRecord server)
    {
        if (!string.IsNullOrWhiteSpace(server.Package))
        {
            return server.Package;
        }

        foreach (string argument in server.Arguments)
        {
            bool looksLikeScript = argument.IndexOf('\\') >= 0 || argument.IndexOf('/') >= 0;
            if (looksLikeScript && !argument.StartsWith("-", StringComparison.Ordinal))
            {
                return argument;
            }
        }

        return null;
    }

    /// <summary>"@modelcontextprotocol/server-filesystem" becomes "modelcontextprotocol_server_filesystem".</summary>
    private static string AsToolName(string text)
    {
        char[] characters = text.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (!char.IsLetterOrDigit(characters[index]))
            {
                characters[index] = '_';
            }
        }

        return new string(characters).Trim('_');
    }
}
