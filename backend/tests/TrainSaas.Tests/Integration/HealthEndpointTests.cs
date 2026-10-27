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

    /// <summary>
    /// R16a：Swagger UI 中间件（Swashbuckle.AspNetCore.SwaggerUI，仅 UI，不带
    /// 文档生成器）挂在内置 MapOpenApi 产出的文档上——/swagger/index.html 可
    /// 达，且 UI 的配置脚本 /swagger/index.js 引用 /openapi/v1.json（测试宿主
    /// 默认 Development 环境，MapOpenApi 生效）。
    /// </summary>
    [Fact]
    public async Task SwaggerUI_is_served_and_points_at_builtin_openapi_document()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Swashbuckle 10.x 把 configObject（含文档 urls）放进 index.js，
        // index.html 只负责加载它。
        var configScript = await client.GetAsync("/swagger/index.js");
        Assert.Equal(HttpStatusCode.OK, configScript.StatusCode);
        Assert.Contains("\"url\":\"/openapi/v1.json\"", await configScript.Content.ReadAsStringAsync());
    }
}
