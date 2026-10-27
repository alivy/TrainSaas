using System.Net;

namespace TrainSaas.Tests.Integration;

public class HealthEndpointTests(ApiFactory factory)
    : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }
}
