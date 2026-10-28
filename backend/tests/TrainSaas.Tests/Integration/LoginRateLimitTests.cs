using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// I6（最终审查）：三个匿名登录路由（platform/org/parent）挂 "login"
/// 固定窗口限流——10 次/分钟/源 IP，超限 429。全网初始密码 123456 若无
/// 节流可被批量爆破；限流器在端点之前运行，第 11 次即便携带正确凭据也被拒。
///
/// 与 PublicOrgRateLimitTests 同款纪律：限流分区住在宿主里、所有测试客户端
/// 共享同一 loopback 地址，因此每个测试构造**专属 ApiFactory**（独立宿主 =
/// 独立分区），并把 RateLimit:LoginPermitLimit 显式设回 10（共享 fixture 的
/// 默认覆盖是放宽值——各集成测试类的登录次数远超 10/分钟，见 ApiFactory）。
/// </summary>
public class LoginRateLimitTests
{
    /// <summary>专属宿主：login 限流恢复生产默认（10 次/分钟/IP）。</summary>
    private static ApiFactory LimitedFactory() => new(new Dictionary<string, string>
    {
        ["RateLimit:LoginPermitLimit"] = "10",
    });

    private static async Task<Institution> SeedInstitutionAsync(ApiFactory factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var institution = new Institution
        {
            Code = code,
            Name = $"机构{code}",
            Status = InstitutionStatus.Active,
        };
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        return institution;
    }

    private static async Task SeedStaffAsync(ApiFactory factory, int institutionId, string phone)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        db.Staff.Add(new Staff
        {
            InstitutionId = institutionId,
            Name = $"员工{phone}",
            Phone = phone,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
            Role = StaffRole.Admin,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Eleventh_org_login_from_same_ip_within_a_minute_returns_429()
    {
        using var factory = LimitedFactory();
        var institution = await SeedInstitutionAsync(factory, "lrl1");
        await SeedStaffAsync(factory, institution.Id, "13977700001");
        var client = factory.CreateClient();

        // 前 10 次（错误密码 → 401）都在窗口配额内，绝不 429。
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/org/auth/login",
                new { phone = "13977700001", password = "wrong-password" });
            statuses.Add(resp.StatusCode);
        }

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, 10), statuses);

        // 第 11 次即便携带**正确**凭据也被限流器挡在端点之前 → 429。
        var eleventh = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13977700001", password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }

    [Fact]
    public async Task Parent_and_platform_login_routes_are_throttled_too()
    {
        // 家长登录路由（专属宿主，避免与上面共享分区）。
        using (var factory = LimitedFactory())
        {
            var client = factory.CreateClient();
            for (var i = 0; i < 10; i++)
            {
                var resp = await client.PostAsJsonAsync("/api/parent/auth/login",
                    new { phone = "13977700002", password = "wrong-password" });
                Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await client.PostAsJsonAsync("/api/parent/auth/login",
                    new { phone = "13977700002", password = "wrong-password" })).StatusCode);
        }

        // 平台登录路由（再一个专属宿主）。
        using (var factory = LimitedFactory())
        {
            var client = factory.CreateClient();
            for (var i = 0; i < 10; i++)
            {
                var resp = await client.PostAsJsonAsync("/api/platform/auth/login",
                    new { username = "nobody", password = "wrong-password" });
                Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
            }

            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await client.PostAsJsonAsync("/api/platform/auth/login",
                    new { username = "nobody", password = "wrong-password" })).StatusCode);
        }
    }
}
