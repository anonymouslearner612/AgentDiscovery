using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShadowAgents;

public enum ManifestKind
{
    PythonManifest,
    PythonLockFile,
    NpmPackage,
    DotNetProject,
    LangGraphConfig,
    CrewAiConfig,
}

/// <summary>A framework dependency declared in a manifest.</summary>
public sealed class ManifestReference
{
    public ManifestReference(string frameworkKey, string packageName, string version)
    {
        FrameworkKey = frameworkKey;
        PackageName = packageName;
        Version = version;
    }

    public string FrameworkKey { get; }
    public string PackageName { get; }
    public string Version { get; }
}

public sealed class ManifestInfo
{
    public string FilePath { get; set; }
    public ManifestKind Kind { get; set; }
    public List<ManifestReference> References { get; } = new List<ManifestReference>();
    public string ProjectName { get; set; }

    public string FileName
    {
        get { return Path.GetFileName(FilePath); }
    }

    public string DirectoryPath
    {
        get { return PathHelpers.FolderOf(FilePath); }
    }

    /// <summary>
    /// CrewAI YAML usually lives in <c>src/&lt;name&gt;/config/</c>. It belongs to the surrounding project
    /// instead of starting a project of its own.
    /// </summary>
    public bool StartsProjectRoot
    {
        get { return Kind != ManifestKind.CrewAiConfig; }
    }
}

/// <summary>Stage 2 (Referenced): finds in-scope framework dependencies in a manifest file.</summary>
public static class ManifestReader
{
    public static ManifestInfo Read(string filePath, string text)
    {
        string fileName = Path.GetFileName(filePath);
        ManifestKind kind = ClassifyManifest(fileName);
        ManifestInfo manifest = new ManifestInfo { FilePath = filePath, Kind = kind };

        try
        {
            switch (kind)
            {
                case ManifestKind.NpmPackage:
                    ReadPackageJson(text, manifest);
                    break;
                case ManifestKind.DotNetProject:
                    ReadDotNetPackageReferences(text, manifest);
                    break;
                case ManifestKind.LangGraphConfig:
                    ReadLangGraphConfig(text, manifest);
                    break;
                case ManifestKind.CrewAiConfig:
                    ReadCrewAiConfig(fileName, text, manifest);
                    break;
                case ManifestKind.PythonLockFile:
                    ReadPythonLockFile(fileName, text, manifest);
                    break;
                default:
                    ReadPythonManifest(fileName, text, manifest);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException || exception is RegexMatchTimeoutException)
        {
            // A broken or hostile manifest gives no references; it still marks a project folder.
        }

        return manifest;
    }

    private static ManifestKind ClassifyManifest(string fileName)
    {
        string lowerName = fileName.ToLowerInvariant();
        if (lowerName == "package.json")
        {
            return ManifestKind.NpmPackage;
        }

        if (lowerName.EndsWith(".csproj") || lowerName.EndsWith(".fsproj")
            || lowerName == "packages.config" || lowerName == "directory.packages.props")
        {
            return ManifestKind.DotNetProject;
        }

        if (lowerName == "langgraph.json")
        {
            return ManifestKind.LangGraphConfig;
        }

        if (lowerName == "crew.yaml" || lowerName == "agents.yaml" || lowerName == "tasks.yaml")
        {
            return ManifestKind.CrewAiConfig;
        }

        if (lowerName == "poetry.lock" || lowerName == "uv.lock" || lowerName == "pipfile.lock")
        {
            return ManifestKind.PythonLockFile;
        }

        return ManifestKind.PythonManifest;
    }

    /// <summary>
    /// requirements*.txt, pyproject.toml, Pipfile, setup.py, setup.cfg, environment.yml: every word that
    /// is a known framework distribution name counts, with the version that follows it if any.
    /// </summary>
    private static void ReadPythonManifest(string fileName, string text, ManifestInfo manifest)
    {
        foreach (Match token in PythonPackageToken.Matches(text))
        {
            string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.PyPI, token.Value);
            if (frameworkKey == null)
            {
                continue;
            }

            Match versionMatch = VersionAfterPackageName.Match(text, token.Index + token.Length);
            string version = null;
            if (versionMatch.Success)
            {
                version = versionMatch.Groups["version"].Value;
            }
            AddReference(manifest, new ManifestReference(frameworkKey, token.Value, version));
        }

        if (fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            Match projectName = TomlProjectName.Match(text);
            if (projectName.Success)
            {
                manifest.ProjectName = projectName.Groups["name"].Value;
            }
        }
    }

