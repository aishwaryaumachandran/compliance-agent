using Xunit;

public class ExtractionResponseParserTests
{
    [Fact]
    public void TryParseDraftFromResponse_ParsesJsonInsideWrapperText()
    {
        var response = "Result:\n```json\n{\n  \"arrangementId\": null,\n  \"country\": \"Germany\",\n  \"entities\": [\"ABC GmbH\"],\n  \"description\": \"Financing agreement\",\n  \"transactionType\": null,\n  \"status\": \"draft\"\n}\n```";

        var ok = ExtractionResponseParser.TryParseDraftFromResponse(response, out var draft);

        Assert.True(ok);
        Assert.NotNull(draft);
        Assert.Equal("Germany", draft!.Country);
        Assert.Single(draft.Entities);
        Assert.Equal("draft", draft.Status);
    }

    [Fact]
    public void TryParseDraftFromResponse_ReturnsFalseForInvalidJson()
    {
        var ok = ExtractionResponseParser.TryParseDraftFromResponse("not-json", out var draft);

        Assert.False(ok);
        Assert.Null(draft);
    }
}
