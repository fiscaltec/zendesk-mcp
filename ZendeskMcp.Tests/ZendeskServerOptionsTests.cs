using FluentAssertions;

namespace ZendeskMcp.Tests;

public class VitallyServerOptionsTests
{
    private static ZendeskServerOptions Valid() => new()
    {
        Region = "EU",
        DevelopmentApiKey = "sk_live_test"
    };

    [Fact]
    public void MaxAutoPageFetches_DefaultsTo10()
    {
        new ZendeskServerOptions().MaxAutoPageFetches.Should().Be(10);
    }

    [Fact]
    public void Validate_RejectsNonPositiveMaxAutoPageFetches()
    {
        var opts = Valid();
        opts.MaxAutoPageFetches = 0;
        Action act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxAutoPageFetches*");
    }

    [Fact]
    public void Validate_AcceptsPositiveMaxAutoPageFetches()
    {
        var opts = Valid();
        opts.MaxAutoPageFetches = 5;
        Action act = () => opts.Validate();
        act.Should().NotThrow();
    }
}
