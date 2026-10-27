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
/// Integration tests for the course product management API (Task 6):
/// admin creates Fixed/Flexible products (name required ≤50, price ≥ 0,
/// sessionCount ≥ 1, type safe-parsed), products start unpublished and are
/// published/unpublished via explicit actions, Sales may read but never
/// write (AdminOrSales on GET, AdminOnly on writes), Teacher and FrontDesk
/// get 403 on every product route, and cross-tenant ids are 404.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff/products via factory.Services.CreateScope() and drives
/// every behavior over real HTTP with real tokens. Codes/phones are unique
/// per test because the class fixture shares one database.
/// </summary>
public class OrgProductTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The exact response shape of both the 201 create body and list items.</summary>
    private static readonly string[] ProductKeys =
        ["coverImagePath", "createdAt", "description", "id", "isPublished", "name", "price", "sessionCount", "type"];

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

    /// <summary>
    /// Seeds a product row straight into the DB (bypassing HTTP). The seeding
    /// scope has no tenant context, so InstitutionId must be set explicitly.
    /// </summary>
    private async Task<Product> SeedProductAsync(
        int institutionId,
        string name,
        ProductType type = ProductType.Fixed,
        decimal price = 1000,
        int sessionCount = 10,
        bool isPublished = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = type,
            Price = price,
            SessionCount = sessionCount,
            IsPublished = isPublished,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
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

    /// <summary>GETs the paged list and returns its items array.</summary>
    private static async Task<List<JsonElement>> ListItemsAsync(HttpClient client, string query = "")
    {
        var list = await client.GetAsync($"/api/org/products{query}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var paged = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            new[] { "items", "total" },
            paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        return paged.GetProperty("items").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Admin_creates_fixed_and_flexible_products_and_lists_them()
    {
        var institution = await SeedInstitutionAsync("op1");
        await SeedStaffAsync(institution.Id, "13955500001", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13955500001");

        // A Fixed product with a description: the 201 body is exactly the
        // documented shape, unpublished from the start.
        var fixedResp = await client.PostAsJsonAsync("/api/org/products",
            new { name = "暑期固定班", type = "Fixed", price = 1980m, sessionCount = 20, description = "周一三五 09:00-12:00" });
        Assert.Equal(HttpStatusCode.Created, fixedResp.StatusCode);
        var fixedProduct = await fixedResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProductKeys, fixedProduct.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var fixedId = fixedProduct.GetProperty("id").GetInt32();
        Assert.True(fixedId > 0);
        Assert.Equal("暑期固定班", fixedProduct.GetProperty("name").GetString());
        Assert.Equal("Fixed", fixedProduct.GetProperty("type").GetString());
        Assert.Equal(1980m, fixedProduct.GetProperty("price").GetDecimal());
        Assert.Equal(20, fixedProduct.GetProperty("sessionCount").GetInt32());
        Assert.Equal("周一三五 09:00-12:00", fixedProduct.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, fixedProduct.GetProperty("coverImagePath").ValueKind);
        Assert.False(fixedProduct.GetProperty("isPublished").GetBoolean());
        Assert.True(fixedProduct.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        // A Flexible product: type parsing is case-insensitive, description is
        // optional, and the response carries the canonical enum name.
        var flexibleResp = await client.PostAsJsonAsync("/api/org/products",
            new { name = "灵活课时包", type = "flexible", price = 2980.5m, sessionCount = 30 });
        Assert.Equal(HttpStatusCode.Created, flexibleResp.StatusCode);
        var flexibleProduct = await flexibleResp.Content.ReadFromJsonAsync<JsonElement>();
        var flexibleId = flexibleProduct.GetProperty("id").GetInt32();
        Assert.Equal("Flexible", flexibleProduct.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, flexibleProduct.GetProperty("description").ValueKind);

        // Name validation: required…
        var noName = await client.PostAsJsonAsync("/api/org/products",
            new { name = "", type = "Fixed", price = 100m, sessionCount = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        var problem = await ProblemDetailAsync(noName);
        Assert.Contains("名称", problem.GetProperty("detail").GetString());

        // …and at most 50 characters.
        var longName = await client.PostAsJsonAsync("/api/org/products",
            new { name = new string('名', 51), type = "Fixed", price = 100m, sessionCount = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);

        // The list shows both products with the documented item shape, by id.
        var items = await ListItemsAsync(client);
        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { fixedId, flexibleId }, items.Select(i => i.GetProperty("id").GetInt32()));
        foreach (var item in items)
        {
            Assert.Equal(ProductKeys, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        }

        var fixedRow = items.Single(i => i.GetProperty("id").GetInt32() == fixedId);
        Assert.Equal(1980m, fixedRow.GetProperty("price").GetDecimal());
        Assert.Equal("Fixed", fixedRow.GetProperty("type").GetString());
        Assert.False(fixedRow.GetProperty("isPublished").GetBoolean());

        // Optional type filter narrows the list…
        var fixedOnly = await ListItemsAsync(client, "?type=Fixed");
        Assert.Equal(fixedId, fixedOnly.Single().GetProperty("id").GetInt32());

        // …while invalid filter values are clean 400s, never 500s.
        var badType = await client.GetAsync("/api/org/products?type=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        problem = await ProblemDetailAsync(badType);
        Assert.Contains("类型", problem.GetProperty("detail").GetString());
        var badPublished = await client.GetAsync("/api/org/products?published=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, badPublished.StatusCode);

        // Partial update: only the sent fields change.
        var put = await client.PutAsJsonAsync($"/api/org/products/{fixedId}",
            new { name = "暑期固定班·升级", price = 2180m, sessionCount = 24, coverImagePath = "/uploads/covers/1.png" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var updated = (await ListItemsAsync(client, "?type=Fixed")).Single();
        Assert.Equal("暑期固定班·升级", updated.GetProperty("name").GetString());
        Assert.Equal(2180m, updated.GetProperty("price").GetDecimal());
        Assert.Equal(24, updated.GetProperty("sessionCount").GetInt32());
        Assert.Equal("/uploads/covers/1.png", updated.GetProperty("coverImagePath").GetString());
        Assert.Equal("周一三五 09:00-12:00", updated.GetProperty("description").GetString());

        // Cross-tenant rows stay invisible in the list…
        var other = await SeedInstitutionAsync("op1x");
        var otherProduct = await SeedProductAsync(other.Id, "别家机构课包");
        Assert.Equal(2, (await ListItemsAsync(client)).Count);

        // …and are addressable only as 404 (unknown ids equally so).
        var putOther = await client.PutAsJsonAsync(
            $"/api/org/products/{otherProduct.Id}", new { name = "越权改名" });
        Assert.Equal(HttpStatusCode.NotFound, putOther.StatusCode);
        var publishOther = await PostAsync(client, $"/api/org/products/{otherProduct.Id}/publish");
        Assert.Equal(HttpStatusCode.NotFound, publishOther.StatusCode);
        var publishMissing = await PostAsync(client, "/api/org/products/999999/publish");
        Assert.Equal(HttpStatusCode.NotFound, publishMissing.StatusCode);
    }

    [Fact]
    public async Task SessionCount_zero_returns_400()
    {
        var institution = await SeedInstitutionAsync("op2");
        await SeedStaffAsync(institution.Id, "13955500002", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13955500002");
        var product = await SeedProductAsync(institution.Id, "既有课包", sessionCount: 10);

        var resp = await client.PostAsJsonAsync("/api/org/products",
            new { name = "零课时课包", type = "Fixed", price = 500m, sessionCount = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("课时", problem.GetProperty("detail").GetString());

        // Negative session counts are equally invalid…
        var negative = await client.PostAsJsonAsync("/api/org/products",
            new { name = "负课时课包", type = "Fixed", price = 500m, sessionCount = -3 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        // …and so is an unknown course type (safe enum parse, never a 500).
        var badType = await client.PostAsJsonAsync("/api/org/products",
            new { name = "坏类型课包", type = "Bogus", price = 500m, sessionCount = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        problem = await ProblemDetailAsync(badType);
        Assert.Contains("类型", problem.GetProperty("detail").GetString());

        // Updates validate the same way…
        var put = await client.PutAsJsonAsync($"/api/org/products/{product.Id}", new { sessionCount = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // …and no rejected payload ever reached the database.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Products.IgnoreQueryFilters()
                .AnyAsync(p => p.Name == "零课时课包" || p.Name == "负课时课包" || p.Name == "坏类型课包"));
            Assert.Equal(10, (await db.Products.IgnoreQueryFilters()
                .SingleAsync(p => p.Id == product.Id)).SessionCount);
        }
    }

    [Fact]
    public async Task Negative_price_returns_400()
    {
        var institution = await SeedInstitutionAsync("op3");
        await SeedStaffAsync(institution.Id, "13955500003", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13955500003");

        var resp = await client.PostAsJsonAsync("/api/org/products",
            new { name = "负价课包", type = "Flexible", price = -0.01m, sessionCount = 12 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("价格", problem.GetProperty("detail").GetString());

        // Price 0 is the valid lower bound (a free pack).
        var free = await client.PostAsJsonAsync("/api/org/products",
            new { name = "免费体验课包", type = "Flexible", price = 0m, sessionCount = 12 });
        Assert.Equal(HttpStatusCode.Created, free.StatusCode);
        var freeProduct = await free.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0m, freeProduct.GetProperty("price").GetDecimal());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Name == "负价课包"));
        }
    }

    [Fact]
    public async Task Sales_can_read_but_not_create_returns_403()
    {
        var institution = await SeedInstitutionAsync("op4");
        await SeedStaffAsync(institution.Id, "13955500004", StaffRole.Admin);
        await SeedStaffAsync(institution.Id, "13955500404", StaffRole.Sales);
        var product = await SeedProductAsync(institution.Id, "销售可见课包", isPublished: true);
        var other = await SeedInstitutionAsync("op4x");
        await SeedProductAsync(other.Id, "别家机构课包");

        var sales = await LoginStaffClientAsync("13955500404");

        // Reads work under AdminOrSales and stay tenant-scoped.
        var items = await ListItemsAsync(sales);
        Assert.Equal(product.Id, items.Single().GetProperty("id").GetInt32());

        // Every write is 403…
        var create = await sales.PostAsJsonAsync("/api/org/products",
            new { name = "销售私建课包", type = "Fixed", price = 100m, sessionCount = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        var put = await sales.PutAsJsonAsync($"/api/org/products/{product.Id}", new { name = "销售改名" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var publish = await PostAsync(sales, $"/api/org/products/{product.Id}/publish");
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        var unpublish = await PostAsync(sales, $"/api/org/products/{product.Id}/unpublish");
        Assert.Equal(HttpStatusCode.Forbidden, unpublish.StatusCode);

        // …and the rejected writes changed nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Name == "销售私建课包"));
            var unchanged = await db.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == product.Id);
            Assert.Equal("销售可见课包", unchanged.Name);
            Assert.True(unchanged.IsPublished);
        }
    }

    [Fact]
    public async Task Teacher_cannot_access_products_returns_403()
    {
        var institution = await SeedInstitutionAsync("op5");
        await SeedStaffAsync(institution.Id, "13955500005", StaffRole.Admin);
        await SeedStaffAsync(institution.Id, "13955500505", StaffRole.Teacher);
        await SeedStaffAsync(institution.Id, "13955500506", StaffRole.FrontDesk);
        var product = await SeedProductAsync(institution.Id, "教师禁区课包");

        // Teacher: every product route is 403 (matrix "—").
        var teacher = await LoginStaffClientAsync("13955500505");
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.GetAsync("/api/org/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.PostAsJsonAsync("/api/org/products",
            new { name = "教师私建课包", type = "Fixed", price = 100m, sessionCount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.PutAsJsonAsync(
            $"/api/org/products/{product.Id}", new { name = "教师改名" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(teacher, $"/api/org/products/{product.Id}/publish")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(teacher, $"/api/org/products/{product.Id}/unpublish")).StatusCode);

        // FrontDesk is equally locked out (matrix parity).
        var frontDesk = await LoginStaffClientAsync("13955500506");
        Assert.Equal(HttpStatusCode.Forbidden, (await frontDesk.GetAsync("/api/org/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await frontDesk.PostAsJsonAsync("/api/org/products",
            new { name = "前台私建课包", type = "Fixed", price = 100m, sessionCount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await frontDesk.PutAsJsonAsync(
            $"/api/org/products/{product.Id}", new { name = "前台改名" })).StatusCode);
    }

    [Fact]
    public async Task Unpublished_product_excluded_when_published_filter_true()
    {
        var institution = await SeedInstitutionAsync("op6");
        await SeedStaffAsync(institution.Id, "13955500006", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13955500006");

        var created = await client.PostAsJsonAsync("/api/org/products",
            new { name = "待发布课包", type = "Fixed", price = 880m, sessionCount = 16 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        Assert.False(body.GetProperty("isPublished").GetBoolean());

        // published=true hides the fresh product…
        var publishedOnly = await ListItemsAsync(client, "?published=true");
        Assert.Empty(publishedOnly);

        // …while published=false surfaces it.
        var unpublishedOnly = await ListItemsAsync(client, "?published=false");
        Assert.Equal(id, unpublishedOnly.Single().GetProperty("id").GetInt32());

        // Publishing is the explicit action that flips it.
        var publish = await PostAsync(client, $"/api/org/products/{id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
        Assert.Equal(id, (await ListItemsAsync(client, "?published=true")).Single().GetProperty("id").GetInt32());
        Assert.Empty(await ListItemsAsync(client, "?published=false"));
        Assert.Single(await ListItemsAsync(client));

        // Unpublishing reverses it.
        var unpublish = await PostAsync(client, $"/api/org/products/{id}/unpublish");
        Assert.Equal(HttpStatusCode.NoContent, unpublish.StatusCode);
        Assert.Empty(await ListItemsAsync(client, "?published=true"));
    }
}