    /// <summary>poetry.lock and uv.lock list "name = ..." followed by "version = ...". Pipfile.lock is JSON.</summary>
    private static void ReadPythonLockFile(string fileName, string text, ManifestInfo manifest)
    {
        if (fileName.Equals("Pipfile.lock", StringComparison.OrdinalIgnoreCase))
        {
            ReadPipfileLock(text, manifest);
            return;
        }

        foreach (Match lockedPackage in LockFilePackage.Matches(text))
        {
            string packageName = lockedPackage.Groups["name"].Value;
            string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.PyPI, packageName);
            if (frameworkKey != null)
            {
                AddReference(manifest, new ManifestReference(frameworkKey, packageName, lockedPackage.Groups["version"].Value));
            }
        }
    }

    private static void ReadPipfileLock(string text, ManifestInfo manifest)
    {
        using JsonDocument pipfileLock = JsonDocument.Parse(text);
        foreach (string sectionName in new[] { "default", "develop" })
        {
            if (!pipfileLock.RootElement.TryGetProperty(sectionName, out JsonElement section) || section.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty package in section.EnumerateObject())
            {
                string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.PyPI, package.Name);
                if (frameworkKey == null)
                {
                    continue;
                }

                string version = null;
                if (package.Value.ValueKind == JsonValueKind.Object
                    && package.Value.TryGetProperty("version", out JsonElement versionElement)
                    && versionElement.ValueKind == JsonValueKind.String)
                {
                    version = versionElement.GetString().TrimStart('=');
                }

                AddReference(manifest, new ManifestReference(frameworkKey, package.Name, version));
            }
        }
    }

    private static void ReadPackageJson(string text, ManifestInfo manifest)
    {
        using JsonDocument packageJson = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = packageJson.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            manifest.ProjectName = nameElement.GetString();
        }

        foreach (string sectionName in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (!root.TryGetProperty(sectionName, out JsonElement section) || section.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty dependency in section.EnumerateObject())
            {
                string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.Npm, dependency.Name);
                if (frameworkKey == null)
                {
                    continue;
                }

                string version = null;
                if (dependency.Value.ValueKind == JsonValueKind.String)
                {
                    version = dependency.Value.GetString();
                }
                AddReference(manifest, new ManifestReference(frameworkKey, dependency.Name, version));
            }
        }
    }

    private static void ReadDotNetPackageReferences(string text, ManifestInfo manifest)
    {
        foreach (Match packageReference in DotNetPackageReference.Matches(text))
        {
            string packageId = packageReference.Groups["id"].Value;
            string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.NuGet, packageId);
            if (frameworkKey == null)
            {
                continue;
            }

            Group versionGroup = packageReference.Groups["version"];
            string version = null;
            if (versionGroup.Success)
            {
                version = versionGroup.Value;
            }

            AddReference(manifest, new ManifestReference(frameworkKey, packageId, version));
        }
    }

    /// <summary>langgraph.json serves both LangGraph (Python) and LangGraph.js; the JS one sets "node_version".</summary>
    private static void ReadLangGraphConfig(string text, ManifestInfo manifest)
    {
        string frameworkKey = "langgraph";
        if (text.Contains("\"node_version\"", StringComparison.Ordinal))
        {
            frameworkKey = "langgraph_js";
        }
        AddReference(manifest, new ManifestReference(frameworkKey, "langgraph.json", null));
    }

    private static void ReadCrewAiConfig(string fileName, string text, ManifestInfo manifest)
    {
        string lowerFileName = fileName.ToLowerInvariant();
        bool isCrewAiConfig = true;
        if (lowerFileName == "agents.yaml")
        {
            isCrewAiConfig = CrewAiConfigReader.LooksLikeAgentsConfig(text);
        }
        else if (lowerFileName == "tasks.yaml")
        {
            isCrewAiConfig = text.Contains("expected_output:", StringComparison.Ordinal);
        }

        if (isCrewAiConfig)
        {
            AddReference(manifest, new ManifestReference("crewai", fileName, null));
        }
    }

    /// <summary>One reference per framework: the first one found, upgraded if a later one has a version.</summary>
    private static void AddReference(ManifestInfo manifest, ManifestReference reference)
    {
        for (int index = 0; index < manifest.References.Count; index++)
        {
            ManifestReference existing = manifest.References[index];
            if (existing.FrameworkKey != reference.FrameworkKey)
            {
                continue;
            }

            if (existing.Version == null && reference.Version != null)
            {
                manifest.References[index] = reference;
            }

            return;
        }

        manifest.References.Add(reference);
    }

    private static readonly Regex PythonPackageToken = new Regex(@"[A-Za-z][A-Za-z0-9._\-]*", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(2000));

    // Right after a package name: optional extras, then "==1.2", ">=1.2", "~=1.2", " = \"^1.2\"" (Poetry/Pipfile).
    private static readonly Regex VersionAfterPackageName = new Regex("""\G(?:\[[^\]\r\n]*\])?[ \t]*(?:===?|~=|>=|>|\^|~|=[ \t]*["'][\^~>=]*)?[ \t]*["']?(?<version>\d[\w.+\-]*)""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex LockFilePackage = new Regex(@"(?m)^name\s*=\s*""(?<name>[^""\r\n]+)""\s*\r?\nversion\s*=\s*""(?<version>[^""\r\n]+)""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(2000));

    private static readonly Regex TomlProjectName = new Regex("""(?m)^\s*name\s*=\s*["'](?<name>[^"'\r\n]{1,200})["']""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    // <PackageReference Include="X" Version="Y" />, <PackageVersion Include=... />, <package id="X" version="Y" />
    private static readonly Regex DotNetPackageReference = new Regex("""(?:Include|id)\s*=\s*"(?<id>[^"\r\n]+)"(?:\s+[Vv]ersion\s*=\s*"(?<version>[^"\r\n]+)")?""", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));
}

