using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace ZendeskMcp.Tests;

/// <summary>
/// Reflection sweep asserting that <b>every</b> tool carries exactly one <c>[Authorize]</c> policy,
/// and that the policy agrees with the tool's own <c>ReadOnly</c> annotation.
///
/// <para>
/// This is prefix-independent on purpose. Without it a future tool could ship with no
/// <c>[Authorize]</c> at all and be silently advertised to every caller in <c>tools/list</c>,
/// including readers who cannot invoke it — a security-relevant regression that no other test in the
/// suite would catch. It is discovery hygiene rather than the security boundary itself (that remains
/// <c>ZendeskService.SendAsync</c>), but a wrong or missing policy here is exactly what makes the
/// advertised list and the enforced tier disagree.
/// </para>
/// </summary>
public class ToolAuthorizePolicyCoverageTests
{
    private const string ReadPolicy = ToolCatalogue.ReadPolicy;
    private const string WritePolicy = ToolCatalogue.WritePolicy;
    private const string DeletePolicy = ToolCatalogue.DeletePolicy;

    /// <summary>Name prefixes that only ever read, so must be discoverable with the read tier alone.</summary>
    public static TheoryData<string> ReadPrefixes => new() { "List_", "Get_", "Search_", "Count_", "Find_", "Preview_", "Export_" };

    /// <summary>
    /// Name prefixes that write through POST/PUT. <c>Apply_</c>, <c>Add_</c>, <c>Set_</c> and
    /// <c>Request_</c> are the ones a Create_/Update_ check would miss — <c>Apply_macro</c> changes a
    /// ticket and <c>Request_*_upload</c> reserves an upload, so each must stay out of a reader's list.
    /// </summary>
    public static TheoryData<string> WritePrefixes => new() { "Create_", "Update_", "Apply_", "Add_", "Set_", "Request_" };

    [Fact]
    public void EveryTool_CarriesExactlyOneAuthorizePolicyConsistentWithItsAnnotations()
    {
        foreach (var tool in ToolCatalogue.Production())
        {
            var (name, attr, method) = (tool.Name, tool.Attribute, tool.Method);

            // CustomAttributeData rather than the attribute instance: it is the only way to see the
            // policy exactly as the source assigned it, and to count the usages on this member
            // without inheritance folding two declarations into one.
            var authorizeUsages = method.GetCustomAttributesData()
                .Where(d => d.AttributeType == typeof(AuthorizeAttribute))
                .ToList();

            authorizeUsages.Should().HaveCount(1,
                $"{name} must carry exactly one [Authorize] attribute so tools/list filtering has an unambiguous policy");

            // Ends in `?? throw` rather than asserting NotBeNull afterwards. A FluentAssertions
            // check does not narrow nullability, so every `policy.Should()…` below remained a
            // possibly-null receiver to static analysis (CodeQL cs/dereferenced-value-may-be-null).
            // Throwing here removes the nullable local altogether and still names the real problem:
            // a bare [Authorize] with no policy given either as a named argument or positionally.
            var policy = authorizeUsages[0].NamedArguments
                .Where(a => a.MemberName == nameof(AuthorizeAttribute.Policy))
                .Select(a => a.TypedValue.Value as string)
                .FirstOrDefault()
                // A positional [Authorize("policy")] also sets Policy, via the constructor.
                ?? authorizeUsages[0].ConstructorArguments.FirstOrDefault().Value as string
                ?? throw new InvalidOperationException(
                    $"{name} must name a policy on its [Authorize] attribute, not just [Authorize].");

            policy.Should().BeOneOf([ReadPolicy, WritePolicy, DeletePolicy],
                $"{name} must use one of the three registered zendesk:* policies");

            if (attr.ReadOnly)
            {
                policy.Should().Be(ReadPolicy,
                    $"{name} is ReadOnly=true, so a reader-tier caller must be allowed to discover it");
            }
            else if (ToolCatalogue.ReadTierMutations.Contains(name))
            {
                policy.Should().Be(ReadPolicy,
                    $"{name} changes only the caller's own connection, so it is deliberately in the read tier");
            }
            else
            {
                policy.Should().BeOneOf([WritePolicy, DeletePolicy],
                    $"{name} mutates (ReadOnly=false), so it must not be discoverable with only {ReadPolicy}");
            }
        }
    }

    [Fact]
    public void TheReadTierExceptions_AreRealTools()
    {
        // An exception list that outlives its tool would quietly widen the rule above for whatever is
        // next given that name.
        var names = ToolCatalogue.Production().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        ToolCatalogue.ReadTierMutations.Should().OnlyContain(n => names.Contains(n));
    }

    /// <summary>
    /// Guards the split itself, by name rather than by count: the tool set grows as tools are added,
    /// so a hard-coded total would fail on every addition and be "fixed" by editing the number. A
    /// tool silently changing tier (a write re-labelled as a read) still fails here, because its
    /// prefix says what its verb is.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadPrefixes))]
    public void ReadPrefixedTools_AreInTheReadTier(string prefix)
    {
        var tools = ToolCatalogue.Production().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, $"No {prefix}* tools are registered yet.");

        tools.Should().OnlyContain(t => t.Policy == ReadPolicy, $"{prefix}* tools only issue GETs");
    }

    [Theory]
    [MemberData(nameof(WritePrefixes))]
    public void WritePrefixedTools_AreInTheWriteTier(string prefix)
    {
        var tools = ToolCatalogue.Production().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, $"No {prefix}* tools are registered yet.");

        tools.Should().OnlyContain(t => t.Policy == WritePolicy, $"{prefix}* tools issue POST/PUT");
    }

    [Fact]
    public void DeletePrefixedTools_AreInTheDeleteTier()
    {
        // v1 ships no delete tools, so this guards the first one to arrive rather than any today.
        var tools = ToolCatalogue.Production().Where(t => t.Name.StartsWith("Delete_", StringComparison.Ordinal)).ToList();
        Assert.SkipWhen(tools.Count == 0, "No Delete_* tools are registered (v1 has none).");

        tools.Should().OnlyContain(t => t.Policy == DeletePolicy, "Delete_* tools issue DELETEs");
    }

    [Fact]
    public void EveryTool_IsClassified_AndTheSetIsNotEmpty()
    {
        var tools = ToolCatalogue.Production();

        tools.Should().NotBeEmpty("a sweep over nothing proves nothing");
        tools.Should().OnlyContain(t => t.Policy == ReadPolicy || t.Policy == WritePolicy || t.Policy == DeletePolicy,
            "each tool must be in exactly one of the three tiers");
        tools.Select(t => t.Name).Should().OnlyHaveUniqueItems("a duplicate name would make one tool unreachable");
    }
}
