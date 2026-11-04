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

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    public void ACustomFieldValue_ThatIsOnlyQuotes_IsRefused(string value)
    {
        // Quote drops quote characters, so this would otherwise send custom_field_1: with no operand.
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { CustomFieldId = 1, CustomFieldValue = value });

        act.Should().Throw<ArgumentException>().WithMessage("*custom_field_id and custom_field_value*");
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
    public void Channels_BecomeViaTerms_InTheChannelTablesOrder_WithEmailAsMail()
    {
        TicketSearchQuery.Build(new TicketSearchFilters { Channels = ["WEB", " email ", "web"] })
            .Should().Be("type:ticket via:mail via:web");
    }

    [Fact]
    public void Messaging_ExpandsToEveryMessagingChannel_WithoutRepeatingOneAlsoNamed()
    {
        // The set Zendesk accepted as one search on 2026-11-04 (through Swifteq's server).
        TicketSearchQuery.Build(new TicketSearchFilters { Channels = ["messaging", "whatsapp", "email"] })
            .Should().Be("type:ticket via:mail via:whatsapp via:native_messaging via:line via:wechat via:instagram_dm via:telegram via:viber via:kakaotalk via:apple_business_chat via:google_rcs via:google_business_messages via:sunshine_conversations_facebook_messenger via:sunshine_conversations_twitter_dm via:twilio_sms via:messagebird_sms via:sunshine_conversations_api");
    }

    [Fact]
    public void EveryChannelValue_IsAcceptedAndMapsToATerm()
    {
        // Every value but messaging is a group of Count_tickets group_by channel, so a filter selects
        // exactly that group's tickets: the term is the one the breakdown counts.
        foreach (var (channel, searchValue) in ZendeskMcp.Tools.TicketCountHelpers.Channels)
        {
            TicketSearchQuery.Build(new TicketSearchFilters { Channels = [channel] })
                .Should().Be($"type:ticket via:{searchValue}");
        }

        TicketSearchQuery.ChannelValues.Should().Contain("messaging");
    }

    [Fact]
    public void TheChannelDescription_ListsExactlyTheValuesTheFilterTakes()
    {
        // The description the model reads is written by hand; this keeps it in step with the table.
        var listed = ZendeskMcp.Tools.TicketFilterDescriptions.Channel
            .Split(':', 2)[1].Split('.', 2)[0]
            .Replace(" or ", ", ", StringComparison.Ordinal)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        listed.Should().BeEquivalentTo(TicketSearchQuery.ChannelValues);
    }

    [Theory]
    [InlineData("instagram_dm")]
    [InlineData("via:mail")]
    [InlineData("")]
    public void UnknownChannels_AreRefused(string channel)
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { Channels = [channel] });

        act.Should().Throw<ArgumentException>().WithMessage("*channel values must be from*never 'via:' in text*");
    }

    [Fact]
    public void ViaInText_StaysAWord_NotAChannelFilter()
    {
        TicketSearchQuery.Build(new TicketSearchFilters { Text = "via:mail" })
            .Should().Be("type:ticket \"via:mail\"");
    }

    [Fact]
    public void CustomFieldConditions_BecomeOneTermEach()
    {
        var query = TicketSearchQuery.Build(new TicketSearchFilters
        {
            CustomFieldId = 1,
            CustomFieldValue = "a",
            CustomFields =
            [
                new TicketCustomFieldCondition(2, Value: "two words"),
                new TicketCustomFieldCondition(3, HasValue: false),
                new TicketCustomFieldCondition(4, HasValue: true),
            ],
        });

        query.Should().Be("type:ticket custom_field_1:a custom_field_2:\"two words\" -custom_field_3:* custom_field_4:*");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("x", false)]
    [InlineData("  ", null)]
    [InlineData("  ", true)]
    [InlineData("\"\"", null)]
    [InlineData("\" \"", null)]
    public void ACustomFieldCondition_NeedsExactlyOneOfValueAndHasValue(string? value, bool? hasValue)
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = [new TicketCustomFieldCondition(5, value, hasValue)] });

        act.Should().Throw<ArgumentException>().WithMessage("*exactly one of value*has_value*");
    }

    [Fact]
    public void AFieldNamedTwice_IsRefused_BecauseZendeskWouldOrTheConditions()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters
        {
            CustomFields = [new TicketCustomFieldCondition(5, Value: "a"), new TicketCustomFieldCondition(5, Value: "b")],
        });

        act.Should().Throw<ArgumentException>().WithMessage("*more than once*");
    }

    [Fact]
    public void AFieldAlsoGivenAsCustomFieldId_IsRefused()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters
        {
            CustomFieldId = 5,
            CustomFieldValue = "a",
            CustomFields = [new TicketCustomFieldCondition(5, HasValue: true)],
        });

        act.Should().Throw<ArgumentException>().WithMessage("*already filtered by custom_field_id*");
    }

    [Fact]
    public void MoreThanTenConditions_AreRefused_AndTenAreAccepted()
    {
        TicketCustomFieldCondition[] Conditions(int count) =>
            Enumerable.Range(1, count).Select(i => new TicketCustomFieldCondition(i, HasValue: true)).ToArray();

        TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = Conditions(10) }).Should().Contain("custom_field_10:*");
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = Conditions(11) });
        act.Should().Throw<ArgumentException>().WithMessage("*at most 10*");
    }

    [Fact]
    public void ANonPositiveFieldId_IsRefused()
    {
        var act = () => TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = [new TicketCustomFieldCondition(0, Value: "a")] });

        act.Should().Throw<ArgumentException>().WithMessage("*positive ticket field id*");
    }

    [Fact]
    public void ACustomFieldValue_DropsQuotes_SoItCannotBreakOutOfTheTerm()
    {
        TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = [new TicketCustomFieldCondition(5, Value: "a\" status:solved \"b")] })
            .Should().Be("type:ticket custom_field_5:\"a status:solved b\"");
    }

    [Theory]
    [InlineData("*", "\"*\"")]
    [InlineData("a*", "\"a*\"")]
    [InlineData("-x", "\"-x\"")]
    [InlineData("<5", "\"<5\"")]
    [InlineData("10:30", "\"10:30\"")]
    [InlineData("plain", "plain")]
    public void ACustomFieldValue_IsExact_SoSearchSyntaxIsQuoted(string value, string expected)
    {
        TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = [new TicketCustomFieldCondition(5, Value: value)] })
            .Should().Be($"type:ticket custom_field_5:{expected}");
    }

    [Theory]
    [InlineData("*", "\"*\"")]
    [InlineData("-x", "\"-x\"")]
    [InlineData("<5", "\"<5\"")]
    [InlineData("plain", "plain")]
    public void CustomFieldValue_QuotesSearchSyntax_AsCustomFieldsDoes(string value, string expected)
    {
        // Both routes to one filter build the same term.
        TicketSearchQuery.Build(new TicketSearchFilters { CustomFieldId = 5, CustomFieldValue = value })
            .Should().Be($"type:ticket custom_field_5:{expected}");
        TicketSearchQuery.Build(new TicketSearchFilters { CustomFields = [new TicketCustomFieldCondition(5, Value: value)] })
            .Should().Be($"type:ticket custom_field_5:{expected}");
    }

    [Fact]
    public void TheAgentSearchUrl_EscapesTheQuery()
    {
        TicketSearchQuery.AgentSearchUrl("https://acme.zendesk.com", "type:ticket status:open")
            .Should().Be("https://acme.zendesk.com/agent/search/1?type=ticket&q=type%3Aticket%20status%3Aopen");
    }
}
