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
/// Integration tests for the platform-superadmin institution management API
/// (Task 4): provisioning (create + the new admin logs in with the initial
/// password), code format / duplicate / phone-collision handling, listing
/// with counts and paging, partial update, disable/enable and admin password
/// reset, plus the PlatformOnly policy gate deferred from Task 3.
///
/// ApiFactory runs with SeedDemoData=false, so tests seed the platform user
/// and extra rows via factory.Services.CreateScope() and drive every behavior
/// over real HTTP. Codes/phones are unique per test because the class fixture
/// shares one database.
/// </summary>
public class PlatformInstitutionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string PlatformUsername = "plat4admin";

    private static Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client,
        string code,
        string adminPhone,
        string? name = null,
        string? adminName = null,
        string? contactPhone = null,
        string? address = null,
        string? introText = null) =>
        client.PostAsJsonAsync("/api/platform/institutions", new
        {
            code,
            name = name ?? $"机构{code}",
            adminName = adminName ?? $"管理员{code}",
            adminPhone,
            contactPhone,
            address,
            introText,
        });

    private static async Task<JsonElement> CreateInstitutionOkAsync(
        HttpClient client, string code, string adminPhone,
        string? contactPhone = null, string? address = null, string? introText = null)
    {
        var resp = await PostCreateAsync(
            client, code, adminPhone, contactPhone: contactPhone, address: address, introText: introText);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Seeds (once per database) and logs in the platform superadmin over real HTTP.</summary>
    private async Task<HttpClient> LoginPlatformAsync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            if (!await db.PlatformUsers.AnyAsync(u => u.Username == PlatformUsername))
            {
                db.PlatformUsers.Add(new PlatformUser
                {
                    Username = PlatformUsername,
                    PasswordHash = hasher.Hash(DefaultPassword.Initial),
                });
                await db.SaveChangesAsync();
            }
        }

        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/platform/auth/login",
            new { username = PlatformUsername, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return AuthedClient(token!);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpResponseMessage> StaffLoginAsync(string phone, string password = DefaultPassword.Initial)
    {
        var client = factory.CreateClient();
        return client.PostAsJsonAsync("/api/org/auth/login", new { phone, password });
    }

    private async Task<Institution> SeedInstitutionDirectAsync(
        string code, InstitutionStatus status = InstitutionStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var institution = new Institution { Code = code, Name = $"机构{code}", Status = status };
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        return institution;
    }

    private async Task<Staff> SeedStaffDirectAsync(
        int institutionId, string phone, string? password = null,
        StaffRole role = StaffRole.Admin, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var staff = new Staff
        {
            InstitutionId = institutionId,
            Name = $"员工{phone}",
            Phone = phone,
            PasswordHash = hasher.Hash(password ?? DefaultPassword.Initial),
            Role = role,
            IsActive = isActive,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    private async Task SeedStudentsDirectAsync(int institutionId, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.Students.Add(new Student
            {
                InstitutionId = institutionId,
                Name = $"学员{institutionId}-{i}",
                Phone = $"137{institutionId:0000000}{i:000}",
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_institution_then_its_admin_can_login_with_initial_password()
    {
        var client = await LoginPlatformAsync();

        var resp = await PostCreateAsync(client, "pi1", "13922200001");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // 201 body is exactly {id,code,name}.
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.Equal("pi1", created.GetProperty("code").GetString());
        Assert.Equal("机构pi1", created.GetProperty("name").GetString());
        Assert.Equal(
            new[] { "code", "id", "name" }.OrderBy(n => n),
            created.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        // The provisioned admin logs in with the initial password right away.
        var login = await StaffLoginAsync("13922200001");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("institutionId").GetInt32());
        Assert.Equal("pi1", body.GetProperty("institutionCode").GetString());
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.Equal("管理员pi1", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Create_with_duplicate_code_returns_409()
    {
        var client = await LoginPlatformAsync();

        var first = await PostCreateAsync(client, "pi2", "13922200002");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var dup = await PostCreateAsync(client, "pi2", "13922200012");
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var problem = await ProblemDetailAsync(dup);
        Assert.Contains("机构编码", problem.GetProperty("detail").GetString());

        // The failed create leaves no partial rows behind (transactional creation).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Institutions.CountAsync(i => i.Code == "pi2"));
            Assert.False(await db.Staff.IgnoreQueryFilters()
                .AnyAsync(s => s.Phone == "13922200012"));
        }
    }

    [Fact]
    public async Task Create_with_invalid_code_format_returns_400()
    {
        var client = await LoginPlatformAsync();

        // 3–32 chars, lowercase letters/digits/hyphens, alphanumeric at both ends.
        var invalidCodes = new[]
        {
            "a",                                          // too short (min 3)
            "-bad",                                       // leading hyphen
            "Bad_Code",                                   // uppercase + underscore
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",          // 33 chars (max 32)
        };
        foreach (var code in invalidCodes)
        {
            var resp = await PostCreateAsync(client, code, "13922200003");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await ProblemDetailAsync(resp);
            Assert.Contains("机构编码", problem.GetProperty("detail").GetString());
        }

        // Format is validated BEFORE the duplicate check: a code that is both
        // invalid-format and already present must return 400, not 409.
        await SeedInstitutionDirectAsync("PI3!");
        var bothBad = await PostCreateAsync(client, "PI3!", "13922200013");
        Assert.Equal(HttpStatusCode.BadRequest, bothBad.StatusCode);
    }

    [Fact]
    public async Task Create_with_phone_already_used_by_staff_returns_409()
    {
        var client = await LoginPlatformAsync();

        // Existing staff in another institution; the create runs under a
        // platform token (InstitutionId=null), so the phone-collision lookup
        // only sees this row if it bypasses the tenant filter.
        var other = await SeedInstitutionDirectAsync("pi4a");
        await SeedStaffDirectAsync(other.Id, "13922200004");

        var resp = await PostCreateAsync(client, "pi4", "13922200004");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("手机号已被占用", problem.GetProperty("detail").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Institutions.AnyAsync(i => i.Code == "pi4"));
        }
    }

    [Fact]
    public async Task Disable_institution_blocks_staff_login_with_403_and_enable_restores()
    {
        var client = await LoginPlatformAsync();
        var created = await CreateInstitutionOkAsync(client, "pi5", "13922200005");
        var id = created.GetProperty("id").GetInt32();

        // Precondition: the admin can log in.
        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13922200005")).StatusCode);

        var disable = await client.PostAsync($"/api/platform/institutions/{id}/disable", content: null);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        var blocked = await StaffLoginAsync("13922200005");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var problem = await ProblemDetailAsync(blocked);
        Assert.Contains("机构已停用", problem.GetProperty("detail").GetString());

        // The list reflects the new status.
        var disabledList = await client.GetAsync("/api/platform/institutions?status=Disabled");
        Assert.Equal(HttpStatusCode.OK, disabledList.StatusCode);
        var paged = await disabledList.Content.ReadFromJsonAsync<JsonElement>();
        var row = paged.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal("pi5", row.GetProperty("code").GetString());
        Assert.Equal("Disabled", row.GetProperty("status").GetString());

        var activeList = await client.GetAsync("/api/platform/institutions?status=Active");
        var activePaged = await activeList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(activePaged.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetInt32() == id);

        var enable = await client.PostAsync($"/api/platform/institutions/{id}/enable", content: null);
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13922200005")).StatusCode);
    }

    [Fact]
    public async Task Reset_admin_password_allows_login_with_123456_again()
    {
        var client = await LoginPlatformAsync();
        var created = await CreateInstitutionOkAsync(client, "pi6", "13922200006");
        var id = created.GetProperty("id").GetInt32();

        // A second (higher-Id) active admin: the reset must target the FIRST
        // active admin ordered by Id, not this one.
        await SeedStaffDirectAsync(id, "13922200106", password: "custom-0026");

        // The first admin no longer uses the initial password.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var admin = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Phone == "13922200006");
            admin.PasswordHash = hasher.Hash("changed-000006");
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffLoginAsync("13922200006")).StatusCode);

        var reset = await client.PostAsync($"/api/platform/institutions/{id}/reset-admin-password", content: null);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The first admin logs in with 123456 again…
        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13922200006")).StatusCode);
        // …while the second admin is untouched: 123456 fails, its own password still works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await StaffLoginAsync("13922200106")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StaffLoginAsync("13922200106", "custom-0026")).StatusCode);

        // Unknown institution → 404.
        var missing = await client.PostAsync("/api/platform/institutions/999999/reset-admin-password", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Institution with no active admin → 404.
        var noAdmin = await SeedInstitutionDirectAsync("pi6b");
        await SeedStaffDirectAsync(noAdmin.Id, "13922200116", isActive: false);
        var none = await client.PostAsync($"/api/platform/institutions/{noAdmin.Id}/reset-admin-password", content: null);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task Staff_token_accessing_platform_institutions_returns_403()
    {
        var client = await LoginPlatformAsync();
        await CreateInstitutionOkAsync(client, "pi7", "13922200007");

        var login = await StaffLoginAsync("13922200007");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var staffClient = AuthedClient(token);

        // The PlatformOnly policy rejects staff tokens on the whole controller.
        var get = await staffClient.GetAsync("/api/platform/institutions");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);

        var post = await PostCreateAsync(staffClient, "pi7b", "13922200017");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        // Anonymous callers are challenged, and the platform token still works.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync("/api/platform/institutions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/institutions")).StatusCode);
    }

    /// <summary>
    /// Pins the list contract: Paged shape {total,items}, per-institution
    /// staff/student counts, optional status filter and page/pageSize paging.
    /// </summary>
    [Fact]
    public async Task List_returns_institutions_with_counts_and_paging()
    {
        var client = await LoginPlatformAsync();
        var a = await CreateInstitutionOkAsync(client, "pi8a", "13922200008");
        var aId = a.GetProperty("id").GetInt32();
        var b = await CreateInstitutionOkAsync(client, "pi8b", "13922200018");

        // pi8a: provisioned admin + one extra staff, two students.
        await SeedStaffDirectAsync(aId, "13922200108");
        await SeedStudentsDirectAsync(aId, 2);

        var resp = await client.GetAsync("/api/platform/institutions?status=Active&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var paged = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // Paged envelope is exactly {total,items}.
        Assert.Equal(
            new[] { "items", "total" }.OrderBy(n => n),
            paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        var total = paged.GetProperty("total").GetInt32();
        var items = paged.GetProperty("items").EnumerateArray().ToList();
        Assert.True(total >= 2);
        Assert.Equal(total, items.Count); // everything fits in pageSize=100

        var rowA = items.Single(i => i.GetProperty("code").GetString() == "pi8a");
        Assert.Equal(aId, rowA.GetProperty("id").GetInt32());
        Assert.Equal("机构pi8a", rowA.GetProperty("name").GetString());
        Assert.Equal("Active", rowA.GetProperty("status").GetString());
        Assert.Equal(2, rowA.GetProperty("staffCount").GetInt32());
        Assert.Equal(2, rowA.GetProperty("studentCount").GetInt32());
        Assert.True(rowA.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        var rowB = items.Single(i => i.GetProperty("code").GetString() == "pi8b");
        Assert.Equal(b.GetProperty("id").GetInt32(), rowB.GetProperty("id").GetInt32());
        Assert.Equal(1, rowB.GetProperty("staffCount").GetInt32());
        Assert.Equal(0, rowB.GetProperty("studentCount").GetInt32());

        // Item shape is exactly the seven documented fields.
        Assert.Equal(
            new[] { "code", "createdAt", "id", "name", "staffCount", "status", "studentCount" }.OrderBy(n => n),
            rowA.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        // pageSize caps the page size while total stays the full count.
        var page1 = await client.GetAsync("/api/platform/institutions?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var p1 = await page1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(p1.GetProperty("items").EnumerateArray());
        Assert.Equal(total, p1.GetProperty("total").GetInt32());

        // Default parameters work too.
        var noParams = await client.GetAsync("/api/platform/institutions");
        Assert.Equal(HttpStatusCode.OK, noParams.StatusCode);
    }

    /// <summary>Pins the PUT contract: only non-null fields update; unknown id → 404.</summary>
    [Fact]
    public async Task Update_institution_changes_only_provided_fields()
    {
        var client = await LoginPlatformAsync();
        var created = await CreateInstitutionOkAsync(
            client, "pi9", "13922200009",
            contactPhone: "021-60000009", address: "浦东1号", introText: "简介原文");
        var id = created.GetProperty("id").GetInt32();

        var rename = await client.PutAsJsonAsync($"/api/platform/institutions/{id}", new { name = "改名机构" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var institution = await db.Institutions.SingleAsync(i => i.Id == id);
            Assert.Equal("改名机构", institution.Name);
            Assert.Equal("021-60000009", institution.ContactPhone);
            Assert.Equal("浦东1号", institution.Address);
            Assert.Equal("简介原文", institution.IntroText);
        }

        var partial = await client.PutAsJsonAsync(
            $"/api/platform/institutions/{id}", new { introText = "新简介", contactPhone = "021-60000109" });
        Assert.Equal(HttpStatusCode.NoContent, partial.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var institution = await db.Institutions.SingleAsync(i => i.Id == id);
            Assert.Equal("改名机构", institution.Name);
            Assert.Equal("新简介", institution.IntroText);
            Assert.Equal("021-60000109", institution.ContactPhone);
            Assert.Equal("浦东1号", institution.Address);
        }

        var missing = await client.PutAsJsonAsync("/api/platform/institutions/999999", new { name = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
