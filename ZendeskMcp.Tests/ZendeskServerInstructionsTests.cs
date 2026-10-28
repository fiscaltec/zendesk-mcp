using FluentAssertions;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

public class VitallyServerInstructionsTests
{
    [Theory]
    [InlineData("organisation")]
    [InlineData("Traits")]
    [InlineData("custom object")]
    [InlineData("List_organizations(nameContains")]
    [InlineData("createdAfter")]
    [InlineData("Read-only")]
    public void Text_ContainsKeyGuidanceMarkers(string marker)
    {
        ZendeskServerInstructions.Text.Should().Contain(marker);
    }

    [Fact]
    public void Text_IsNotEmpty()
    {
        ZendeskServerInstructions.Text.Should().NotBeNullOrWhiteSpace();
    }
}
