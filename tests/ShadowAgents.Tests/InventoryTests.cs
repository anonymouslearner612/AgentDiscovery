namespace ShadowAgents.Tests;

public class InventoryTests
{
    [Fact]
    public void Only_new_changed_and_removed_findings_are_reported()
    {
        AgentInventory inventory = new AgentInventory();
        FindingRecord first = RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py");
        FindingRecord second = RuntimeTests.AgentRecord("a2", @"C:\code\bot", "other.py");
        List<string> scope = new List<string> { @"C:\code\bot" };

        List<InventoryChange> firstScan = inventory.ApplyScanResults(new List<FindingRecord> { first, second }, scope);
        Assert.Equal(2, firstScan.Count);
        Assert.All(firstScan, change => Assert.Equal(InventoryChange.New, change.Change));

        // Same content again, only a later "lastSeen": nothing to send.
        FindingRecord sameAgain = RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py");
        sameAgain.LastSeen = sameAgain.LastSeen.AddDays(1);
        FindingRecord changedTools = RuntimeTests.AgentRecord("a2", @"C:\code\bot", "other.py");
        changedTools.Agent.Tools.Add("send_email");

        List<InventoryChange> secondScan = inventory.ApplyScanResults(new List<FindingRecord> { sameAgain, changedTools }, scope);
        InventoryChange onlyChange = Assert.Single(secondScan);
        Assert.Equal(InventoryChange.Changed, onlyChange.Change);
        Assert.Equal("a2", onlyChange.Record.Id);

        // a2's file was deleted.
        List<InventoryChange> thirdScan = inventory.ApplyScanResults(new List<FindingRecord> { sameAgain }, scope);
        Assert.Equal(InventoryChange.Removed, Assert.Single(thirdScan).Change);
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public void Findings_outside_the_scanned_scope_are_kept()
    {
        AgentInventory inventory = new AgentInventory();
        inventory.ApplyScanResults(new List<FindingRecord> { RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py") }, new List<string>());

        List<InventoryChange> changes = inventory.ApplyScanResults(new List<FindingRecord>(), new List<string> { @"C:\code\other" });

        Assert.Empty(changes);
        Assert.Equal(1, inventory.Count);
    }

    [Fact]
    public void Scope_does_not_match_a_folder_with_the_same_prefix()
    {
        Assert.True(AgentInventory.IsSameOrInside(@"C:\code\bot\src", @"C:\code\bot"));
        Assert.False(AgentInventory.IsSameOrInside(@"C:\code\bot2", @"C:\code\bot"));
        Assert.True(AgentInventory.IsSameOrInside(@"C:\code", @"C:\"));
    }

    [Fact]
    public void Rescan_keeps_running_state_from_the_watcher()
    {
        AgentInventory inventory = new AgentInventory();
        List<string> scope = new List<string> { @"C:\code\bot" };
        inventory.ApplyScanResults(new List<FindingRecord> { RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py") }, scope);
        inventory.MarkRunning(@"C:\code\bot", "python.exe (pid 1)", DateTimeOffset.UtcNow);

        inventory.ApplyScanResults(new List<FindingRecord> { RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py") }, scope);

        FindingRecord record = inventory.Find("a1")!;
        Assert.True(record.Runtime.Running);
        Assert.Equal(FindingStates.Running, record.State);
    }

    [Fact]
    public void Inventory_survives_a_save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), "inventory-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AgentInventory inventory = new AgentInventory();
            inventory.ApplyScanResults(new List<FindingRecord> { RuntimeTests.AgentRecord("a1", @"C:\code\bot", "main.py") }, new List<string>());
            inventory.Save(path);

            AgentInventory loaded = AgentInventory.Load(path);

            Assert.Equal("agent-a1", loaded.Find("a1")!.Agent.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
