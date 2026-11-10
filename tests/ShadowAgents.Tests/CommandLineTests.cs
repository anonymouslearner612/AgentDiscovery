namespace ShadowAgents.Tests;

public class CommandLineTests
{
    [Fact]
    public void Results_are_saved_to_a_timestamped_file_in_the_results_folder()
    {
        string outputPath = Program.DefaultOutputPath();

        Assert.StartsWith(Program.DefaultResultsFolder(), outputPath);
        Assert.Contains(Path.DirectorySeparatorChar + "scan-", outputPath);
        Assert.EndsWith(".jsonl", outputPath);
    }
}
