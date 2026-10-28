using FluentAssertions;

namespace ZendeskMcp.Tests;

public class ZendeskServerOptionsTests
{
    // Per-user OAuth with a local client secret: the smallest configuration that validates without
    // the development API token.
    private static ZendeskServerOptions Valid() => new()
    {
        Subdomain = "acme",
        OAuthClientId = "zendesk-test-client",
        DevelopmentOAuthClientSecret = "zendesk-test-secret",
    };

    private static ZendeskServerOptions ValidDevToken() => new()
    {
        Subdomain = "acme",
        DevelopmentEmail = "dev@example.com",
        DevelopmentApiToken = "zd_test_token",
    };

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var opts = new ZendeskServerOptions();
        opts.MaxAutoPageFetches.Should().Be(10);
        opts.MaxAnalyticsTickets.Should().Be(2000);
        opts.ConnectLinkLifetime.Should().Be(TimeSpan.FromMinutes(15));
        opts.RequireEmailMatch.Should().BeTrue("binding a Zendesk login to the Entra identity is on unless turned off");
        opts.OAuthScopes.Should().Be("read write");
    }

    [Fact]
    public void Validate_AcceptsPerUserOAuthWithADevelopmentSecret()
    {
        var act = () => Valid().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AcceptsTheDevelopmentApiTokenWithItsEmail()
    {
        var act = () => ValidDevToken().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void BaseUrl_IsTheAccountOrigin_AfterTrimming()
    {
        var opts = Valid();
        opts.Subdomain = "  acme  ";
        opts.Validate();
        opts.BaseUrl.Should().Be("https://acme.zendesk.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RequiresASubdomain(string? subdomain)
    {
        var opts = Valid();
        opts.Subdomain = subdomain;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Subdomain*");
    }

    [Theory]
    // Anything beyond letters, digits and hyphens could change the host the caller's token is sent
    // to (BaseUrl is built by interpolation), so each of these must be refused.
    [InlineData("acme.evil.com")]
    [InlineData("acme/evil")]
    [InlineData("acme@evil")]
    [InlineData("acme:443")]
    [InlineData("https://acme")]
    [InlineData("ac me")]
    public void Validate_RejectsASubdomainOutsideTheCharset(string subdomain)
    {
        var opts = Valid();
        opts.Subdomain = subdomain;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Subdomain*");
    }

    [Fact]
    public void Validate_AcceptsHyphensAndDigitsInTheSubdomain()
    {
        var opts = Valid();
        opts.Subdomain = "acme-2";
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_RequiresTheEmail_WithTheDevelopmentApiToken()
    {
        // Zendesk API tokens authenticate as `{email}/token`, so a token alone cannot be used.
        var opts = ValidDevToken();
        opts.DevelopmentEmail = " ";
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*DevelopmentEmail*");
    }

    [Fact]
    public void Validate_RequiresTheOAuthClientId_WithoutTheDevelopmentApiToken()
    {
        var opts = Valid();
        opts.OAuthClientId = null;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuthClientId*");
    }

    [Fact]
    public void Validate_DoesNotRequireTheOAuthClient_WithTheDevelopmentApiToken()
    {
        var opts = ValidDevToken();
        opts.OAuthClientId = null;
        opts.DevelopmentOAuthClientSecret = null;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_RequiresAKeyVaultOrADevelopmentSecret_ForPerUserOAuth()
    {
        var opts = Valid();
        opts.DevelopmentOAuthClientSecret = null;
        opts.KeyVaultUri = null;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*KeyVaultUri*DevelopmentOAuthClientSecret*");
    }

    [Fact]
    public void Validate_AcceptsAKeyVaultInPlaceOfTheDevelopmentSecret()
    {
        var opts = Valid();
        opts.DevelopmentOAuthClientSecret = null;
        opts.KeyVaultUri = "https://kv-zendesk.vault.azure.net/";
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_RequiresTheSecretRef_WithAKeyVault()
    {
        var opts = Valid();
        opts.DevelopmentOAuthClientSecret = null;
        opts.KeyVaultUri = "https://kv-zendesk.vault.azure.net/";
        opts.OAuthClientSecretRef = "  ";
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuthClientSecretRef*");
    }

    [Theory]
    [InlineData("http://kv-zendesk.vault.azure.net/")]
    [InlineData("kv-zendesk.vault.azure.net")]
    [InlineData("not a uri")]
    public void Validate_RequiresAnHttpsKeyVaultUri(string keyVaultUri)
    {
        // The vault holds every person's Zendesk token, so it is never reached over plain http.
        var opts = Valid();
        opts.KeyVaultUri = keyVaultUri;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*KeyVaultUri*https*");
    }

    [Theory]
    [InlineData(299)]
    [InlineData(172_801)]
    [InlineData(0)]
    public void Validate_RejectsAnAccessTokenLifetimeOutOfRange(int seconds)
    {
        var opts = Valid();
        opts.AccessTokenExpiresInSeconds = seconds;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*AccessTokenExpiresInSeconds*");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(172_800)]
    [InlineData(null)]
    public void Validate_AcceptsAnAccessTokenLifetimeInRange_OrNone(int? seconds)
    {
        var opts = Valid();
        opts.AccessTokenExpiresInSeconds = seconds;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(604_799)]
    [InlineData(7_776_001)]
    public void Validate_RejectsARefreshTokenLifetimeOutOfRange(int seconds)
    {
        var opts = Valid();
        opts.RefreshTokenExpiresInSeconds = seconds;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*RefreshTokenExpiresInSeconds*");
    }

    [Theory]
    [InlineData(604_800)]
    [InlineData(7_776_000)]
    [InlineData(null)]
    public void Validate_AcceptsARefreshTokenLifetimeInRange_OrNone(int? seconds)
    {
        var opts = Valid();
        opts.RefreshTokenExpiresInSeconds = seconds;
        var act = () => opts.Validate();
        act.Should().NotThrow();
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

    [Fact]
    public void Validate_RejectsNonPositiveMaxAnalyticsTickets()
    {
        var opts = Valid();
        opts.MaxAnalyticsTickets = 0;
        Action act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxAnalyticsTickets*");
    }

    [Fact]
    public void Validate_AcceptsAMaxAnalyticsTicketsOfOne()
    {
        var opts = Valid();
        opts.MaxAnalyticsTickets = 1;
        Action act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Validate_RejectsAConnectLinkLifetimeOutOfBounds(int minutes)
    {
        // A connect link stores a Zendesk token against its owner, so it must not stay usable for long.
        var opts = Valid();
        opts.ConnectLinkLifetime = TimeSpan.FromMinutes(minutes);
        Action act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectLinkLifetime*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void Validate_AcceptsAConnectLinkLifetimeWithinBounds(int minutes)
    {
        var opts = Valid();
        opts.ConnectLinkLifetime = TimeSpan.FromMinutes(minutes);
        Action act = () => opts.Validate();
        act.Should().NotThrow();
    }
}
