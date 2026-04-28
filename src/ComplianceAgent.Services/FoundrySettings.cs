namespace ComplianceAgent.Services;

public class FoundrySettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.2";
    public string AgentName { get; set; } = "compliance-agent-backend";
}
