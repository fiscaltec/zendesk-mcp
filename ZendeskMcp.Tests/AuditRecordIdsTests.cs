using FluentAssertions;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

/// <summary>
/// The half of vitally-mcp#147's acceptance criterion that the upstream path cannot supply. A get-by-id call
/// names its record in the URL, and so do scoped lists like <c>organizations/{id}/tickets</c> — but
/// an <b>unscoped</b> list or search, which is most reads, carries the identities only in the
/// response body. Without these ids, a search returning a hundred tickets records that a hundred
/// tickets were read and names none of them.
/// </summary>
public class AuditRecordIdsTests
{
    [Fact]
    public void Extract_NamesTheRecordsInAResourceNamedEnvelope()
    {
        // Zendesk names the collection after the resource, and its ids are integers: recorded as their
        // raw JSON text, so `35101` reads back exactly as Zendesk wrote it.
        var raw = """{"tickets":[{"id":35101,"subject":"A"},{"id":35102,"subject":"B"}],"meta":{"has_more":true,"after_cursor":"c9"}}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("35101", "35102");
        result.RecordsFetched.Should().Be(2);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_ReadsTheSearchResultsEnvelope()
    {
        // Search answers `{results:[...]}` whatever the record type.
        var raw = """{"results":[{"id":501,"result_type":"user"},{"id":601,"result_type":"organization"}],"count":2,"next_page":null}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("501", "601");
        result.RecordsFetched.Should().Be(2);
    }

    [Fact]
    public void Extract_TakesTheFirstCollection_AndIgnoresTheSideloadsAfterIt()
    {
        // `?include=users` puts the requesters beside the tickets. They are context the caller did not
        // ask for by id, so recording them would claim the call read those users' records.
        var result = AuditRecordIds.Extract(TestHelpers.GetSampleTicketsJson());

        result.Ids.Should().Equal("101", "102");
        result.RecordsFetched.Should().Be(2, "the sideloaded user is not a record this call read");
        result.Ids.Should().NotContain("501");
    }

