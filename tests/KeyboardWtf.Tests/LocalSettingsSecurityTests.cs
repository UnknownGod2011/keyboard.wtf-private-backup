using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class LocalSettingsSecurityTests
{
    [Fact]
    public void Action_token_requires_an_exact_unpredictable_session_value()
    {
        const string expected = "g-LZs7Bv2W8kq5C6V4mX9P1tR3nF0aHd";

        Assert.True(WebSettingsService.HasValidSettingsActionToken(expected, expected));
        Assert.False(WebSettingsService.HasValidSettingsActionToken("explicit-user-action", expected));
        Assert.False(WebSettingsService.HasValidSettingsActionToken(expected + "x", expected));
        Assert.False(WebSettingsService.HasValidSettingsActionToken(null, expected));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:8090", true)]
    [InlineData("http://127.0.0.1:8090", true)]
    [InlineData("http://[::1]:8090", true)]
    [InlineData("https://localhost:8090", false)]
    [InlineData("http://localhost:8091", false)]
    [InlineData("https://evil.example", false)]
    public void Origin_is_limited_to_the_exact_loopback_settings_origin(string origin, bool expected) =>
        Assert.Equal(expected, WebSettingsService.IsSameSettingsOrigin(origin, 8090));

    [Theory]
    [InlineData("http://localhost:8090/#account", true)]
    [InlineData("http://127.0.0.1:8090/settings", true)]
    [InlineData("http://localhost:8091/", false)]
    [InlineData("https://evil.example/attack", false)]
    public void Referer_is_limited_to_the_exact_loopback_settings_origin(string referer, bool expected) =>
        Assert.Equal(expected, WebSettingsService.IsSameSettingsReferer(referer, 8090));

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=UTF-8", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    public void Mutation_bodies_must_be_json(string contentType, bool expected) =>
        Assert.Equal(expected, WebSettingsService.IsJsonContentType(contentType));

    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("none", true)]
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    public void Browser_fetch_metadata_rejects_cross_site_mutations(string site, bool expected) =>
        Assert.Equal(expected, WebSettingsService.IsSameOriginFetch(site));
}
