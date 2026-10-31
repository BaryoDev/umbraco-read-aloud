using System.Net;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>The settings endpoints on a real boot, called without a backoffice login.</summary>
[Collection(UmbracoCollection.Name)]
public class SettingsEndpointTests
{
    private const string Base = "/umbraco/management/api/v1/baryodev/read-aloud/azure";

    private readonly UmbracoSiteFixture _site;

    public SettingsEndpointTests(UmbracoSiteFixture site) => _site = site;

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/test")]
    [InlineData("PUT", "")]
    [InlineData("DELETE", "")]
    public async Task Anonymous_callers_are_refused_and_nothing_is_echoed(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Base + path)
        {
            Content = new StringContent(
                $$"""{"region":"southeastasia","key":"{{AzureTestSupport.FakeKey}}"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        var response = await _site.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(AzureTestSupport.FakeKey);
    }
}