    [Fact]
    public void Extract_IgnoresPagingMetadata_EvenWhenItComesFirst()
    {
        // `meta` and `links` are never the records, wherever they sit in the object.
        var raw = """{"meta":{"has_more":false},"links":{"next":null},"users":[{"id":7001},{"id":7002}]}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("7001", "7002");
    }

    [Fact]
    public void Extract_CapsTheIdList_ButStillReportsHowManyWereRead()
    {
        // The bounded auto-pager can fetch ten pages of a hundred, so an uncapped list is ~1000 ids
        // in one record. Capping without reporting the count would under-report a bulk read:
        // "read 640 tickets, first 100 listed" is honest, "read 100" is not.
        var records = string.Join(",", Enumerable.Range(0, 640).Select(i => $$"""{"id":{{1000 + i}}}"""));
        var raw = $$"""{"tickets":[{{records}}]}""";

        var result = AuditRecordIds.Extract(raw);

        result.IdsRecorded.Should().Be(100, "the id list is capped");
        result.RecordsFetched.Should().Be(640, "the magnitude of the read is reported regardless");
        result.Ids.Should().StartWith(["1000"]).And.EndWith(["1099"]);
    }

    [Fact]
    public void Extract_ReadsABareArray()
    {
        // A few endpoints answer with a bare array rather than an envelope. Covering only the
        // envelope would leave those calls recording nothing.
        var raw = """[{"id":11},{"id":12}]""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("11", "12");
        result.RecordsFetched.Should().Be(2);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_NamesTheRecordOnAWrappedGetByIdResponse()
    {
        // A single record comes back wrapped in its resource name. The upstream path names this record
        // too, so the id is corroboration rather than the only evidence — but reporting "ids
        // unavailable" here would be a false gap, and a false gap is worse than a real one: it sends
        // whoever is reading the trail looking for a limitation that does not exist.
        var result = AuditRecordIds.Extract(TestHelpers.GetSampleTicketJson());

        result.Ids.Should().Equal("101");
        result.RecordsFetched.Should().Be(1);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_NamesTheRecordOnABareGetByIdResponse()
    {
        var result = AuditRecordIds.Extract("""{"id":42,"name":"Acme","tags":["vip"]}""");

        result.Ids.Should().Equal("42");
        result.RecordsFetched.Should().Be(1);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_StillAcceptsStringIds()
    {
        // Most Zendesk ids are integers, but some resources (custom object records, Guide media) use
        // strings. Both are ids.
        var result = AuditRecordIds.Extract("""{"custom_object_records":[{"id":"01GDXYD7ZTWYP542BA8MDDTE36"}]}""");

        result.Ids.Should().Equal("01GDXYD7ZTWYP542BA8MDDTE36");
    }

    [Fact]
    public void Extract_ReportsAnExplicitGap_WhenTheShapeYieldsNoIds()
    {
        // "Ids unavailable for this path" is a deliberate outcome. A record that silently omits them
        // looks complete while naming nobody, which is the one failure mode the acceptance criterion
        // cannot tolerate.
        var raw = """{"job_status":{"status":"queued","url":null}}""";

        var result = AuditRecordIds.Extract(raw);

        result.IdsAvailable.Should().BeFalse();
        result.Ids.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DoesNotThrow_OnAResponseThatIsNotJson()
    {
        // An audit component must never be the reason a tool call fails. Zendesk can return a
        // non-JSON body (a gateway error page, an empty 204), and `JsonDocument.Parse` throws on
        // those — so without this the trail would turn an upstream hiccup into a failed call.
        var act = () => AuditRecordIds.Extract("<html>502 Bad Gateway</html>");

        act.Should().NotThrow();
        AuditRecordIds.Extract("<html>502 Bad Gateway</html>").IdsAvailable.Should().BeFalse();
        AuditRecordIds.Extract("").IdsAvailable.Should().BeFalse("an empty 204 names nobody");
    }

    [Fact]
    public void Extract_ReportsAGap_WhenTheArrayHasRecordsButNoneCarriesAnId()
    {
        // Distinct from an empty result set, which is a complete answer with nothing in it. A
        // non-empty array whose elements have no `id` is a genuine gap: records were read and none
        // can be named. Reporting it as available makes CallsWithoutIds say there was no gap.
        var result = AuditRecordIds.Extract("""{"tickets":[{"subject":"A"},{"subject":"B"}]}""");

        result.RecordsFetched.Should().Be(2, "two records really were read");
        result.IdsAvailable.Should().BeFalse("none of them could be named");
    }

    [Fact]
    public void Extract_TreatsAnEmptyResultSetAsAvailable_NotAsAGap()
    {
        // The counterpart. An empty list is a complete answer, not a failure to read ids, and
        // flagging it would make every "no matches" search look like a broken audit record.
        var result = AuditRecordIds.Extract("""{"results":[],"count":0}""");

        result.RecordsFetched.Should().Be(0);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_DoesNotThrow_WhenAnIdIsNeitherANumberNorAString()
    {
        // `JsonElement.GetString()` throws on a non-string element, and extraction runs inside
        // `ZendeskService.SendAsync` — so an odd id in an otherwise successful response would turn a
        // working tool call into an error. Numbers and strings are ids; anything else is skipped.
        const string raw = """{"tickets":[{"id":123},{"id":"abc"},{"id":true},{"id":null},{"id":{"x":1}}]}""";
        var act = () => AuditRecordIds.Extract(raw);

        act.Should().NotThrow();

        var result = AuditRecordIds.Extract(raw);
        result.Ids.Should().Equal(["123", "abc"], "only the ids that are actually ids are recorded");
        result.RecordsFetched.Should().Be(5, "five records were still read");
        result.IdsAvailable.Should().BeTrue("some of them could be named");
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void FromMutationUrl_NamesANumericLastSegment(string method)
    {
        // Zendesk answers deletes, and some updates, with an empty body — so the URL is the only place
        // the record is named, on exactly the events an access trail is kept for.
        var result = AuditRecordIds.FromMutationUrl(new HttpMethod(method), "https://acme.zendesk.com/api/v2/tickets/35101");

        result.Ids.Should().Equal("35101");
        result.RecordsFetched.Should().Be(1);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void FromMutationUrl_StripsAJsonSuffix_AndIgnoresTheQuery()
    {
        var result = AuditRecordIds.FromMutationUrl(HttpMethod.Delete, "https://acme.zendesk.com/api/v2/tickets/35101.json?async=true");

        result.Ids.Should().Equal("35101");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public void FromMutationUrl_IgnoresMethodsWhoseUrlDoesNotEndInTheRecord(string method)
    {
        // A GET's record is in its body; a POST's URL names the collection it creates into.
        AuditRecordIds.FromMutationUrl(new HttpMethod(method), "https://acme.zendesk.com/api/v2/tickets/35101")
            .IdsAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://acme.zendesk.com/api/v2/help_center/articles/360001/translations/en-gb")] // a locale
    [InlineData("https://acme.zendesk.com/api/v2/tickets/update_many.json")]                       // an action
    [InlineData("https://acme.zendesk.com/api/v2/tickets")]                                        // a collection
    [InlineData("https://acme.zendesk.com/api/v2/custom_objects/acme_contract")]                   // a key, not an id
    [InlineData("not a url")]
    public void FromMutationUrl_NeverInventsAnId(string url)
    {
        // A fabricated identifier is far worse than an honest gap: it resolves to nothing and cannot be
        // told apart from a real one.
        var result = AuditRecordIds.FromMutationUrl(HttpMethod.Put, url);

        result.IdsAvailable.Should().BeFalse();
        result.Ids.Should().BeEmpty();
    }
}
