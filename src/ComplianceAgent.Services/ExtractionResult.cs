namespace ComplianceAgent.Services;

public class ExtractionResult
{
    public bool Success { get; init; }
    public string Json { get; init; } = string.Empty;
    public string? Error { get; init; }
}
