using FluentAssertions;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

public class ZendeskServerInstructionsTests
{
    [Theory]
    [InlineData("connect link")]
    [InlineData("Get_zendesk_connection")]
    [InlineData("untrusted data")]
    [InlineData("Search_tickets")]
    [InlineData("Preview_macro")]
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
