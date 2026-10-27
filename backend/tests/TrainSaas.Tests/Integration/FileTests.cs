using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// Integration tests for image upload storage and authorized voucher serving
/// (Task 9): POST /api/org/files (purpose ∈ logo/cover) writes the exact bytes
/// under the configured Storage:UploadRoot at
/// {purpose}/{institutionId}/{yyyyMM}/{guid}{ext} and returns the relative
/// path; text files, extension/content-type mismatches and oversized (&gt;5MB)
/// uploads are 400. POST /api/org/files/vouchers/{orderId} attaches a Voucher
/// row to a tenant-visible order — the order rows are seeded directly via
/// DbContext (ruling R3, the order service arrives in Task 10) — and
/// GET /api/files/vouchers/{id} serves the bytes with the extension-inferred
/// content type to the owning institution's staff only (cross-institution 404
/// via the tenant filter, anonymous 401 via the StaffOnly policy).
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff/orders via factory.Services.CreateScope() and drives
/// every behavior over real HTTP with real tokens. Codes/phones are unique
/// per test because the class fixture shares one database and one upload root.
/// </summary>
public class FileTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// Minimal byte array starting with the PNG file signature. Validation is
    /// extension/content-type based (no magic-byte sniffing), so this suffices.
    /// </summary>
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x1A, 0x2A, 0x3B];

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
    /// Seeds an order straight into the DB (bypassing HTTP). The order service
    /// only arrives in Task 10 (ruling R3), so terminal statuses are seeded
    /// directly; the seeding scope has no tenant context, so InstitutionId is
    /// set explicitly. Order has no FK to Student/Product, so placeholder ids
    /// are fine — only institution, status and existence matter here.
    /// </summary>
    private async Task<Order> SeedOrderAsync(int institutionId, int createdById, OrderStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = new Order
        {
            InstitutionId = institutionId,
            OrderNo = $"SO-FT-{Guid.NewGuid():N}",
            StudentId = 1,
            ProductId = 1,
            Amount = 1000m,
            CreatedById = createdById,
            Status = status,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>
    /// Seeds a voucher row straight into the DB. The FilePath need not exist on
    /// disk when the expected outcome short-circuits before file resolution.
    /// </summary>
    private async Task<Voucher> SeedVoucherAsync(int orderId, int institutionId, int uploadedById)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var voucher = new Voucher
        {
            InstitutionId = institutionId,
            OrderId = orderId,
            FilePath = $"voucher/{institutionId}/202610/seeded.png",
            UploadedById = uploadedById,
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        return voucher;
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

    /// <summary>
    /// Builds a multipart form-data body: a <c>file</c> part with the given
    /// name/content type and an optional <c>purpose</c> field, mimicking a
    /// browser image upload.
    /// </summary>
    private static MultipartFormDataContent ImageForm(
        byte[] content, string fileName, string contentType, string? purpose = null)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent();
        form.Add(file, "file", fileName);
        if (purpose is not null)
        {
            form.Add(new StringContent(purpose), "purpose");
        }
        return form;
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Upload_png_returns_path_and_file_exists_on_disk()
    {
        var institution = await SeedInstitutionAsync("fx1");
        await SeedStaffAsync(institution.Id, "13966600001");
        var client = await LoginStaffClientAsync("13966600001");

        var resp = await client.PostAsync(
            "/api/org/files", ImageForm(PngBytes, "logo.png", "image/png", purpose: "logo"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["path"], body.EnumerateObject().Select(p => p.Name));

        // The path is the server-generated relative storage path: forward
        // slashes, purpose/institution/month/guid.png.
        var path = body.GetProperty("path").GetString();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Matches($"^logo/{institution.Id}/\\d{{6}}/[0-9a-f]{{32}}\\.png$", path);

        // The exact uploaded bytes exist on disk under the configured upload root.
        var fullPath = Path.Combine(factory.UploadRoot, path!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"expected uploaded file at {fullPath}");
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task Upload_text_file_returns_400()
    {
        var institution = await SeedInstitutionAsync("fx2");
        await SeedStaffAsync(institution.Id, "13966600002");
        var client = await LoginStaffClientAsync("13966600002");

        // text/plain is not an allowed image content type.
        var text = await client.PostAsync(
            "/api/org/files",
            ImageForm(Encoding.ASCII.GetBytes("hello world"), "note.txt", "text/plain", purpose: "logo"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        var problem = await ProblemDetailAsync(text);
        Assert.Contains("类型", problem.GetProperty("detail").GetString());

        // The declared content type is fine, but the file extension does not
        // match it (.jpg is not a png).
        var mismatch = await client.PostAsync(
            "/api/org/files", ImageForm(PngBytes, "photo.jpg", "image/png", purpose: "logo"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        problem = await ProblemDetailAsync(mismatch);
        Assert.Contains("扩展名", problem.GetProperty("detail").GetString());

        // purpose must be logo or cover.
        var badPurpose = await client.PostAsync(
            "/api/org/files", ImageForm(PngBytes, "logo.png", "image/png", purpose: "banner"));
        Assert.Equal(HttpStatusCode.BadRequest, badPurpose.StatusCode);
        problem = await ProblemDetailAsync(badPurpose);
        Assert.Contains("用途", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Upload_over_5MB_returns_400()
    {
        var institution = await SeedInstitutionAsync("fx3");
        await SeedStaffAsync(institution.Id, "13966600003");
        var client = await LoginStaffClientAsync("13966600003");

        // 5MB + 1 byte, generated in memory (never reads a real file).
        var oversized = new byte[5 * 1024 * 1024 + 1];
        var resp = await client.PostAsync(
            "/api/org/files", ImageForm(oversized, "big.png", "image/png", purpose: "cover"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("大小", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Voucher_get_by_other_institution_staff_returns_404()
    {
        var own = await SeedInstitutionAsync("fx4");
        await SeedStaffAsync(own.Id, "13966600004");
        var other = await SeedInstitutionAsync("fx4x");
        var otherStaff = await SeedStaffAsync(other.Id, "13966600005");
        var otherOrder = await SeedOrderAsync(other.Id, otherStaff.Id, OrderStatus.Pending);
        var ownClient = await LoginStaffClientAsync("13966600004");
        var otherClient = await LoginStaffClientAsync("13966600005");

        // The other institution's staff attaches a voucher to their own order...
        var attach = await otherClient.PostAsync(
            $"/api/org/files/vouchers/{otherOrder.Id}", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var created = await attach.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id", "url"], created.EnumerateObject().Select(p => p.Name));
        var voucherId = created.GetProperty("id").GetInt32();
        Assert.True(voucherId > 0);
        Assert.Equal($"/api/files/vouchers/{voucherId}", created.GetProperty("url").GetString());

        // ...and can fetch the exact bytes back with the extension-inferred type.
        var download = await otherClient.GetAsync($"/api/files/vouchers/{voucherId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await download.Content.ReadAsByteArrayAsync());

        // This institution's staff sees neither the voucher...
        var cross = await ownClient.GetAsync($"/api/files/vouchers/{voucherId}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        await ProblemDetailAsync(cross);

        // ...nor the order it belongs to: attaching to the cross-tenant order is
        // equally 404 (tenant filter), and the rejected attach created nothing.
        var attachOther = await ownClient.PostAsync(
            $"/api/org/files/vouchers/{otherOrder.Id}", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, attachOther.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Vouchers.IgnoreQueryFilters()
                .CountAsync(v => v.OrderId == otherOrder.Id));
        }
    }

    [Fact]
    public async Task Voucher_get_anonymous_returns_401()
    {
        var institution = await SeedInstitutionAsync("fx5");
        var staff = await SeedStaffAsync(institution.Id, "13966600006");
        var order = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var voucher = await SeedVoucherAsync(order.Id, institution.Id, staff.Id);

        // No token at all: the StaffOnly policy answers 401 before any lookup.
        var anonymous = factory.CreateClient();
        var resp = await anonymous.GetAsync($"/api/files/vouchers/{voucher.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Attach_voucher_to_voided_order_returns_400()
    {
        var institution = await SeedInstitutionAsync("fx6");
        var staff = await SeedStaffAsync(institution.Id, "13966600007");
        // The order service and void API only arrive in Task 10 (ruling R3):
        // seed the closed orders directly with their terminal statuses.
        var voided = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Voided);
        var cancelled = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Cancelled);
        var client = await LoginStaffClientAsync("13966600007");

        var resp = await client.PostAsync(
            $"/api/org/files/vouchers/{voided.Id}", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("作废", problem.GetProperty("detail").GetString());

        // Cancelled orders are equally closed for vouchers.
        var cancelledResp = await client.PostAsync(
            $"/api/org/files/vouchers/{cancelled.Id}", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.BadRequest, cancelledResp.StatusCode);

        // Unknown order ids are 404 (the tenant filter makes cross-institution
        // ids behave the same), and no voucher row was ever created.
        var missing = await client.PostAsync(
            "/api/org/files/vouchers/999999", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Vouchers.IgnoreQueryFilters()
                .AnyAsync(v => v.OrderId == voided.Id || v.OrderId == cancelled.Id));
        }
    }
}
