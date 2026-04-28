using Microsoft.Extensions.Configuration;
using Xunit;

public class PromptLibraryTests
{
    [Fact]
    public void BuildExtractionPrompt_UsesConfiguredVersionAndContainsGuardrails()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:PromptVersion"] = "v1"
            })
            .Build();

        var library = new VersionedPromptLibrary(config);
        var package = library.BuildExtractionPrompt("ABC GmbH entered a financing agreement in Germany.");

        Assert.Equal("v1", package.Version);
        Assert.Contains("Do NOT infer, assume, enrich, normalize, or guess missing values.", package.Prompt);
        Assert.Contains("Only valid JSON.", package.Prompt);
        Assert.Contains("\"transactionType\": null", package.Prompt);
        Assert.Contains("ABC GmbH entered a financing agreement in Germany.", package.Prompt);
    }

    [Fact]
    public void BuildExtractionPrompt_FallsBackToV1WhenVersionUnknown()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:PromptVersion"] = "v999"
            })
            .Build();

        var library = new VersionedPromptLibrary(config);

        Assert.Equal("v1", library.ActiveVersion);
        Assert.Contains("v1", library.AvailableVersions);
    }
}
