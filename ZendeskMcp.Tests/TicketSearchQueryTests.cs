using FluentAssertions;

namespace ZendeskMcp.Tests;

/// <summary>
/// The shared ticket filters: each becomes exactly one validated Zendesk search term, and free text
/// can only ever match words — never widen or replace a filter.
/// </summary>
public class TicketSearchQueryTests
{
    [Fact]
    public void NoFilters_SearchesAllTickets()
    {
        TicketSearchQuery.Build(new TicketSearchFilters()).Should().Be("type:ticket");
    }

    [Fact]
    public void Filters_BecomeOneTermEach()
    {
        var query = TicketSearchQuery.Build(new TicketSearchFilters
        {
            Status = "Open",
            Priority = "high, urgent",
            TicketType = "problem",
            Assignee = "me",
            Requester = "1234567890",
            Organization = "Acme Corp",
            Group = "20663166",
            Brand = "Main",
            Form = "Default ticket form",
            IncludeTags = "bug,Urgent",
            ExcludeTags = "spam",
            CustomFieldId = 360001,
            CustomFieldValue = "two words",
            CustomStatusId = 77,
            HasAttachments = true,
            StartDate = "2026-01-01",
            EndDate = "2026-01-31T23:59:59Z",
            SolvedStartDate = "2026-02-01",
        });

        query.Should().Be(
            "type:ticket status:open priority:high priority:urgent ticket_type:problem assignee:me " +
            "requester_id:1234567890 organization:\"Acme Corp\" group_id:20663166 brand:Main " +
            "form:\"Default ticket form\" tags:bug tags:urgent -tags:spam custom_field_360001:\"two words\" " +
            "custom_status_id:77 has_attachment:true created>=2026-01-01 created<=2026-01-31T23:59:59Z solved>=2026-02-01");
    }

    [Fact]
    public void FreeText_CannotCarrySearchSyntax()
    {
        var query = TicketSearchQuery.Build(new TicketSearchFilters { Text = "login status:solved -tags:vip \"quoted\" a*" });

        query.Should().Be("type:ticket login \"status:solved\" \"-tags:vip\" quoted \"a*\"");
    }

    [Fact]
    public void Names_DropQuotes_SoTheyCannotBreakOutOfTheTerm()
    {
        TicketSearchQuery.Build(new TicketSearchFilters { Assignee = "Susan\" status:solved \"Warren" })
            .Should().Be("type:ticket assignee:\"Susan status:solved Warren\"");
    }

    [Theory]
    [InlineData("status", "archived")]
    [InlineData("priority", "critical")]
    [InlineData("ticket_type", "bug")]
    public void UnknownEnumValues_AreRefused(string parameter, string value)
    {
        var filters = parameter switch
        {
            "status" => new TicketSearchFilters { Status = value },
            "priority" => new TicketSearchFilters { Priority = value },
            _ => new TicketSearchFilters { TicketType = value },
        };

        var act = () => TicketSearchQuery.Build(filters);

        act.Should().Throw<ArgumentException>().WithMessage($"*{parameter}*");
    }

    [Fact]
    public void ACustomField_NeedsBothHalves()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { CustomFieldId = 1 });

        act.Should().Throw<ArgumentException>().WithMessage("*custom_field_id and custom_field_value*");
    }

    [Theory]
    [InlineData("31/01/2026")]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    public void BadDates_AreRefused(string date)
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { StartDate = date });

        act.Should().Throw<ArgumentException>().WithMessage("*start_date*");
    }

    [Fact]
    public void TagsWithSpaces_AreRefused()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { IncludeTags = "two words" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TextOverTheLimit_IsRefused()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { Text = new string('a', 501) });

        act.Should().Throw<ArgumentException>().WithMessage("*500*");
    }

    [Fact]
    public void TheAgentSearchUrl_EscapesTheQuery()
    {
        TicketSearchQuery.AgentSearchUrl("https://acme.zendesk.com", "type:ticket status:open")
            .Should().Be("https://acme.zendesk.com/agent/search/1?type=ticket&q=type%3Aticket%20status%3Aopen");
    }
}
