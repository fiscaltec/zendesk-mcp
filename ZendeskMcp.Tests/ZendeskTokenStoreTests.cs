using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ZendeskMcp.Tests;

/// <summary>
/// One Key Vault secret per person. Removal is a tombstone version rather than a delete, because a
/// soft-deleted name cannot be set again until purged — a delete would make disconnect-then-reconnect
/// fail for the whole retention period.
/// </summary>
public class ZendeskTokenStoreTests
{
    private const string ObjectId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public async Task Save_WritesOneSecretPerPerson_AsJson()
    {
        var secrets = new Mock<SecretClient>();
        KeyVaultSecret? written = null;
        secrets.Setup(s => s.SetSecretAsync(It.IsAny<KeyVaultSecret>(), It.IsAny<CancellationToken>()))
            .Callback<KeyVaultSecret, CancellationToken>((secret, _) => written = secret)
            .ReturnsAsync(Response.FromValue(new KeyVaultSecret("x", "y"), Mock.Of<Response>()));
        var store = Build(secrets.Object);

        await store.SaveAsync(ObjectId, TestHelpers.SampleToken("abc"));

        written!.Name.Should().Be($"zendesk-user-{ObjectId}");
        written.Properties.ContentType.Should().Be("application/json");
        written.Value.Should().Contain("\"access_token\":\"abc\"");
        written.Properties.Tags["kind"].Should().Be("zendesk-user-token");
    }

    [Fact]
    public async Task Get_RoundTripsWhatWasStored()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TestHelpers.SampleToken("abc"));
        var secrets = SecretReturning(json);
        var store = Build(secrets.Object);

        var token = await store.GetAsync(ObjectId);

        token.Should().Be(TestHelpers.SampleToken("abc"));
    }

    [Fact]
    public async Task Get_ReturnsNull_ForAPersonWhoNeverConnected()
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<string>(), null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "SecretNotFound"));

        (await Build(secrets.Object).GetAsync(ObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task Get_CachesAbsence_SoAnUnconnectedPersonDoesNotHitTheVaultEveryCall()
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<string>(), null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "SecretNotFound"));
        var store = Build(secrets.Object);

        await store.GetAsync(ObjectId);
        await store.GetAsync(ObjectId);

        secrets.Verify(s => s.GetSecretAsync(It.IsAny<string>(), null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Remove_WritesATombstone_RatherThanDeleting()
    {
        var secrets = new Mock<SecretClient>(MockBehavior.Strict);
        KeyVaultSecret? written = null;
        secrets.Setup(s => s.SetSecretAsync(It.IsAny<KeyVaultSecret>(), It.IsAny<CancellationToken>()))
            .Callback<KeyVaultSecret, CancellationToken>((secret, _) => written = secret)
            .ReturnsAsync(Response.FromValue(new KeyVaultSecret("x", "y"), Mock.Of<Response>()));
        var store = Build(secrets.Object);

        await store.RemoveAsync(ObjectId);

        written!.Name.Should().Be($"zendesk-user-{ObjectId}");
        KeyVaultZendeskTokenStore.Parse(written.Value).Should().BeNull("a tombstone reads as not connected");
        (await store.GetAsync(ObjectId)).Should().BeNull("this replica must not serve the token it has just removed");
    }

    [Theory]
    [InlineData("{\"revoked\":true}")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ReadsATombstoneOrNothingAsNotConnected(string value)
    {
        KeyVaultZendeskTokenStore.Parse(value).Should().BeNull();
    }

    [Theory]
    [InlineData("not json", null, null)]
    [InlineData("[1,2]", null, null)]
    [InlineData("{}", null, null)]
    [InlineData("{\"access_token\":\"\"}", null, null)]
    [InlineData("{\"revoked\":true,\"refresh_token\":\"r1\"}", null, "r1")]
    [InlineData("{\"access_token\":\"a1\",\"expires_at\":\"not a date\"}", "a1", null)]
    [InlineData("{\"access_token\":\"a1\",\"refresh_token\":\"r1\",\"zendesk_user_id\":\"x\"}", "a1", "r1")]
    [InlineData("{\"refresh_token\":\"r1\"}", null, "r1")]
    public void Parse_RefusesAnythingElse_SalvagingTheTokensForRevocation(string value, string? access, string? refresh)
    {
        // Read as "not connected", such a value let a reconnect replace the grant inside it without
        // revoking it (#12). Whatever tokens it holds are kept, for revocation only.
        var act = () => KeyVaultZendeskTokenStore.Parse(value);

        var thrown = act.Should().Throw<ZendeskStoredTokenUnreadableException>().Which;
        thrown.Message.Should().NotContain("a1").And.NotContain("r1");
        if (access is null && refresh is null)
        {
            thrown.Salvaged.Should().BeNull();
            return;
        }

        thrown.Salvaged!.AccessToken.Should().Be(access ?? string.Empty);
        thrown.Salvaged.RefreshToken.Should().Be(refresh);
        if (refresh is not null)
        {
            thrown.Salvaged.ExpiresAt.Should().Be(DateTimeOffset.UnixEpoch,
                "an untrusted expiry is treated as expired, so a revocation refreshes first");
        }
    }

    [Fact]
    public async Task Get_OfAnUnreadableSecret_Throws_LogsTheVersion_AndCachesNothing()
    {
        var secrets = new Mock<SecretClient>();
        var secret = SecretModelFactory.KeyVaultSecret(
            SecretModelFactory.SecretProperties(new Uri($"https://kv.vault.azure.net/secrets/zendesk-user-{ObjectId}/v123"), version: "v123"),
            "{\"access_token\":\"live-token\",\"expires_at\":\"not a date\"}");
        secrets.Setup(s => s.GetSecretAsync($"zendesk-user-{ObjectId}", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(secret, Mock.Of<Response>()));
        var logger = new CapturingLogger<KeyVaultZendeskTokenStore>();
        var store = new KeyVaultZendeskTokenStore(secrets.Object, new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ZendeskServerOptions { Subdomain = "acme" }), logger);

        var first = () => store.GetAsync(ObjectId);
        (await first.Should().ThrowAsync<ZendeskStoredTokenUnreadableException>()).Which.Salvaged!.AccessToken.Should().Be("live-token");
        var second = () => store.GetAsync(ObjectId);
        await second.Should().ThrowAsync<ZendeskStoredTokenUnreadableException>("caching it as not connected is the defect");

        secrets.Verify(s => s.GetSecretAsync(It.IsAny<string>(), null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        logger.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Message.Contains(ObjectId) && e.Message.Contains("v123") && !e.Message.Contains("live-token"));
        logger.Exceptions.Should().OnlyContain(e => e == null, "an attached exception is rendered in full by the exporters");
    }

    private static KeyVaultZendeskTokenStore Build(SecretClient secrets) =>
        new(secrets, new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ZendeskServerOptions { Subdomain = "acme" }),
            NullLogger<KeyVaultZendeskTokenStore>.Instance);

    private static Mock<SecretClient> SecretReturning(string value)
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync($"zendesk-user-{ObjectId}", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new KeyVaultSecret($"zendesk-user-{ObjectId}", value), Mock.Of<Response>()));
        return secrets;
    }
}
