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
/// via the tenant filter, anonymous 401 via the StaffOnly policy). The three
/// serving security branches (traversal FilePath, missing physical file,
/// non-whitelisted extension) are pinned by T9① tests, and the anonymous
/// logo/cover image route (GET /api/files/images/{**relativePath}, added by
/// the final-review fix wave I3) is covered for both serving and rejection.
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
    /// disk when the expected outcome short-circuits before file resolution;
    /// pass a custom <paramref name="filePath"/> to pin the serving guards
    /// (traversal / missing file / extension whitelist).
    /// </summary>
    private async Task<Voucher> SeedVoucherAsync(
        int orderId, int institutionId, int uploadedById, string? filePath = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var voucher = new Voucher
        {
            InstitutionId = institutionId,
            OrderId = orderId,
            FilePath = filePath ?? $"voucher/{institutionId}/202610/seeded.png",
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

    /// <summary>Writes a file with the given relative path under the factory's upload root.</summary>
    private async Task WriteUnderUploadRootAsync(string relativePath, byte[] content)
    {
        var full = Path.Combine(factory.UploadRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
    }

    /// <summary>
    /// I3（最终审查补齐）：logo/cover 图片的匿名下发路由
    /// GET /api/files/images/{**relativePath}。员工上传后匿名 GET 返回 200、
    /// 扩展名推断的 content type 与逐字节一致的响应体；voucher/ 前缀绝不匿名
    /// 下发（即便文件物理存在且经授权路由可取）。
    /// </summary>
    [Fact]
    public async Task Public_image_route_serves_uploaded_logo_and_cover_anonymously()
    {
        var institution = await SeedInstitutionAsync("fx7");
        await SeedStaffAsync(institution.Id, "13966600008");
        var client = await LoginStaffClientAsync("13966600008");
        var anonymous = factory.CreateClient();

        // 员工上传 logo → 匿名 GET 返回的路径 → 200 + image/png + 字节一致。
        var logo = await client.PostAsync(
            "/api/org/files", ImageForm(PngBytes, "logo.png", "image/png", purpose: "logo"));
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        var logoPath = (await logo.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("path").GetString()!;
        var getLogo = await anonymous.GetAsync($"/api/files/images/{logoPath}");
        Assert.Equal(HttpStatusCode.OK, getLogo.StatusCode);
        Assert.Equal("image/png", getLogo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await getLogo.Content.ReadAsByteArrayAsync());

        // cover 同样可匿名下发；content type 按扩展名推断（.jpg → image/jpeg，
        // 服务端不做魔数嗅探——文件体仍是那几字节）。
        var cover = await client.PostAsync(
            "/api/org/files", ImageForm(PngBytes, "cover.jpg", "image/jpeg", purpose: "cover"));
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        var coverPath = (await cover.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("path").GetString()!;
        var getCover = await anonymous.GetAsync($"/api/files/images/{coverPath}");
        Assert.Equal(HttpStatusCode.OK, getCover.StatusCode);
        Assert.Equal("image/jpeg", getCover.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await getCover.Content.ReadAsByteArrayAsync());

        // 凭据的 staff-only 路由不受影响：匿名仍然 401（策略层）。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/files/vouchers/1")).StatusCode);
    }

    /// <summary>
    /// I3 的拒绝分支：voucher/ 前缀（文件真实存在也拒）、目录穿越（编码的
    /// ../ 逃不出上传根）、前缀大小写敏感（LOGO/ 下真实存在的文件也拒）、
    /// 未知扩展名（cover/ 下真实存在的 .txt 也拒）、不存在的路径——一律 404。
    /// </summary>
    [Fact]
    public async Task Public_image_route_rejects_voucher_traversal_case_and_bad_extension()
    {
        var institution = await SeedInstitutionAsync("fx8");
        var staff = await SeedStaffAsync(institution.Id, "13966600009");
        var client = await LoginStaffClientAsync("13966600009");
        var anonymous = factory.CreateClient();

        // 一张真实存在的凭据文件：授权路由可取（200），匿名图片路由绝不给。
        var order = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var attach = await client.PostAsync(
            $"/api/org/files/vouchers/{order.Id}", ImageForm(PngBytes, "receipt.png", "image/png"));
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var voucherId = (await attach.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();
        string voucherPath;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            voucherPath = (await db.Vouchers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(v => v.Id == voucherId)).FilePath;
        }

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/files/vouchers/{voucherId}")).StatusCode);
        var voucherViaImages = await anonymous.GetAsync($"/api/files/images/{voucherPath}");
        Assert.Equal(HttpStatusCode.NotFound, voucherViaImages.StatusCode);

        // 目录穿越：编码的 ../（%2f 不被客户端折叠）必须逃不出上传根 → 404。
        var traversal = await anonymous.GetAsync(
            "/api/files/images/logo/..%2f..%2f..%2f..%2fetc%2fpasswd");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        Assert.DoesNotContain("root:", await traversal.Content.ReadAsStringAsync());
        // 字面 ../ 变体（被客户端 URI 规范化折叠）同样到不了任何文件 → 404。
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(
            "/api/files/images/logo/../../../../etc/passwd")).StatusCode);

        // 前缀白名单大小写敏感：LOGO/ 下真实存在的文件也拒。
        await WriteUnderUploadRootAsync("LOGO/real.png", PngBytes);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync("/api/files/images/LOGO/real.png")).StatusCode);

        // 扩展名白名单：cover/ 下真实存在的 .txt 也拒（且绝不回显内容）。
        await WriteUnderUploadRootAsync($"cover/{institution.Id}/202610/notes.txt",
            "TOP-SECRET"u8.ToArray());
        var txt = await anonymous.GetAsync($"/api/files/images/cover/{institution.Id}/202610/notes.txt");
        Assert.Equal(HttpStatusCode.NotFound, txt.StatusCode);
        Assert.DoesNotContain("TOP-SECRET", await txt.Content.ReadAsStringAsync());

        // 不存在的路径 / 空路径 → 404。
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(
            $"/api/files/images/logo/{institution.Id}/209912/missing.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync("/api/files/images/")).StatusCode);
    }

    /// <summary>
    /// T9①（最终审查必修延后项）：GET /api/files/vouchers/{id} 的三个安全分支
    /// 此前零测试覆盖——本测试把它们钉死，移除 ResolveForServing 的任何一道
    /// 防线（根内断言 / 物理存在 / 扩展名白名单）都会让全套变红：
    /// ① FilePath="../../etc/passwd"（根外穿越，文件物理存在）→ 404；
    /// ② 行存在但物理文件缺失 → 404；
    /// ③ FilePath 为 .txt（文件物理存在）→ 404，且绝不回显内容。
    /// 对照组：同目录下的合法 .png 正常下发（404 来自防线而非路由坏了）。
    /// </summary>
    [Fact]
    public async Task Voucher_serving_traversal_missing_file_and_bad_extension_return_404()
    {
        var institution = await SeedInstitutionAsync("fx9");
        var staff = await SeedStaffAsync(institution.Id, "13966600010");
        var client = await LoginStaffClientAsync("13966600010");

        // ① 穿越：FilePath 指向上传根之外（/etc/passwd 物理存在）→ 404。
        var traversalOrder = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var traversalVoucher = await SeedVoucherAsync(
            traversalOrder.Id, institution.Id, staff.Id, filePath: "../../etc/passwd");
        var traversal = await client.GetAsync($"/api/files/vouchers/{traversalVoucher.Id}");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        await ProblemDetailAsync(traversal);
        Assert.DoesNotContain("root:", await traversal.Content.ReadAsStringAsync());

        // ② 行存在、物理文件缺失 → 404。
        var missingOrder = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var missing = await SeedVoucherAsync(missingOrder.Id, institution.Id, staff.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/files/vouchers/{missing.Id}")).StatusCode);

        // ③ 扩展名白名单：.txt 文件物理存在也 404，内容绝不外泄。
        var txtRelative = $"voucher/{institution.Id}/202610/notes.txt";
        await WriteUnderUploadRootAsync(txtRelative, "TOP-SECRET"u8.ToArray());
        var txtOrder = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var txtVoucher = await SeedVoucherAsync(
            txtOrder.Id, institution.Id, staff.Id, filePath: txtRelative);
        var txt = await client.GetAsync($"/api/files/vouchers/{txtVoucher.Id}");
        Assert.Equal(HttpStatusCode.NotFound, txt.StatusCode);
        Assert.DoesNotContain("TOP-SECRET", await txt.Content.ReadAsStringAsync());

        // 对照组：合法 .png（物理存在）正常下发，content type 按扩展名推断。
        var pngRelative = $"voucher/{institution.Id}/202610/real.png";
        await WriteUnderUploadRootAsync(pngRelative, PngBytes);
        var pngOrder = await SeedOrderAsync(institution.Id, staff.Id, OrderStatus.Pending);
        var pngVoucher = await SeedVoucherAsync(
            pngOrder.Id, institution.Id, staff.Id, filePath: pngRelative);
        var ok = await client.GetAsync($"/api/files/vouchers/{pngVoucher.Id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await ok.Content.ReadAsByteArrayAsync());
    }
}
