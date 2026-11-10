namespace ShadowAgents.Tests;

public class ContinuousModeTests
{
    private static readonly DateTimeOffset StartTime = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Batcher_waits_until_a_root_is_quiet()
    {
        ChangeBatcher batcher = new ChangeBatcher();
        TimeSpan debounce = TimeSpan.FromSeconds(60);

        batcher.AddChange(@"C:\code\bot", StartTime);
        batcher.AddChange(@"C:\code\bot", StartTime.AddSeconds(50)); // a save storm restarts the wait

        Assert.Empty(batcher.TakeQuietRoots(StartTime.AddSeconds(100), debounce));
        Assert.Equal(new List<string> { @"C:\code\bot" }, batcher.TakeQuietRoots(StartTime.AddSeconds(111), debounce));
        Assert.Empty(batcher.TakeQuietRoots(StartTime.AddSeconds(200), debounce)); // taken only once
    }

    [Fact]
    public void Batcher_treats_root_paths_case_insensitively()
    {
        ChangeBatcher batcher = new ChangeBatcher();
        batcher.AddChange(@"C:\Code\Bot", StartTime);
        batcher.AddChange(@"c:\code\bot", StartTime);

        Assert.Equal(1, batcher.PendingCount);
    }

    [Fact]
    public void Schedule_fires_once_per_occurrence()
    {
        ScanSchedule schedule = new ScanSchedule("*/5 * * * *", StartTime);

        Assert.False(schedule.TakeIfDue(StartTime.AddMinutes(1)));
        Assert.True(schedule.TakeIfDue(StartTime.AddMinutes(5)));
        Assert.False(schedule.TakeIfDue(StartTime.AddMinutes(6)));
        Assert.True(schedule.TakeIfDue(StartTime.AddMinutes(10)));
    }

    [Fact]
    public void Root_finder_walks_up_to_the_nearest_manifest_or_git_folder()
    {
        string baseFolder = Path.Combine(Path.GetTempPath(), "shadow-agents-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(baseFolder, "repo", ".git"));
            Directory.CreateDirectory(Path.Combine(baseFolder, "repo", "services", "bot", "src"));
            File.WriteAllText(Path.Combine(baseFolder, "repo", "services", "bot", "pyproject.toml"), "");
            Directory.CreateDirectory(Path.Combine(baseFolder, "repo", "scripts"));
            Directory.CreateDirectory(Path.Combine(baseFolder, "loose"));

            string inBot = ProjectRootFinder.FindRootFor(Path.Combine(baseFolder, "repo", "services", "bot", "src", "agent.py"));
            string inScripts = ProjectRootFinder.FindRootFor(Path.Combine(baseFolder, "repo", "scripts", "run.py"));

            Assert.Equal(Path.Combine(baseFolder, "repo", "services", "bot"), inBot);
            Assert.Equal(Path.Combine(baseFolder, "repo"), inScripts);
        }
        finally
        {
            Directory.Delete(baseFolder, recursive: true);
        }
    }
}
