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
/// Integration tests for the order lifecycle API (Task 10), driven over real
/// HTTP with real staff tokens: a Sales creates an order (salesId defaults to
/// the creator, order number matches SO+yyyyMMdd+6 digits), attaches a voucher
/// through the Task 9 endpoint while the order is still Pending, and an admin
/// confirms it — producing the session pack and the parent account with the
/// initial password. Double-confirm is pinned as 409 over HTTP with exactly one
/// pack (Review Focus 2). Cross-institution ids are 404 on every order route
/// (Review Focus 1), the list endpoint filters by status/studentId and
/// safe-parses status to 400, and cancel-by-creator works over HTTP.
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff/students/products via factory.Services.CreateScope().
/// Codes/phones are unique per test because the class fixture shares one
/// database. Seeding scopes carry no tenant context, so InstitutionId is set
/// explicitly on every seeded row.
/// </summary>
public class OrgOrderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>GET /api/org/orders 列表项的完整 JSON 键集合。</summary>
    private static readonly string[] ListItemKeys =
        ["amount", "createdAt", "id", "orderNo", "productId", "productName",
            "salesId", "salesName", "status", "studentId", "studentName"];

    /// <summary>GET /api/org/orders/{id} 详情的完整 JSON 键集合。</summary>
    private static readonly string[] DetailKeys =
        ["amount", "confirmedAt", "confirmedById", "createdAt", "createdById", "id", "orderNo",
            "pack", "product", "productId", "refundAmount", "remark", "salesId", "status", "student",
            "studentId", "voidedAt", "voidedById", "voidReason", "vouchers"];

    /// <summary>订单详情内嵌摘要（student/product/pack）的键集合。</summary>
    private static readonly string[] StudentKeys = ["id", "name", "phone"];
    private static readonly string[] ProductKeys = ["id", "name", "type"];
    private static readonly string[] PackKeys = ["id", "remainingSessions", "status", "totalSessions"];
    private static readonly string[] VoucherKeys = ["id", "url"];

    /// <summary>Minimal byte array starting with the PNG signature (validation is name/type based).</summary>
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x1A, 0x2B, 0x3C];

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

    private async Task<Student> SeedStudentAsync(
        int institutionId, string phone, string name = "学员")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = new Student
        {
            InstitutionId = institutionId,
            Name = name,
            Phone = phone,
            IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    private async Task<Product> SeedProductAsync(
        int institutionId, string name, int sessionCount = 10, decimal price = 1000m)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = ProductType.Fixed,
            Price = price,
            SessionCount = sessionCount,
            // Unpublished on purpose: selling to an existing student ignores publish state.
            IsPublished = false,
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

    /// <summary>Creates an order over HTTP and returns its id (asserting 201 + shape).</summary>
    private async Task<int> CreateOrderAsync(
        HttpClient client, int studentId, int productId, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/org/orders", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id", "orderNo", "status"], created.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        Assert.Matches(@"^SO\d{14}$", created.GetProperty("orderNo").GetString());
        return created.GetProperty("id").GetInt32();
    }

    private static MultipartFormDataContent ImageForm(byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent();
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Sales_creates_order_defaults_self_as_sales_and_can_upload_voucher_then_admin_confirms()
    {
        var institution = await SeedInstitutionAsync("ox1");
        var sales = await SeedStaffAsync(institution.Id, "13977700001", StaffRole.Sales);
        var admin = await SeedStaffAsync(institution.Id, "13977700002", StaffRole.Admin);
        var student = await SeedStudentAsync(institution.Id, "13977700003", "张小学员");
        var product = await SeedProductAsync(institution.Id, "启蒙英语包", sessionCount: 24, price: 4800m);
        var salesClient = await LoginStaffClientAsync(sales.Phone);

        // Sales creates the order without salesId — it must default to the creator.
        // Amount deliberately differs from the product price (议价，不做等值校验)。
        var orderId = await CreateOrderAsync(
            salesClient, student.Id, product.Id, new { studentId = student.Id, productId = product.Id, amount = 4500m });

        var detail = await salesClient.GetFromJsonAsync<JsonElement>($"/api/org/orders/{orderId}");
        Assert.Equal(DetailKeys, detail.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(sales.Id, detail.GetProperty("salesId").GetInt32());
        Assert.Equal(4500m, detail.GetProperty("amount").GetDecimal());
        Assert.Null(detail.GetProperty("pack").GetString());

        // While Pending, the Task 9 voucher endpoint still accepts the order.
        var attach = await salesClient.PostAsync(
            $"/api/org/files/vouchers/{orderId}", ImageForm(PngBytes, "receipt.png"));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var voucherId = (await attach.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();

        // Sales cannot confirm or void: both routes are AdminOnly.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PostAsync($"/api/org/orders/{orderId}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PostAsJsonAsync(
                $"/api/org/orders/{orderId}/void", new { reason = "越权作废" })).StatusCode);

        // The admin confirms: 204, order Active, pack + parent account materialize.
        var adminClient = await LoginStaffClientAsync(admin.Phone);
        var confirm = await adminClient.PostAsync($"/api/org/orders/{orderId}/confirm", null);
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var confirmed = await adminClient.GetFromJsonAsync<JsonElement>($"/api/org/orders/{orderId}");
        Assert.Equal("Active", confirmed.GetProperty("status").GetString());
        Assert.Equal(admin.Id, confirmed.GetProperty("confirmedById").GetInt32());
        Assert.NotNull(confirmed.GetProperty("confirmedAt").GetString());

        var studentSummary = confirmed.GetProperty("student");
        Assert.Equal(StudentKeys, studentSummary.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(student.Id, studentSummary.GetProperty("id").GetInt32());
        Assert.Equal("张小学员", studentSummary.GetProperty("name").GetString());
        Assert.Equal(student.Phone, studentSummary.GetProperty("phone").GetString());

        var productSummary = confirmed.GetProperty("product");
        Assert.Equal(ProductKeys, productSummary.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(product.Id, productSummary.GetProperty("id").GetInt32());
        Assert.Equal("启蒙英语包", productSummary.GetProperty("name").GetString());
        Assert.Equal("Fixed", productSummary.GetProperty("type").GetString());

        var pack = confirmed.GetProperty("pack");
        Assert.Equal(PackKeys, pack.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(24, pack.GetProperty("totalSessions").GetInt32());
        Assert.Equal(24, pack.GetProperty("remainingSessions").GetInt32());
        Assert.Equal("Active", pack.GetProperty("status").GetString());

        var vouchers = confirmed.GetProperty("vouchers");
        var voucher = Assert.Single(vouchers.EnumerateArray());
        Assert.Equal(VoucherKeys, voucher.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(voucherId, voucher.GetProperty("id").GetInt32());
        Assert.Equal($"/api/files/vouchers/{voucherId}", voucher.GetProperty("url").GetString());

        // DB state: exactly one pack row and one parent account with the initial password.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The assertion scope has no tenant context, so bypass the filter.
            var packRow = await db.SessionPacks.IgnoreQueryFilters()
                .SingleAsync(sp => sp.OrderId == orderId);
            Assert.Equal(student.Id, packRow.StudentId);
            Assert.Equal(product.Id, packRow.ProductId);
            Assert.Equal(24, packRow.TotalSessions);
            Assert.Equal(24, packRow.RemainingSessions);
            Assert.Equal(PackStatus.Active, packRow.Status);

            var parent = await db.ParentAccounts.AsNoTracking()
                .SingleAsync(p => p.Phone == student.Phone);
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            Assert.True(hasher.Verify(parent.PasswordHash, DefaultPassword.Initial));
        }
    }

    [Fact]
    public async Task Double_confirm_returns_409_and_single_pack()
    {
        var institution = await SeedInstitutionAsync("ox2");
        var admin = await SeedStaffAsync(institution.Id, "13977700010");
        var student = await SeedStudentAsync(institution.Id, "13977700011");
        var product = await SeedProductAsync(institution.Id, "双击课程", sessionCount: 8);
        var client = await LoginStaffClientAsync(admin.Phone);

        var orderId = await CreateOrderAsync(
            client, student.Id, product.Id, new { studentId = student.Id, productId = product.Id, amount = 800m });

        // Review Focus 2 over HTTP: the second confirm (double click) is 409.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/org/orders/{orderId}/confirm", null)).StatusCode);
        var second = await client.PostAsync($"/api/org/orders/{orderId}/confirm", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ProblemDetailAsync(second);
        Assert.Equal("订单已处理", problem.GetProperty("detail").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.SessionPacks.IgnoreQueryFilters()
                .CountAsync(sp => sp.OrderId == orderId));
        }
    }

    [Fact]
    public async Task Other_institution_order_detail_returns_404()
    {
        var own = await SeedInstitutionAsync("ox3");
        var other = await SeedInstitutionAsync("ox3x");
        await SeedStaffAsync(own.Id, "13977700020");
        var otherSales = await SeedStaffAsync(other.Id, "13977700021", StaffRole.Sales);
        var otherStudent = await SeedStudentAsync(other.Id, "13977700022", "别家学员");
        var otherProduct = await SeedProductAsync(other.Id, "别家课程", sessionCount: 6);
        var ownClient = await LoginStaffClientAsync("13977700020");
        var otherClient = await LoginStaffClientAsync(otherSales.Phone);

        // The other institution's sales creates their own order over HTTP.
        var orderId = await CreateOrderAsync(
            otherClient, otherStudent.Id, otherProduct.Id,
            new { studentId = otherStudent.Id, productId = otherProduct.Id, amount = 600m });

        // Review Focus 1: this institution's admin sees nothing of it — the
        // tenant filter makes detail/confirm/cancel/void all 404, and the list
        // stays empty. None of the 404s changed anything on the other side.
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.GetAsync($"/api/org/orders/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PostAsync($"/api/org/orders/{orderId}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PostAsync($"/api/org/orders/{orderId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PostAsJsonAsync($"/api/org/orders/{orderId}/void", new { reason = "越权作废" })).StatusCode);

        var list = await ownClient.GetFromJsonAsync<JsonElement>("/api/org/orders");
        Assert.Equal(0, list.GetProperty("total").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatus.Pending, order.Status);
            Assert.Equal(0, await db.SessionPacks.IgnoreQueryFilters()
                .CountAsync(sp => sp.OrderId == orderId));
        }
    }

    [Fact]
    public async Task List_filters_by_status_and_studentId_and_invalid_status_returns_400()
    {
        var institution = await SeedInstitutionAsync("ox4");
        var sales = await SeedStaffAsync(institution.Id, "13977700030", StaffRole.Sales);
        var admin = await SeedStaffAsync(institution.Id, "13977700031", StaffRole.Admin);
        var student = await SeedStudentAsync(institution.Id, "13977700032", "学员甲");
        var product = await SeedProductAsync(institution.Id, "列表课程", sessionCount: 10);
        var salesClient = await LoginStaffClientAsync(sales.Phone);
        var adminClient = await LoginStaffClientAsync(admin.Phone);

        var orderA = await CreateOrderAsync(
            salesClient, student.Id, product.Id, new { studentId = student.Id, productId = product.Id, amount = 1000m });
        var orderB = await CreateOrderAsync(
            salesClient, student.Id, product.Id, new { studentId = student.Id, productId = product.Id, amount = 2000m });

        // Admin confirms A; the creator (sales) cancels B — both over HTTP.
        Assert.Equal(HttpStatusCode.NoContent,
            (await adminClient.PostAsync($"/api/org/orders/{orderA}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await salesClient.PostAsync($"/api/org/orders/{orderB}/cancel", null)).StatusCode);

        // Unknown enum values safe-parse to 400 (never 500).
        var bad = await adminClient.GetAsync("/api/org/orders?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await ProblemDetailAsync(bad);
        Assert.Contains("状态", problem.GetProperty("detail").GetString());

        var all = await adminClient.GetFromJsonAsync<JsonElement>("/api/org/orders");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var active = await adminClient.GetFromJsonAsync<JsonElement>("/api/org/orders?status=Active");
        Assert.Equal(1, active.GetProperty("total").GetInt32());
        var item = active.GetProperty("items")[0];
        Assert.Equal(ListItemKeys, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(orderA, item.GetProperty("id").GetInt32());
        Assert.Equal("Active", item.GetProperty("status").GetString());
        Assert.Equal(1000m, item.GetProperty("amount").GetDecimal());
        Assert.Equal("学员甲", item.GetProperty("studentName").GetString());
        Assert.Equal("列表课程", item.GetProperty("productName").GetString());
        Assert.Equal(sales.Id, item.GetProperty("salesId").GetInt32());
        Assert.Equal($"员工{sales.Phone}", item.GetProperty("salesName").GetString());

        var cancelled = await adminClient.GetFromJsonAsync<JsonElement>("/api/org/orders?status=Cancelled");
        Assert.Equal(1, cancelled.GetProperty("total").GetInt32());
        Assert.Equal(orderB, cancelled.GetProperty("items")[0].GetProperty("id").GetInt32());

        var byStudent = await adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/org/orders?studentId={student.Id}");
        Assert.Equal(2, byStudent.GetProperty("total").GetInt32());
        var combined = await adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/org/orders?status=Active&studentId={student.Id}");
        Assert.Equal(1, combined.GetProperty("total").GetInt32());
        Assert.Equal(orderA, combined.GetProperty("items")[0].GetProperty("id").GetInt32());
    }
}
