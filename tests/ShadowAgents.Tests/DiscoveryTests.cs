namespace ShadowAgents.Tests;

public class DiscoveryTests
{
    [Fact]
    public void Requirements_file_references_frameworks_with_versions()
    {
        ManifestInfo manifest = ManifestReader.Read(@"C:\p\requirements.txt", "crewai[tools]==0.80.0\nrequests>=2\nlangchain-openai>=0.2.1\n");

        Assert.Equal(2, manifest.References.Count);
        Assert.Equal("crewai", manifest.References[0].FrameworkKey);
        Assert.Equal("0.80.0", manifest.References[0].Version);
        Assert.Equal("langchain", manifest.References[1].FrameworkKey);
        Assert.Equal("0.2.1", manifest.References[1].Version);
    }

    [Fact]
    public void Package_json_references_js_frameworks()
    {
        ManifestInfo manifest = ManifestReader.Read(@"C:\p\package.json", """{ "name": "bot", "dependencies": { "ai": "^5.0.0", "openai": "^4" } }""");

        ManifestReference reference = Assert.Single(manifest.References);
        Assert.Equal("vercel_ai_sdk", reference.FrameworkKey);
        Assert.Equal("bot", manifest.ProjectName);
    }

    [Fact]
    public void Csproj_references_dotnet_frameworks()
    {
        ManifestInfo manifest = ManifestReader.Read(@"C:\p\Bot.csproj", """<PackageReference Include="Microsoft.SemanticKernel" Version="1.30.0" />""");

        Assert.Equal("semantic_kernel_dotnet", Assert.Single(manifest.References).FrameworkKey);
    }

    [Fact]
    public void Broken_package_json_gives_no_references_but_does_not_throw()
    {
        ManifestInfo manifest = ManifestReader.Read(@"C:\p\package.json", "{ broken");

        Assert.Empty(manifest.References);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\code\bot\node_modules\ai\index.js", true)]
    [InlineData(@"C:\Users\alice\code\bot\.venv\Lib\site-packages\x.py", true)]
    [InlineData(@"C:\Users\alice\AppData\Local\Programs\Tool\app.js", true)]
    [InlineData(@"C:\Users\alice\code\bot\dist\main.js", true)]
    [InlineData(@"C:\Users\alice\code\bot\src\agent.py", false)]
    public void Skipped_folders_are_recognised_anywhere_in_a_path(string filePath, bool expectedSkipped)
    {
        Assert.Equal(expectedSkipped, SkipRules.IsInSkippedFolder(filePath));
    }

    [Theory]
    [InlineData("tests/test_agent.py", true)]
    [InlineData("src/agent.test.ts", true)]
    [InlineData("src/__tests__/agent.ts", true)]
    [InlineData("src/agent.py", false)]
    public void Test_paths_are_flagged(string relativePath, bool expectedIsTest)
    {
        Assert.Equal(expectedIsTest, SkipRules.IsTestPath(relativePath));
    }

    [Fact]
    public void Machine_wide_index_keeps_loose_sources_only_in_profiles_or_projects()
    {
        CandidateIndex index = new CandidateIndex();
        index.SourceFiles.Add(@"C:\Users\alice\Downloads\notebook.ipynb");
        index.SourceFiles.Add(@"C:\tools\bundled\helper.py");
        index.SourceFiles.Add(@"D:\repos\bot\src\agent.py");
        index.GitRoots.Add(@"D:\repos\bot");

        index.KeepOnlyProfileOrProjectSources();

        Assert.Equal(new List<string> { @"C:\Users\alice\Downloads\notebook.ipynb", @"D:\repos\bot\src\agent.py" }, index.SourceFiles);
    }

    [Fact]
    public void Walker_finds_sources_and_installed_packages_but_never_reads_dependency_folders()
    {
        using TemporaryFolder folder = new TemporaryFolder();
        folder.WriteFile("bot/pyproject.toml", "[project]\ndependencies = [\"crewai\"]");
        folder.WriteFile("bot/src/crew.py", "from crewai import Agent");
        folder.WriteFile("bot/.venv/pyvenv.cfg", "home = x");
        folder.CreateFolder("bot/.venv/Lib/site-packages/crewai-0.80.0.dist-info");
        folder.WriteFile("bot/.venv/Lib/site-packages/crewai/agent.py", "from crewai import Agent");
        folder.WriteFile("web/node_modules/ai/package.json", "{ \"version\": \"5.0.1\" }");
        folder.WriteFile("web/node_modules/ai/evil.ts", "import { Agent } from '@openai/agents'");

        CandidateIndex index = new CandidateIndex();
        new DirectoryCandidateWalker().WalkInto(folder.Path, index, CancellationToken.None);

        string onlySource = Assert.Single(index.SourceFiles);
        Assert.EndsWith(Path.Combine("bot", "src", "crew.py"), onlySource);
        Assert.Contains(index.InstalledPackages, package => package.FrameworkKey == "crewai" && package.Version == "0.80.0");
        Assert.Contains(index.InstalledPackages, package => package.FrameworkKey == "vercel_ai_sdk" && package.Version == "5.0.1");
    }

    [Fact]
    public void Project_roots_group_files_under_their_manifest_and_loose_files_by_folder()
    {
        CandidateIndex index = new CandidateIndex();
        index.SourceFiles.Add(@"C:\code\bot\src\agent.py");
        index.SourceFiles.Add(@"C:\code\bot\tools\search.py");
        index.SourceFiles.Add(@"C:\Users\a\Downloads\script.py");
        ManifestInfo manifest = ManifestReader.Read(@"C:\code\bot\requirements.txt", "crewai");

        List<ProjectRoot> roots = ProjectRootResolver.Resolve(index, new List<ManifestInfo> { manifest }, 5000);

        Assert.Equal(2, roots.Count);
        ProjectRoot botRoot = roots.Single(root => root.RootPath == @"C:\code\bot");
        Assert.Equal(2, botRoot.SourceFiles.Count);
        ProjectRoot looseRoot = roots.Single(root => root.RootPath == @"C:\Users\a\Downloads");
        Assert.True(looseRoot.IsLoose);
    }
}
