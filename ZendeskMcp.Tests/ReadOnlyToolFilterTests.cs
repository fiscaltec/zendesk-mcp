using FluentAssertions;
using ModelContextProtocol.Protocol;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

public class ReadOnlyToolFilterTests
{
    private static Tool ReadTool() => new() { Name = "Get_thing", Annotations = new ToolAnnotations { ReadOnlyHint = true } };
    private static Tool DeleteTool() => new() { Name = "Delete_thing", Annotations = new ToolAnnotations { DestructiveHint = true } };
    private static Tool CreateTool() => new() { Name = "Create_thing" }; // no annotations

    [Fact]
    public void ReadOnly_KeepsOnlyReadHintedTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Get_thing");
    }

    [Fact]
    public void NotReadOnly_KeepsAllTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: false);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void ReadOnly_DropsToolsWithNullAnnotations()
    {
        var tools = new[] { CreateTool() }; // null annotations -> not read-only -> dropped

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ReadOnly_KeepsDisconnectZendesk()
    {
        // Destructive in annotation, but it only touches the caller's own connection.
        var disconnect = new Tool { Name = "Disconnect_zendesk", Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true } };
        var tools = new[] { ReadTool(), DeleteTool(), disconnect };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Select(t => t.Name).Should().BeEquivalentTo(["Get_thing", "Disconnect_zendesk"]);
    }

    [Fact]
    public void OwnConnectionTools_AreExactlyTheReadTierMutations()
    {
        // The filter's exemption and the catalogue's read-tier exception must name the same tools:
        // a new read-tier mutation is not exempt from read-only mode unless someone decides it is.
        ReadOnlyToolFilter.OwnConnectionTools.Should().BeEquivalentTo(ToolCatalogue.ReadTierMutations);
    }
}
