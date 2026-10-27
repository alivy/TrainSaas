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
/// Integration tests for the org-side purchase-intent funnel (Task 8): the
/// lead list is AdminSalesFrontDesk (Teacher 403, cross-tenant rows invisible),
/// follow is AdminOrSales with the exact conflict rule (allowed only when
/// Status=New or the intent is already the caller's own, otherwise 409), and
/// mark-converted/mark-invalid just set the status — their optional
/// {orderId}/{reason} bodies are accepted but deliberately not persisted
/// (PurchaseIntent has no such columns; documented YAGNI). The lead itself is
/// created through the real anonymous endpoint so the persisted row's
/// InstitutionId proves the public-path tenant fill end-to-end.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff/intents via factory.Services.CreateScope() and drives
/// every behavior over real HTTP with real tokens. Codes/phones are unique
/// per test because the class fixture shares one database. Rate-limit budget:
/// this class issues exactly one anonymous POST to the rate-limited intents
/// endpoint, far below the 5/min shared window of its host.
/// </summary>
public class OrgIntentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The exact response shape of the lead list items.</summary>
    private static readonly string[] ItemKeys =
    [
        "createdAt", "followedByName", "followedByStaffId", "id", "message",
        "name", "phone", "productId", "productName", "status",
    ];

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
        int institutionId, string phone, StaffRole role = StaffRole.Admin)
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
            // Entity default is false — seeded staff must log in, so activate explicitly.
            IsActive = true,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    private async Task<Product> SeedProductAsync(int institutionId, string name, bool isPublished)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = ProductType.Fixed,
            Price = 1980m,
            SessionCount = 20,
            IsPublished = isPublished,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>
    /// Seeds an intent row straight into the DB (bypassing HTTP). The seeding
    /// scope has no tenant context, so InstitutionId must be set explicitly.
    /// </summary>
    private async Task<PurchaseIntent> SeedIntentAsync(
        int institutionId,
        string? name,
        string phone,
        int? productId = null,
        string? message = null,
        IntentStatus status = IntentStatus.New,
        int? followedByStaffId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var intent = new PurchaseIntent
        {
            InstitutionId = institutionId,
            Name = name,
            Phone = phone,
            ProductId = productId,
            Message = message,
            Status = status,
            FollowedByStaffId = followedByStaffId,
        };
        db.PurchaseIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent;
    }

    /// <summary>
    /// Creates a purchase intent over the anonymous public endpoint (the same
    /// funnel a landing-page visitor uses) and returns its id.
    /// </summary>
    private async Task<int> CreateIntentViaHttpAsync(string code, object body)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/public/orgs/{code}/intents", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetInt32();
    }

    /// <summary>Logs a staff member in over real HTTP and returns a bearer-authed client.</summary>
    private async Task<HttpClient> LoginStaffClientAsync(string phone)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/org/auth/login", new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return AuthedClient(token!);
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
    public async Task Sales_follow_sets_status_and_owner_second_sales_gets_409()
    {
        var institution = await SeedInstitutionAsync("oi1");
        var product = await SeedProductAsync(institution.Id, "在售课包", isPublished: true);
        var sales1 = await SeedStaffAsync(institution.Id, "13977700001", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977700002", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977700003", StaffRole.FrontDesk);
        var other = await SeedInstitutionAsync("oi1x");
        await SeedStaffAsync(other.Id, "13977700004", StaffRole.Sales);

        // Anonymous submission — the row's tenant tag can only come from the
        // institution code, since the request carries no tenant context.
        var intentId = await CreateIntentViaHttpAsync("oi1",
            new { name = "赵女士", phone = "13877770001", productId = product.Id, message = "请回电" });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PurchaseIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(institution.Id, row.InstitutionId);
            Assert.Equal(IntentStatus.New, row.Status);
            Assert.Null(row.FollowedByStaffId);
        }

        // The first sales rep claims the lead: New → Followed, owner recorded.
        var sales1Client = await LoginStaffClientAsync("13977700001");
        var follow = await PostAsync(sales1Client, $"/api/org/intents/{intentId}/follow");
        Assert.Equal(HttpStatusCode.NoContent, follow.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PurchaseIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(IntentStatus.Followed, row.Status);
            Assert.Equal(sales1.Id, row.FollowedByStaffId);
        }

        // A second sales rep cannot steal a followed lead: 409, nothing changes.
        var sales2Client = await LoginStaffClientAsync("13977700002");
        var conflict = await PostAsync(sales2Client, $"/api/org/intents/{intentId}/follow");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await ProblemDetailAsync(conflict);
        Assert.Contains("跟进", problem.GetProperty("detail").GetString());

        // The owner may re-follow (Status != New but FollowedByStaffId == self).
        var refollow = await PostAsync(sales1Client, $"/api/org/intents/{intentId}/follow");
        Assert.Equal(HttpStatusCode.NoContent, refollow.StatusCode);

        // FrontDesk may read the funnel but is not allowed to follow.
        var frontDeskClient = await LoginStaffClientAsync("13977700003");
        Assert.Equal(HttpStatusCode.OK, (await frontDeskClient.GetAsync("/api/org/intents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(frontDeskClient, $"/api/org/intents/{intentId}/follow")).StatusCode);

        // Cross-institution sales cannot even see the lead: 404, like unknown ids.
        var otherClient = await LoginStaffClientAsync("13977700004");
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAsync(otherClient, $"/api/org/intents/{intentId}/follow")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAsync(sales1Client, "/api/org/intents/999999/follow")).StatusCode);

        // None of the rejected attempts changed the lead.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PurchaseIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(IntentStatus.Followed, row.Status);
            Assert.Equal(sales1.Id, row.FollowedByStaffId);
        }
    }

    [Fact]
    public async Task Mark_converted_sets_status_Converted()
    {
        var institution = await SeedInstitutionAsync("oi2");
        await SeedStaffAsync(institution.Id, "13977700010", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977700011", StaffRole.FrontDesk);
        var product = await SeedProductAsync(institution.Id, "转化课包", isPublished: true);

        // New intents may be converted/invalidated directly — no follow step
        // is required first.
        var converted = await SeedIntentAsync(institution.Id, "钱先生", "13877770010", product.Id);
        var invalidated = await SeedIntentAsync(institution.Id, "孙女士", "13877770011");
        var other = await SeedInstitutionAsync("oi2x");
        var crossTenant = await SeedIntentAsync(other.Id, "别家意向", "13877770012");

        var sales = await LoginStaffClientAsync("13977700010");

        // mark-converted accepts an optional {orderId} — PurchaseIntent has no
        // OrderId column, so it is deliberately not persisted (YAGNI).
        var mark = await sales.PostAsJsonAsync(
            $"/api/org/intents/{converted.Id}/mark-converted", new { orderId = 5 });
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        // mark-invalid equally tolerates an empty body ({reason} never stored).
        var invalid = await PostAsync(sales, $"/api/org/intents/{invalidated.Id}/mark-invalid");
        Assert.Equal(HttpStatusCode.NoContent, invalid.StatusCode);

        // Unknown and cross-tenant ids are 404…
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAsync(sales, "/api/org/intents/999999/mark-converted")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAsync(sales, $"/api/org/intents/{crossTenant.Id}/mark-converted")).StatusCode);

        // …and FrontDesk cannot mark at all (AdminOrSales only).
        var frontDesk = await LoginStaffClientAsync("13977700011");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(frontDesk, $"/api/org/intents/{converted.Id}/mark-converted")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(frontDesk, $"/api/org/intents/{invalidated.Id}/mark-invalid")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var convertedRow = await db.PurchaseIntents.IgnoreQueryFilters()
                .SingleAsync(i => i.Id == converted.Id);
            Assert.Equal(IntentStatus.Converted, convertedRow.Status);
            // No side effects beyond the status.
            Assert.Null(convertedRow.FollowedByStaffId);
            Assert.Equal("钱先生", convertedRow.Name);

            var invalidRow = await db.PurchaseIntents.IgnoreQueryFilters()
                .SingleAsync(i => i.Id == invalidated.Id);
            Assert.Equal(IntentStatus.Invalid, invalidRow.Status);

            // The cross-tenant row was never touched.
            var crossRow = await db.PurchaseIntents.IgnoreQueryFilters()
                .SingleAsync(i => i.Id == crossTenant.Id);
            Assert.Equal(IntentStatus.New, crossRow.Status);
        }
    }

    [Fact]
    public async Task Intents_list_filters_by_status_and_is_role_scoped()
    {
        var institution = await SeedInstitutionAsync("oi3");
        var product = await SeedProductAsync(institution.Id, "列表课包", isPublished: true);
        await SeedStaffAsync(institution.Id, "13977700020", StaffRole.Admin);
        var sales = await SeedStaffAsync(institution.Id, "13977700021", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977700022", StaffRole.Teacher);
        await SeedStaffAsync(institution.Id, "13977700023", StaffRole.FrontDesk);
        var other = await SeedInstitutionAsync("oi3x");
        await SeedIntentAsync(other.Id, "别家意向", "13877770020");

        var newIntent = await SeedIntentAsync(institution.Id, "新线索", "13877770021", product.Id, "想报班");
        var followed = await SeedIntentAsync(
            institution.Id, "跟进中线索", "13877770022",
            status: IntentStatus.Followed, followedByStaffId: sales.Id);
        var converted = await SeedIntentAsync(institution.Id, null, "13877770023", status: IntentStatus.Converted);
        var invalid = await SeedIntentAsync(institution.Id, "作废线索", "13877770024", status: IntentStatus.Invalid);

        var adminClient = await LoginStaffClientAsync("13977700020");
        var list = await adminClient.GetAsync("/api/org/intents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var paged = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["items", "total"], paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        // The cross-tenant row is invisible: exactly this institution's four
        // leads, ordered by id.
        Assert.Equal(4, paged.GetProperty("total").GetInt32());
        var items = paged.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            new[] { newIntent.Id, followed.Id, converted.Id, invalid.Id },
            items.Select(i => i.GetProperty("id").GetInt32()));
        foreach (var item in items)
        {
            Assert.Equal(ItemKeys, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        }

        var newItem = items.Single(i => i.GetProperty("id").GetInt32() == newIntent.Id);
        Assert.Equal("新线索", newItem.GetProperty("name").GetString());
        Assert.Equal("13877770021", newItem.GetProperty("phone").GetString());
        Assert.Equal(product.Id, newItem.GetProperty("productId").GetInt32());
        Assert.Equal("列表课包", newItem.GetProperty("productName").GetString());
        Assert.Equal("想报班", newItem.GetProperty("message").GetString());
        Assert.Equal("New", newItem.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, newItem.GetProperty("followedByStaffId").ValueKind);
        Assert.Equal(JsonValueKind.Null, newItem.GetProperty("followedByName").ValueKind);
        Assert.True(newItem.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        var followedItem = items.Single(i => i.GetProperty("id").GetInt32() == followed.Id);
        Assert.Equal("Followed", followedItem.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, followedItem.GetProperty("productId").ValueKind);
        Assert.Equal(JsonValueKind.Null, followedItem.GetProperty("productName").ValueKind);
        Assert.Equal(sales.Id, followedItem.GetProperty("followedByStaffId").GetInt32());
        Assert.Equal(sales.Name, followedItem.GetProperty("followedByName").GetString());

        // status is safe-parsed (case-insensitive) and narrows the list…
        var newOnly = await adminClient.GetAsync("/api/org/intents?status=New");
        var newPaged = await newOnly.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, newPaged.GetProperty("total").GetInt32());
        Assert.Equal(newIntent.Id, newPaged.GetProperty("items")[0].GetProperty("id").GetInt32());

        var lowerFilter = await adminClient.GetAsync("/api/org/intents?status=converted");
        var lowerPaged = await lowerFilter.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, lowerPaged.GetProperty("total").GetInt32());
        Assert.Equal(converted.Id, lowerPaged.GetProperty("items")[0].GetProperty("id").GetInt32());

        // …invalid values are clean 400s, never 500s…
        var bad = await adminClient.GetAsync("/api/org/intents?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await ProblemDetailAsync(bad);
        Assert.Contains("状态", problem.GetProperty("detail").GetString());

        // …and paging follows the shared Paged contract.
        var paged2 = await adminClient.GetAsync("/api/org/intents?page=1&pageSize=2");
        var two = await paged2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, two.GetProperty("total").GetInt32());
        Assert.Equal(2, two.GetProperty("items").GetArrayLength());

        // Teacher is excluded from the whole funnel (policy excludes it)…
        var teacher = await LoginStaffClientAsync("13977700022");
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.GetAsync("/api/org/intents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(teacher, $"/api/org/intents/{newIntent.Id}/follow")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(teacher, $"/api/org/intents/{newIntent.Id}/mark-converted")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(teacher, $"/api/org/intents/{newIntent.Id}/mark-invalid")).StatusCode);

        // …while FrontDesk may read the same tenant-scoped funnel.
        var frontDeskClient = await LoginStaffClientAsync("13977700023");
        var frontDeskList = await frontDeskClient.GetAsync("/api/org/intents");
        Assert.Equal(HttpStatusCode.OK, frontDeskList.StatusCode);
        var frontDeskPaged = await frontDeskList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, frontDeskPaged.GetProperty("total").GetInt32());
    }
}
