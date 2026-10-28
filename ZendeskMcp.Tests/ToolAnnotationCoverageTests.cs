using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tests;

/// <summary>
/// Reflection sweep over every [McpServerTool] method, asserting the four annotation hints are set
/// consistently with the tool's name prefix. This is enforcement rather than documentation: a new
/// tool added without annotations fails here instead of shipping with misleading retry semantics.
/// </summary>
public class ToolAnnotationCoverageTests
{
    private static IEnumerable<(string Name, McpServerToolAttribute Attr)> AllTools()
    {
        var assembly = typeof(ZendeskService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (attr.Name ?? method.Name, attr);
                }
            }
        }
    }

    /// <summary>
    /// Same sweep as <see cref="AllTools"/>, but pairs each tool with the raw <see cref="CustomAttributeData"/>
    /// for its <see cref="McpServerToolAttribute"/> usage. <c>ReadOnly</c>/<c>Destructive</c>/<c>Idempotent</c>/
    /// <c>OpenWorld</c> are plain <c>bool</c> (not <c>bool?</c>), so an unset hint is indistinguishable from an
    /// explicit <c>false</c> once the attribute is instantiated — <c>NamedArguments</c> is the only way to see
    /// which properties the source actually assigned.
    /// </summary>
    private static IEnumerable<(string Name, McpServerToolAttribute Attr, CustomAttributeData Data)> AllToolsWithAttributeData()
    {
        var assembly = typeof(ZendeskService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    var data = method.GetCustomAttributesData().First(d => d.AttributeType == typeof(McpServerToolAttribute));
                    yield return (attr.Name ?? method.Name, attr, data);
                }
            }
        }
    }

    [Fact]
    public void EveryToolIsDiscovered()
    {
        // No count: the tool set grows as tools are added. A floor of one plus a known tool is what
        // tells "discovery broke" apart from "nothing to check", which would pass every sweep below.
        AllTools().Should().NotBeEmpty("a sweep over no tools proves nothing");
        AllTools().Select(t => t.Name).Should().Contain("Get_zendesk_connection");
    }

    [Theory]
    [InlineData("List_")]
    [InlineData("Get_")]
    [InlineData("Search_")]
    [InlineData("Count_")]
    [InlineData("Find_")]
    [InlineData("Preview_")]
    [InlineData("Export_")]
    public void ReadTools_AreReadOnlyIdempotentAndClosedWorld(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, $"No {prefix}* tools are registered yet.");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(true, $"{name} only reads");
            attr.Destructive.Should().Be(false, $"{name} only reads");
            attr.Idempotent.Should().Be(true, $"{name} is safe to repeat");
            attr.OpenWorld.Should().Be(false, $"{name} addresses one Zendesk account");
        }
    }

    [Fact]
    public void CreateTools_AreDestructiveAndNotIdempotent()
    {
        var tools = AllTools().Where(t => t.Name.StartsWith("Create_", StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, "No Create_* tools are registered yet.");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(false, $"repeating {name} would create a second record");
            attr.OpenWorld.Should().Be(false);
        }
    }

    [Theory]
    [InlineData("Update_")]
    // v1 has no delete tools, so this case skips until one is added; it is kept so the first one is
    // checked on arrival.
    [InlineData("Delete_")]
    public void UpdateAndDeleteTools_AreDestructiveAndIdempotent(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, $"No {prefix}* tools are registered yet.");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(true, $"repeating {name} lands the same final state");
            attr.OpenWorld.Should().Be(false);
        }
    }

    /// <summary>
    /// The writes named outside Create_/Update_. Idempotency is per tool here and deliberately not
    /// asserted: setting a macro active twice lands the same state, applying a macro twice may add
    /// a second comment, and requesting an upload slot twice mints two.
    /// </summary>
    [Theory]
    [InlineData("Request_")]
    [InlineData("Apply_")]
    [InlineData("Set_")]
    [InlineData("Add_")]
    public void OtherWriteTools_AreDestructiveAndClosedWorld(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, $"No {prefix}* tools are registered yet.");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.OpenWorld.Should().Be(false, $"{name} addresses one Zendesk account");
        }
    }

    /// <summary>
    /// Makes the invariant prefix-independent: unlike the tests above, this runs over every tool
    /// regardless of name, so a future tool named outside the prefixes above (e.g. a
    /// hypothetical Sync_*) cannot slip through with unset or contradictory hints. Two checks:
    /// (a) all four hints are explicitly assigned in the attribute usage — not merely defaulted to
    /// false by the CLR because the property was never mentioned — and (b) the repo's ReadOnly/Destructive
    /// convention documented in CLAUDE.md holds: a tool is either read-only or destructive, never both
    /// and never neither.
    /// </summary>
    [Fact]
    public void EveryTool_HasAllFourHintsExplicitlySetAndConsistentReadOnlyDestructive()
    {
        foreach (var (name, attr, data) in AllToolsWithAttributeData())
        {
            var setProperties = data.NamedArguments!.Select(a => a.MemberName).ToHashSet(StringComparer.Ordinal);

            setProperties.Should().Contain("ReadOnly", $"{name} must explicitly set ReadOnly");
            setProperties.Should().Contain("Destructive", $"{name} must explicitly set Destructive");
            setProperties.Should().Contain("Idempotent", $"{name} must explicitly set Idempotent");
            setProperties.Should().Contain("OpenWorld", $"{name} must explicitly set OpenWorld");

            if (attr.ReadOnly)
            {
                attr.Destructive.Should().BeFalse($"{name} is ReadOnly, so per the repo convention it must not also be Destructive");
            }
            else
            {
                attr.Destructive.Should().BeTrue($"{name} mutates (ReadOnly=false), so per the repo convention it must be Destructive");
            }
        }
    }
}
