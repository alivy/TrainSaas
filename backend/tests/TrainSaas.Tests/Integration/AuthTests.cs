using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// Integration tests for the three login flows (platform / org staff / parent),
/// the JWT claim-name contract, and the authorization policies.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own rows
/// via the host's AppDbContext (factory.Services.CreateScope) — the startup
/// seed is not available in tests.
/// </summary>
public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Decodes a JWT without signature validation and returns one claim by its literal name.</summary>
    private static string Claim(JwtSecurityToken jwt, string type) =>
        jwt.Claims.First(c => c.Type == type).Value;

    private async Task<Institution> SeedInstitutionAsync(
        string code, InstitutionStatus status = InstitutionStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var institution = new Institution { Code = code, Name = $"机构{code}", Status = status };
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        return institution;
    }

    private async Task<(Institution Institution, Staff Staff)> SeedStaffAsync(
        string code,
        string phone,
        StaffRole role = StaffRole.Admin,
        bool staffActive = true,
        InstitutionStatus institutionStatus = InstitutionStatus.Active)
    {
        var institution = await SeedInstitutionAsync(code, institutionStatus);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var staff = new Staff
        {
            InstitutionId = institution.Id,
            Name = $"员工{code}",
            Phone = phone,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
            Role = role,
            IsActive = staffActive,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return (institution, staff);
    }

    private async Task<ParentAccount> SeedParentAsync(string phone)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var parent = new ParentAccount
        {
            Phone = phone,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
        };
        db.ParentAccounts.Add(parent);
        await db.SaveChangesAsync();
        return parent;
    }

    private async Task<PlatformUser> SeedPlatformUserAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var user = new PlatformUser
        {
            Username = username,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
        };
        db.PlatformUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<JsonElement> LoginStaffAsync(HttpClient client, string phone, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/org/auth/login", new { phone, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> LoginParentAsync(HttpClient client, string phone, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/parent/auth/login", new { phone, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient AuthedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Staff_login_with_seeded_admin_phone_and_initial_password_returns_token()
    {
        var (institution, staff) = await SeedStaffAsync("auth1", "13911100001", StaffRole.Admin);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13911100001", password = DefaultPassword.Initial });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal($"员工auth1", body.GetProperty("name").GetString());
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.Equal(institution.Id, body.GetProperty("institutionId").GetInt32());
        Assert.Equal($"机构auth1", body.GetProperty("institutionName").GetString());
        Assert.Equal("auth1", body.GetProperty("institutionCode").GetString());

        // The token must carry the literal Global-Constraints claim names and values.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token!);
        Assert.Equal("TrainSaas", jwt.Issuer);
        Assert.Equal("staff", Claim(jwt, "user_type"));
        Assert.Equal(institution.Id.ToString(), Claim(jwt, "institution_id"));
        Assert.Equal(staff.Id.ToString(), Claim(jwt, "staff_id"));
        Assert.Equal("Admin", Claim(jwt, "role"));
        Assert.Equal("13911100001", Claim(jwt, "phone"));
        Assert.Equal($"员工auth1", Claim(jwt, "name"));
    }

    [Fact]
    public async Task Staff_login_wrong_password_returns_401()
    {
        await SeedStaffAsync("auth2", "13911100002");
        var client = factory.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13911100002", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Unknown phone fails identically — no account-existence leak.
        var unknown = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13900099999", password = "whatever" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Staff_of_disabled_institution_gets_403()
    {
        await SeedStaffAsync("auth3", "13911100003", institutionStatus: InstitutionStatus.Disabled);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13911100003", password = DefaultPassword.Initial });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("机构已停用", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Deactivated_staff_login_returns_403()
    {
        await SeedStaffAsync("auth8", "13911100008", staffActive: false);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/org/auth/login",
            new { phone = "13911100008", password = DefaultPassword.Initial });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("停用", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Same_phone_as_staff_and_parent_logs_into_own_endpoint_independently()
    {
        const string phone = "13911100004";
        var (institution, _) = await SeedStaffAsync("auth4", phone, StaffRole.Sales);
        await SeedParentAsync(phone);
        var client = factory.CreateClient();

        // Org login resolves the staff account and issues a staff token.
        var staffBody = await LoginStaffAsync(client, phone, DefaultPassword.Initial);
        var staffToken = staffBody.GetProperty("token").GetString()!;
        Assert.Equal("Sales", staffBody.GetProperty("role").GetString());
        Assert.Equal(institution.Id, staffBody.GetProperty("institutionId").GetInt32());
        var staffJwt = new JwtSecurityTokenHandler().ReadJwtToken(staffToken);
        Assert.Equal("staff", Claim(staffJwt, "user_type"));

        // Parent login resolves the parent account and issues a parent token.
        var parentBody = await LoginParentAsync(client, phone, DefaultPassword.Initial);
        Assert.Equal(phone, parentBody.GetProperty("phone").GetString());
        var parentToken = parentBody.GetProperty("token").GetString()!;
        var parentJwt = new JwtSecurityTokenHandler().ReadJwtToken(parentToken);
        Assert.Equal("parent", Claim(parentJwt, "user_type"));
        Assert.Equal(phone, Claim(parentJwt, "phone"));

        // 互不串号: each token only opens its own endpoint family.
        var staffClient = AuthedClient(staffToken);
        var staffOnParent = await staffClient.PostAsJsonAsync("/api/parent/change-password",
            new { oldPassword = DefaultPassword.Initial, newPassword = "newpass123" });
        Assert.Equal(HttpStatusCode.Forbidden, staffOnParent.StatusCode);

        var parentClient = AuthedClient(parentToken);
        var parentOnOrg = await parentClient.GetAsync("/api/org/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, parentOnOrg.StatusCode);

        // Anonymous callers are challenged, not forbidden.
        var anonymous = await client.GetAsync("/api/org/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Parent_change_password_then_old_password_fails()
    {
        const string phone = "13911100005";
        await SeedParentAsync(phone);
        var client = factory.CreateClient();

        var firstLogin = await LoginParentAsync(client, phone, DefaultPassword.Initial);
        var token = firstLogin.GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var change = await authed.PostAsJsonAsync("/api/parent/change-password",
            new { oldPassword = DefaultPassword.Initial, newPassword = "newpass123" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var oldLogin = await client.PostAsJsonAsync("/api/parent/auth/login",
            new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostAsJsonAsync("/api/parent/auth/login",
            new { phone, password = "newpass123" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Parent_change_password_with_short_new_password_returns_400()
    {
        const string phone = "13911100007";
        await SeedParentAsync(phone);
        var client = factory.CreateClient();

        var login = await LoginParentAsync(client, phone, DefaultPassword.Initial);
        var token = login.GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var change = await authed.PostAsJsonAsync("/api/parent/change-password",
            new { oldPassword = DefaultPassword.Initial, newPassword = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        var body = await change.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("6", body.GetProperty("detail").GetString());

        // The password must be unchanged: the initial password still logs in.
        var relogin = await client.PostAsJsonAsync("/api/parent/auth/login",
            new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
    }

    [Fact]
    public async Task Parent_login_unknown_phone_returns_401()
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/parent/auth/login",
            new { phone = "13700000000", password = "whatever" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Platform_login_returns_token_and_name()
    {
        await SeedPlatformUserAsync("rootadmin");
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "rootadmin", password = DefaultPassword.Initial });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal("rootadmin", body.GetProperty("name").GetString());

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token!);
        Assert.Equal("platform", Claim(jwt, "user_type"));
        Assert.Equal("rootadmin", Claim(jwt, "name"));

        var wrong = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "rootadmin", password = "bad-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var unknown = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "no-such-user", password = "bad-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    /// <summary>
    /// I5：平台超管自助改密——生产首次部署的必经动作（初始密码 123456 改掉）。
    /// 成功 204，旧密码立即失效（401），新密码可登录。
    /// </summary>
    [Fact]
    public async Task Platform_change_password_then_old_password_fails()
    {
        await SeedPlatformUserAsync("chgroot1");
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot1", password = DefaultPassword.Initial });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var change = await authed.PostAsJsonAsync("/api/platform/auth/change-password",
            new { oldPassword = DefaultPassword.Initial, newPassword = "newpass123" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var oldLogin = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot1", password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot1", password = "newpass123" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>I5：旧密码错误 → 401（ProblemDetails "旧密码错误"），密码未被改动。</summary>
    [Fact]
    public async Task Platform_change_password_with_wrong_old_password_returns_401()
    {
        await SeedPlatformUserAsync("chgroot2");
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot2", password = DefaultPassword.Initial });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var change = await authed.PostAsJsonAsync("/api/platform/auth/change-password",
            new { oldPassword = "totally-wrong", newPassword = "newpass123" });
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        var problem = await change.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("旧密码错误", problem.GetProperty("detail").GetString());

        // 密码未变：初始密码仍可登录。
        var relogin = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot2", password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
    }

    /// <summary>I5：新密码 &lt;6 位 → 400，密码未被改动。</summary>
    [Fact]
    public async Task Platform_change_password_with_short_new_password_returns_400()
    {
        await SeedPlatformUserAsync("chgroot3");
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot3", password = DefaultPassword.Initial });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var change = await authed.PostAsJsonAsync("/api/platform/auth/change-password",
            new { oldPassword = DefaultPassword.Initial, newPassword = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        var problem = await change.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("6", problem.GetProperty("detail").GetString());

        var relogin = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = "chgroot3", password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
    }

    /// <summary>I5：改密端点只对平台令牌开放——匿名 401、员工 403。</summary>
    [Fact]
    public async Task Platform_change_password_requires_platform_token()
    {
        var anonymous = await factory.CreateClient().PostAsJsonAsync("/api/platform/auth/change-password",
            new { oldPassword = "x", newPassword = "y" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await SeedStaffAsync("auth7", "13911100007", StaffRole.Admin);
        var staffLogin = await LoginStaffAsync(factory.CreateClient(), "13911100007", DefaultPassword.Initial);
        var staffClient = AuthedClient(staffLogin.GetProperty("token").GetString()!);
        var forbidden = await staffClient.PostAsJsonAsync("/api/platform/auth/change-password",
            new { oldPassword = "x", newPassword = "y" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Reaching 200 here requires the literal claim names to survive the JWT
    /// bearer pipeline untouched: the StaffOnly policy matches user_type=staff,
    /// and the endpoint resolves staff_id/institution_id claims. If inbound
    /// claim-type mapping rewrote any name, this returns 403/404.
    /// </summary>
    [Fact]
    public async Task Staff_me_returns_staff_profile_and_institution()
    {
        var (institution, staff) = await SeedStaffAsync("auth6", "13911100006", StaffRole.Teacher);
        var client = factory.CreateClient();

        var login = await LoginStaffAsync(client, "13911100006", DefaultPassword.Initial);
        var token = login.GetProperty("token").GetString()!;

        var authed = AuthedClient(token);
        var resp = await authed.GetAsync("/api/org/auth/me");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(staff.Id, body.GetProperty("staffId").GetInt32());
        Assert.Equal($"员工auth6", body.GetProperty("name").GetString());
        Assert.Equal("Teacher", body.GetProperty("role").GetString());
        Assert.Equal(institution.Id, body.GetProperty("institutionId").GetInt32());
        Assert.Equal($"机构auth6", body.GetProperty("institutionName").GetString());
        Assert.Equal("auth6", body.GetProperty("institutionCode").GetString());
    }
}