/// <summary>
/// Stage 1 (Installed): reads package names from dependency folders. Only folder names are listed;
/// the only file ever opened is a matched package's small <c>package.json</c>, for its version.
/// </summary>
public static class InstalledPackageReader
{
    /// <summary>
    /// Reads any dependency folder by its name: node_modules, site-packages, dist-packages or .nuget.
    /// </summary>
    public static void ReadDependencyFolder(string folderPath, string folderName, List<InstalledPackage> installedPackages)
    {
        if (folderName.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            ReadNodeModules(folderPath, installedPackages);
        }
        else if (folderName.Equals(".nuget", StringComparison.OrdinalIgnoreCase))
        {
            ReadNuGetPackages(Path.Combine(folderPath, "packages"), installedPackages);
        }
        else
        {
            ReadSitePackages(folderPath, installedPackages);
        }
    }

    /// <summary>
    /// Reads a Python <c>site-packages</c> folder through its <c>*.dist-info</c> and <c>*.egg-info</c>
    /// folder names, which carry both the distribution name and the version.
    /// </summary>
    public static void ReadSitePackages(string sitePackagesPath, List<InstalledPackage> installedPackages)
    {
        foreach (string folderName in ListDirectoryNames(sitePackagesPath))
        {
            if (!TryParseDistributionFolder(folderName, out string distributionName, out string version))
            {
                continue;
            }

            string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.PyPI, distributionName);
            if (frameworkKey != null)
            {
                installedPackages.Add(new InstalledPackage(frameworkKey, distributionName, version, sitePackagesPath, PackageEcosystem.PyPI));
            }
        }
    }

    /// <summary>Reads top-level and scoped (<c>@scope/name</c>) package folders of a <c>node_modules</c> folder.</summary>
    public static void ReadNodeModules(string nodeModulesPath, List<InstalledPackage> installedPackages)
    {
        foreach (string folderName in ListDirectoryNames(nodeModulesPath))
        {
            if (folderName.StartsWith("@", StringComparison.Ordinal))
            {
                string scopePath = Path.Combine(nodeModulesPath, folderName);
                foreach (string scopedFolderName in ListDirectoryNames(scopePath))
                {
                    AddNodePackage(nodeModulesPath, $"{folderName}/{scopedFolderName}", installedPackages);
                }

                continue;
            }

            AddNodePackage(nodeModulesPath, folderName, installedPackages);
        }
    }

    /// <summary>Reads the NuGet global packages folder: <c>packages\&lt;id&gt;\&lt;version&gt;</c>.</summary>
    public static void ReadNuGetPackages(string nuGetPackagesPath, List<InstalledPackage> installedPackages)
    {
        foreach (string packageId in ListDirectoryNames(nuGetPackagesPath))
        {
            string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.NuGet, packageId);
            if (frameworkKey == null)
            {
                continue;
            }

            string newestVersion = FindNewestVersionFolder(Path.Combine(nuGetPackagesPath, packageId));
            installedPackages.Add(new InstalledPackage(frameworkKey, packageId, newestVersion, nuGetPackagesPath, PackageEcosystem.NuGet));
        }
    }

    /// <summary>Finds <c>Lib\site-packages</c> (Windows) or <c>lib/pythonX.Y/site-packages</c> in a Python environment.</summary>
    public static List<string> FindSitePackagesFolders(string environmentPath)
    {
        List<string> sitePackagesFolders = new List<string>();
        string windowsLayout = Path.Combine(environmentPath, "Lib", "site-packages");
        if (Directory.Exists(windowsLayout))
        {
            sitePackagesFolders.Add(windowsLayout);
            return sitePackagesFolders;
        }

        string libPath = Path.Combine(environmentPath, "lib");
        foreach (string folderName in ListDirectoryNames(libPath))
        {
            if (!folderName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string unixLayout = Path.Combine(libPath, folderName, "site-packages");
            if (Directory.Exists(unixLayout))
            {
                sitePackagesFolders.Add(unixLayout);
            }
        }

        return sitePackagesFolders;
    }

    /// <summary>
    /// "langchain_core-0.3.21.dist-info" gives ("langchain_core", "0.3.21").
    /// "crewai-0.80.0-py3.11.egg-info" gives ("crewai", "0.80.0").
    /// </summary>
    private static bool TryParseDistributionFolder(string folderName, out string distributionName, out string version)
    {
        distributionName = "";
        version = "";

        string nameAndVersion;
        if (folderName.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
        {
            nameAndVersion = folderName.Substring(0, folderName.Length - ".dist-info".Length);
        }
        else if (folderName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase))
        {
            nameAndVersion = folderName.Substring(0, folderName.Length - ".egg-info".Length);
        }
        else
        {
            return false;
        }

        string[] parts = nameAndVersion.Split('-');
        if (parts.Length < 2)
        {
            return false;
        }

        distributionName = parts[0];
        version = parts[1];
        return true;
    }

    private static void AddNodePackage(string nodeModulesPath, string packageName, List<InstalledPackage> installedPackages)
    {
        string frameworkKey = PackageCatalog.FindFramework(PackageEcosystem.Npm, packageName);
        if (frameworkKey == null)
        {
            return;
        }

        string packageJsonPath = Path.Combine(nodeModulesPath, packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
        string version = TryReadPackageJsonVersion(packageJsonPath);
        installedPackages.Add(new InstalledPackage(frameworkKey, packageName, version, nodeModulesPath, PackageEcosystem.Npm));
    }

    private static string TryReadPackageJsonVersion(string packageJsonPath)
    {
        const long maxPackageJsonBytes = 256 * 1024;
        try
        {
            FileInfo packageJsonFile = new FileInfo(packageJsonPath);
            if (!packageJsonFile.Exists || packageJsonFile.Length > maxPackageJsonBytes)
            {
                return null;
            }

            using JsonDocument packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (packageJson.RootElement.TryGetProperty("version", out JsonElement versionElement)
                && versionElement.ValueKind == JsonValueKind.String)
            {
                return versionElement.GetString();
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException)
        {
            // Version is best effort.
        }

        return null;
    }

    private static string FindNewestVersionFolder(string packagePath)
    {
        string newestVersionText = null;
        Version newestVersion = null;
        foreach (string versionFolder in ListDirectoryNames(packagePath))
        {
            // "9.0.0-preview.1" parses on the part before the '-'.
            string numericPart = versionFolder.Split('-')[0];
            if (!Version.TryParse(numericPart, out Version parsedVersion))
            {
                continue;
            }

            if (newestVersion == null || parsedVersion > newestVersion)
            {
                newestVersion = parsedVersion;
                newestVersionText = versionFolder;
            }
        }

        return newestVersionText;
    }

    private static List<string> ListDirectoryNames(string directoryPath)
    {
        List<string> directoryNames = new List<string>();
        try
        {
            foreach (DirectoryInfo directory in FolderListing.ListFolders(directoryPath))
            {
                directoryNames.Add(directory.Name);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // A missing or locked folder simply has no packages to report.
        }

        return directoryNames;
    }
}
