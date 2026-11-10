namespace ShadowAgents.Tests;

internal static class PythonDetectorTestHelpers
{
    public static FileDetectionResult Detect(string pythonSource, string fileName = "agent.py")
    {
        var detector = new PythonAgentDetector();
        return detector.Detect(pythonSource, fileName);
    }

    public static DetectedAgent SingleAgent(FileDetectionResult result)
    {
        Assert.Single(result.Agents);
        return result.Agents[0];
    }

    public static DetectedAgent AgentNamed(FileDetectionResult result, string agentName)
    {
        foreach (DetectedAgent agent in result.Agents)
        {
            if (agent.Name == agentName)
            {
                return agent;
            }
        }

        string foundNames = string.Join(", ", result.Agents.Select(agent => agent.Name));
        throw new Xunit.Sdk.XunitException($"No agent named '{agentName}'. Found: {foundNames}");
    }
}
