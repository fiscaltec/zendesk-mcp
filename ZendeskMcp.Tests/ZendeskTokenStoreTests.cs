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
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"access_token\":\"\"}")]
    public void Parse_TreatsAnythingWithoutAnAccessTokenAsNotConnected(string value)
    {
        KeyVaultZendeskTokenStore.Parse(value).Should().BeNull();
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
