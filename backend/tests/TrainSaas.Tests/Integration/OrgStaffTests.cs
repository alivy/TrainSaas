using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// Integration tests for the institution staff management API (Task 5):
/// admin creates staff (initial password 123456, globally unique phone,
/// AdminOnly policy), self-deactivation, password reset, and the
/// last-active-admin protection on demotion and deactivation.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff rows via factory.Services.CreateScope() and drives
/// every behavior over real HTTP with real tokens. Codes/phones are unique
/// per test because the class fixture shares one database. Seeding scopes
/// have no HTTP context (anonymous current user), so every direct Staff
/// read uses IgnoreQueryFilters.
/// </summary>
public class OrgStaffTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<Institution> SeedInstitutionAsync(string code)
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

    private async Task<Staff> SeedStaffAsync(
        int institutionId, string phone, StaffRole role = StaffRole.Admin, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var staff = new Staff
        {
            InstitutionId = institutionId,
            Name = $"员工{phone}",
            Phone = phone,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
            Role = role,
            IsActive = isActive,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    /// <summary>Reads one staff row straight from the DB, bypassing the tenant filter.</summary>
    private async Task<Staff> GetStaffRowAsync(int staffId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Staff.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == staffId);
    }

    /// <summary>Logs a staff member in over real HTTP and returns a bearer-authed client.</summary>
    private async Task<HttpClient> LoginStaffClientAsync(string phone)
    {
        var resp = await StaffLoginAsync(phone);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return AuthedClient(token!);
    }

    private Task<HttpResponseMessage> StaffLoginAsync(
        string phone, string password = DefaultPassword.Initial)
    {
        var client = factory.CreateClient();
        return client.PostAsJsonAsync("/api/org/auth/login", new { phone, password });
    }

    private HttpClient AuthedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url) =>
        client.PostAsync(url, content: null);

    [Fact]
    public async Task Admin_creates_sales_staff_who_can_login_with_123456()
    {
        var institution = await SeedInstitutionAsync("os1");
        var admin = await SeedStaffAsync(institution.Id, "13933300001", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13933300001");

        var resp = await client.PostAsJsonAsync("/api/org/staff",
            new { name = "销售小李", phone = "13933300101", role = "Sales" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // 201 body is exactly {id,name,phone,role,isActive} — active from the start.
        Assert.Equal(
            new[] { "id", "isActive", "name", "phone", "role" }.OrderBy(n => n),
            created.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var staffId = created.GetProperty("id").GetInt32();
        Assert.True(staffId > 0);
        Assert.Equal("销售小李", created.GetProperty("name").GetString());
        Assert.Equal("13933300101", created.GetProperty("phone").GetString());
        Assert.Equal("Sales", created.GetProperty("role").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        // The new staff logs in with the initial password right away and lands
        // in the creator's institution (InstitutionId auto-filled by SaveChanges).
        var login = await StaffLoginAsync("13933300101");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sales", body.GetProperty("role").GetString());
        Assert.Equal(institution.Id, body.GetProperty("institutionId").GetInt32());

        // The list shows the created staff with the documented item shape.
        var list = await client.GetAsync("/api/org/staff");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var paged = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            new[] { "items", "total" }.OrderBy(n => n),
            paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(2, paged.GetProperty("total").GetInt32());
        var row = paged.GetProperty("items").EnumerateArray()
            .Single(s => s.GetProperty("id").GetInt32() == staffId);
        Assert.Equal(
            new[] { "createdAt", "id", "isActive", "name", "phone", "role" }.OrderBy(n => n),
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("销售小李", row.GetProperty("name").GetString());
        Assert.Equal("13933300101", row.GetProperty("phone").GetString());
        Assert.Equal("Sales", row.GetProperty("role").GetString());
        Assert.True(row.GetProperty("isActive").GetBoolean());
        Assert.True(row.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        // Optional role filter narrows the list…
        var sales = await client.GetAsync("/api/org/staff?role=Sales");
        Assert.Equal(HttpStatusCode.OK, sales.StatusCode);
        var salesPaged = await sales.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, salesPaged.GetProperty("total").GetInt32());
        Assert.Single(salesPaged.GetProperty("items").EnumerateArray());

        var admins = await client.GetAsync("/api/org/staff?role=Admin");
        Assert.Equal(HttpStatusCode.OK, admins.StatusCode);
        var adminsPaged = await admins.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(admin.Id, adminsPaged.GetProperty("items").EnumerateArray()
            .Single().GetProperty("id").GetInt32());

        // …while an invalid value is a clean 400, not a 500.
        var badRole = await client.GetAsync("/api/org/staff?role=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        var problem = await ProblemDetailAsync(badRole);
        Assert.Contains("角色", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Sales_cannot_create_staff_returns_403()
    {
        var institution = await SeedInstitutionAsync("os2");
        await SeedStaffAsync(institution.Id, "13933300002", StaffRole.Admin);
        await SeedStaffAsync(institution.Id, "13933300202", StaffRole.Sales);

        var sales = await LoginStaffClientAsync("13933300202");

        var resp = await sales.PostAsJsonAsync("/api/org/staff",
            new { name = "前台小赵", phone = "13933300212", role = "FrontDesk" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // The rejected create left nothing behind.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Staff.IgnoreQueryFilters()
                .AnyAsync(s => s.Phone == "13933300212"));
        }
    }

    [Fact]
    public async Task Staff_phone_used_in_another_institution_returns_409()
    {
        // A staff row in ANOTHER institution, seeded directly in the DB: the
        // create below runs under institution os3's admin token, whose tenant
        // filter hides this row — the phone-collision check must ignore the
        // filter to see it (a tenant-filtered check would let the create pass).
        var other = await SeedInstitutionAsync("os3a");
        var otherStaff = await SeedStaffAsync(other.Id, "13933300003", StaffRole.Teacher);

        var institution = await SeedInstitutionAsync("os3");
        await SeedStaffAsync(institution.Id, "13933300013", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13933300013");

        var resp = await client.PostAsJsonAsync("/api/org/staff",
            new { name = "教师小王", phone = "13933300003", role = "Teacher" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("手机号已被占用", problem.GetProperty("detail").GetString());

        // Still exactly one staff row with that phone (the other institution's).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Staff.IgnoreQueryFilters()
                .CountAsync(s => s.Phone == "13933300003"));
        }

        // The check ignores the tenant filter but excludes nothing: a phone
        // already used in the caller's OWN institution also conflicts.
        var own = await client.PostAsJsonAsync("/api/org/staff",
            new { name = "重复手机号", phone = "13933300013", role = "Sales" });
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);

        // Cross-tenant staff ids are invisible through the tenant filter: 404.
        var deactivateOther = await PostAsync(client, $"/api/org/staff/{otherStaff.Id}/deactivate");
        Assert.Equal(HttpStatusCode.NotFound, deactivateOther.StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_self_returns_403()
    {
        var institution = await SeedInstitutionAsync("os4");
        var admin = await SeedStaffAsync(institution.Id, "13933300004", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13933300004");

        var resp = await PostAsync(client, $"/api/org/staff/{admin.Id}/deactivate");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("停用", problem.GetProperty("detail").GetString());

        // The self-deactivation was refused: the admin is still active.
        Assert.True((await GetStaffRowAsync(admin.Id)).IsActive);
    }

    [Fact]
    public async Task Reset_password_restores_123456_login()
    {
        var institution = await SeedInstitutionAsync("os5");
        await SeedStaffAsync(institution.Id, "13933300005", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13933300005");

        var created = await client.PostAsJsonAsync("/api/org/staff",
            new { name = "教师小张", phone = "13933300105", role = "Teacher" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var staffId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();

        // Replace the password directly in the DB: 123456 stops working.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var staff = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Id == staffId);
            staff.PasswordHash = hasher.Hash("changed-000105");
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffLoginAsync("13933300105")).StatusCode);

        var reset = await PostAsync(client, $"/api/org/staff/{staffId}/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13933300105")).StatusCode);

        // Deactivation blocks login with 403; activation restores it.
        var deactivate = await PostAsync(client, $"/api/org/staff/{staffId}/deactivate");
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        var blocked = await StaffLoginAsync("13933300105");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        var activate = await PostAsync(client, $"/api/org/staff/{staffId}/activate");
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13933300105")).StatusCode);

        // Unknown staff id → 404.
        var missing = await PostAsync(client, "/api/org/staff/999999/reset-password");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Demoting_last_active_admin_returns_409()
    {
        var institution = await SeedInstitutionAsync("os6");
        var admin = await SeedStaffAsync(institution.Id, "13933300006", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13933300006");

        // An invalid role string is a clean 400, not a 500.
        var bogus = await client.PutAsJsonAsync($"/api/org/staff/{admin.Id}", new { role = "Bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        var problem = await ProblemDetailAsync(bogus);
        Assert.Contains("角色", problem.GetProperty("detail").GetString());
        Assert.Equal(StaffRole.Admin, (await GetStaffRowAsync(admin.Id)).Role);

        // Demoting the only active admin (here: the admin itself) → 409, and
        // the role is unchanged.
        var demote = await client.PutAsJsonAsync($"/api/org/staff/{admin.Id}", new { role = "Sales" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        problem = await ProblemDetailAsync(demote);
        Assert.Contains("最后一个活跃管理员", problem.GetProperty("detail").GetString());
        Assert.Equal(StaffRole.Admin, (await GetStaffRowAsync(admin.Id)).Role);

        // Name-only updates never hit the admin rule (partial update works).
        var rename = await client.PutAsJsonAsync($"/api/org/staff/{admin.Id}", new { name = "管理员改名" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        var renamed = await GetStaffRowAsync(admin.Id);
        Assert.Equal("管理员改名", renamed.Name);
        Assert.Equal(StaffRole.Admin, renamed.Role);

        // With a second active admin present the demotion succeeds…
        var second = await SeedStaffAsync(institution.Id, "13933300206", StaffRole.Admin);
        var demoteAgain = await client.PutAsJsonAsync(
            $"/api/org/staff/{admin.Id}", new { role = "Sales" });
        Assert.Equal(HttpStatusCode.NoContent, demoteAgain.StatusCode);
        Assert.Equal(StaffRole.Sales, (await GetStaffRowAsync(admin.Id)).Role);

        // …leaving the caller's token stale (it still carries role=Admin, valid
        // for hours): the demoted caller must still not be able to deactivate
        // the LAST active admin — defense in depth for the stale-token window.
        var deactivate = await PostAsync(client, $"/api/org/staff/{second.Id}/deactivate");
        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.True((await GetStaffRowAsync(second.Id)).IsActive);
    }
}
