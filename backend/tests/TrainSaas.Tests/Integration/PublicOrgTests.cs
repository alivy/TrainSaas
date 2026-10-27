using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// Integration tests for the anonymous public org page and purchase-intent
/// submission (Task 8): the page only lists the active institution's published
/// products (ordered by id), disabled or unknown institution codes answer an
/// identical 404 (no existence leak), and the anonymous intent submission
/// validates phone (^1\d{10}$), foreign/unpublished productId and message
/// length before persisting exactly one row whose InstitutionId was filled
/// from the institution code — anonymous requests carry no tenant context, so
/// the row's tenant tag proves the IgnoreQueryFilters exemption path works.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/products via factory.Services.CreateScope(). Codes/phones are
/// unique per test because the class fixture shares one database.
///
/// Rate-limit budget: the "intent" fixed window (5 requests/minute, partitioned
/// by remote IP) is shared by every request seen by this class fixture's host,
/// and all test clients share the same loopback address. The tests below keep
/// the class-wide POST count to the intents endpoint at exactly 5 — at the
/// permit limit but never above it, so nothing here ever observes a 429. The
/// 429 behavior itself lives in <see cref="PublicOrgRateLimitTests"/> on its
/// own dedicated host.
/// </summary>
public class PublicOrgTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The exact top-level response shape of the public org page.</summary>
    private static readonly string[] OrgKeys =
        ["address", "code", "contactPhone", "introText", "logoPath", "name", "products"];

    /// <summary>The exact shape of the product items on the public page.</summary>
    private static readonly string[] ProductKeys =
        ["coverImagePath", "description", "id", "name", "price", "sessionCount", "type"];

    private async Task<Institution> SeedInstitutionAsync(
        string code,
        InstitutionStatus status = InstitutionStatus.Active,
        string? logoPath = null,
        string? introText = null,
        string? contactPhone = null,
        string? address = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var institution = new Institution
        {
            Code = code,
            Name = $"机构{code}",
            Status = status,
            LogoPath = logoPath,
            IntroText = introText,
            ContactPhone = contactPhone,
            Address = address,
        };
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        return institution;
    }

    /// <summary>
    /// Seeds a product row straight into the DB (bypassing HTTP). The seeding
    /// scope has no tenant context, so InstitutionId must be set explicitly.
    /// </summary>
    private async Task<Product> SeedProductAsync(
        int institutionId,
        string name,
        bool isPublished,
        ProductType type = ProductType.Fixed,
        decimal price = 1980m,
        int sessionCount = 20,
        string? description = null,
        string? coverImagePath = null)
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
            Description = description,
            CoverImagePath = coverImagePath,
            IsPublished = isPublished,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Public_org_page_returns_only_published_products()
    {
        var institution = await SeedInstitutionAsync(
            "pg1",
            logoPath: "/uploads/logos/pg1.png",
            introText: "专注少儿编程启蒙",
            contactPhone: "021-63510000",
            address: "上海市浦东新区张江路 1 号");
        var fixedPublished = await SeedProductAsync(
            institution.Id, "暑期固定班", isPublished: true,
            type: ProductType.Fixed, price: 1980m, sessionCount: 20,
            description: "周一三五 09:00-12:00", coverImagePath: "/uploads/covers/pg1a.png");
        var flexiblePublished = await SeedProductAsync(
            institution.Id, "灵活课时包", isPublished: true,
            type: ProductType.Flexible, price: 2980.5m, sessionCount: 30);
        await SeedProductAsync(institution.Id, "草稿课包", isPublished: false);
        var other = await SeedInstitutionAsync("pg1x");
        await SeedProductAsync(other.Id, "别家机构课包", isPublished: true);

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/public/orgs/pg1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(OrgKeys, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("pg1", body.GetProperty("code").GetString());
        Assert.Equal("机构pg1", body.GetProperty("name").GetString());
        Assert.Equal("/uploads/logos/pg1.png", body.GetProperty("logoPath").GetString());
        Assert.Equal("专注少儿编程启蒙", body.GetProperty("introText").GetString());
        Assert.Equal("021-63510000", body.GetProperty("contactPhone").GetString());
        Assert.Equal("上海市浦东新区张江路 1 号", body.GetProperty("address").GetString());

        // Only this institution's published products, ordered by id: the draft
        // pack and the other institution's published pack are both absent.
        var products = body.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(
            new[] { fixedPublished.Id, flexiblePublished.Id },
            products.Select(p => p.GetProperty("id").GetInt32()));
        foreach (var product in products)
        {
            Assert.Equal(ProductKeys, product.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        }

        var fixedItem = products[0];
        Assert.Equal("暑期固定班", fixedItem.GetProperty("name").GetString());
        // type is serialized as the enum NAME string.
        Assert.Equal("Fixed", fixedItem.GetProperty("type").GetString());
        Assert.Equal(1980m, fixedItem.GetProperty("price").GetDecimal());
        Assert.Equal(20, fixedItem.GetProperty("sessionCount").GetInt32());
        Assert.Equal("周一三五 09:00-12:00", fixedItem.GetProperty("description").GetString());
        Assert.Equal("/uploads/covers/pg1a.png", fixedItem.GetProperty("coverImagePath").GetString());

        Assert.Equal("Flexible", products[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, products[1].GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, products[1].GetProperty("coverImagePath").ValueKind);
    }

    [Fact]
    public async Task Disabled_institution_public_page_returns_404()
    {
        await SeedInstitutionAsync("pg2", status: InstitutionStatus.Disabled);

        var client = factory.CreateClient();
        var disabled = await client.GetAsync("/api/public/orgs/pg2");
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("application/problem+json", disabled.Content.Headers.ContentType?.MediaType);
        var disabledBody = await disabled.Content.ReadAsStringAsync();

        // Unknown codes answer the identical 404 — a disabled institution
        // leaks no existence either way.
        var unknown = await client.GetAsync("/api/public/orgs/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(disabledBody, await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Intent_with_invalid_phone_returns_400()
    {
        var institution = await SeedInstitutionAsync("pg3");
        var published = await SeedProductAsync(institution.Id, "在售课包", isPublished: true);
        var unpublished = await SeedProductAsync(institution.Id, "未发布课包", isPublished: false);
        var other = await SeedInstitutionAsync("pg3x");
        var foreign = await SeedProductAsync(other.Id, "别家机构课包", isPublished: true);

        var client = factory.CreateClient();

        // Phone is required and must match ^1\d{10}$.
        var badPhone = await client.PostAsJsonAsync("/api/public/orgs/pg3/intents",
            new { name = "王女士", phone = "12345", productId = published.Id });
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        var problem = await ProblemDetailAsync(badPhone);
        Assert.Contains("手机", problem.GetProperty("detail").GetString());

        // productId (when given) must be a published product of THIS
        // institution: a published product of another institution…
        var foreignProduct = await client.PostAsJsonAsync("/api/public/orgs/pg3/intents",
            new { phone = "13866660001", productId = foreign.Id });
        Assert.Equal(HttpStatusCode.BadRequest, foreignProduct.StatusCode);
        problem = await ProblemDetailAsync(foreignProduct);
        Assert.Contains("产品", problem.GetProperty("detail").GetString());

        // …and an unpublished product of this one are equally rejected.
        var unpublishedProduct = await client.PostAsJsonAsync("/api/public/orgs/pg3/intents",
            new { phone = "13866660002", productId = unpublished.Id });
        Assert.Equal(HttpStatusCode.BadRequest, unpublishedProduct.StatusCode);

        // message is capped at 200 characters.
        var longMessage = await client.PostAsJsonAsync("/api/public/orgs/pg3/intents",
            new { phone = "13866660003", message = new string('长', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, longMessage.StatusCode);
        problem = await ProblemDetailAsync(longMessage);
        Assert.Contains("留言", problem.GetProperty("detail").GetString());

        // A fully valid anonymous submission returns 201 {id} and persists
        // exactly one row, tenant-tagged with the institution from the code.
        var created = await client.PostAsJsonAsync("/api/public/orgs/pg3/intents",
            new { name = "李先生", phone = "13866660004", productId = published.Id, message = "想咨询暑期班" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id"], createdBody.EnumerateObject().Select(p => p.Name));
        var intentId = createdBody.GetProperty("id").GetInt32();
        Assert.True(intentId > 0);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PurchaseIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            // No tenant context existed on the request: InstitutionId must have
            // been filled from the institution row explicitly.
            Assert.Equal(institution.Id, row.InstitutionId);
            Assert.Equal("李先生", row.Name);
            Assert.Equal("13866660004", row.Phone);
            Assert.Equal(published.Id, row.ProductId);
            Assert.Equal("想咨询暑期班", row.Message);
            Assert.Equal(IntentStatus.New, row.Status);
            Assert.Null(row.FollowedByStaffId);
            // The rejected payloads never reached the database.
            Assert.Equal(1, await db.PurchaseIntents.IgnoreQueryFilters().CountAsync());
        }
    }
}

/// <summary>
/// The 429 test gets its own dedicated ApiFactory (own host, own rate-limiter
/// partitions, own database): the "intent" fixed window is 5 requests/minute
/// partitioned by remote IP, every test client shares the same loopback
/// address, and the partition lives in the host — so the six rapid POSTs here
/// must never share a window with other tests hitting the same endpoint on
/// their own hosts. This class is the only resident of its host.
/// </summary>
public class PublicOrgRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Sixth_intent_from_same_ip_within_a_minute_returns_429()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Institutions.Add(new Institution
            {
                Code = "rl1",
                Name = "机构rl1",
                Status = InstitutionStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/public/orgs/rl1/intents",
                new { name = "意向人", phone = $"1388888000{i}", message = "想试听" });
            statuses.Add(resp.StatusCode);
        }

        // Fixed window of 5 permits per minute per source IP: the first five
        // submissions succeed, the sixth is rejected with 429.
        Assert.Equal(
        [
            HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created,
            HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests,
        ], statuses);

        // The rejected request never reached the endpoint: exactly five rows exist.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(5, await db.PurchaseIntents.IgnoreQueryFilters().CountAsync());
        }
    }
}
