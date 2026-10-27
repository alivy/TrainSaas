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
/// Task 16 端到端集成测试，全部走真实 HTTP。本类不用 IClassFixture：两个测试
/// 各自构造自己的 ApiFactory（配置不同——E2E 用默认的 SeedDemoData=false 并把
/// 平台管理员直接落库；种子测试用 SeedDemoData=true 让启动种子真实跑一遍）。
///
/// Full_business_loop_platform_to_parent 串起整个后端的主业务闭环，顺带回归
/// Task 4（平台建机构）/5（多角色员工）/6（产品）/7（学员）/9（凭证上传）/
/// 10（订单确认出包+家长账号）/14（前台 walk-in 核销）/15（家长端课时包与流水），
/// 并断言钱和课时的最终数字（10 次包核销 1 次 → 9/10，流水恰 1 条 +1 WalkIn）。
///
/// SeedDemoData_is_idempotent_and_demo_admin_can_login 钉住启动种子
/// （SeedMinimalAsync + SeedDemoDataAsync）：演示账号全部可登录、种子内容
/// 与规格一致、重复执行不产生任何新行。
/// </summary>
public class EndToEndTests
{
    /// <summary>Minimal byte array starting with the PNG signature (validation is name/type based).</summary>
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x1A, 0x2B, 0x3C];

    private static async Task<string> TokenOfAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return token!;
    }

    private static HttpClient AuthedClient(ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpClient> LoginStaffClientAsync(ApiFactory factory, string phone) =>
        AuthedClient(factory, await TokenOfAsync(await factory.CreateClient().PostAsJsonAsync(
            "/api/org/auth/login", new { phone, password = DefaultPassword.Initial })));

    private static async Task<JsonElement> StaffLoginBodyAsync(ApiFactory factory, string phone)
    {
        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/api/org/auth/login", new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent ImageForm(byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent();
        form.Add(file, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Full_business_loop_platform_to_parent()
    {
        // 钉住：本测试用默认工厂（SeedDemoData=false），平台管理员直接落库。
        using var factory = new ApiFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            db.PlatformUsers.Add(new PlatformUser
            {
                Username = "admin",
                PasswordHash = hasher.Hash(DefaultPassword.Initial),
            });
            await db.SaveChangesAsync();
        }

        // 1. 平台超管登录（admin/123456）。
        var anon = factory.CreateClient();
        var platform = AuthedClient(factory, await TokenOfAsync(await anon.PostAsJsonAsync(
            "/api/platform/auth/login", new { username = "admin", password = DefaultPassword.Initial })));

        // 2. 平台创建机构 code="e2e"（Task 4 回归）→ 201 {id,code,name}。
        var create = await platform.PostAsJsonAsync("/api/platform/institutions", new
        {
            code = "e2e",
            name = "端到端演示机构",
            adminName = "端到端管理员",
            adminPhone = "13700000001",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("e2e", created.GetProperty("code").GetString());
        Assert.True(created.GetProperty("id").GetInt32() > 0);

        // 3. 新机构管理员用初始密码登录（Task 3/4 回归）。
        var adminLogin = await StaffLoginBodyAsync(factory, "13700000001");
        Assert.Equal("Admin", adminLogin.GetProperty("role").GetString());
        Assert.Equal("e2e", adminLogin.GetProperty("institutionCode").GetString());
        var admin = AuthedClient(factory, adminLogin.GetProperty("token").GetString()!);

        // 4. 建 Flexible 产品（10 课时，Task 6 回归）→ 201。
        var productResp = await admin.PostAsJsonAsync("/api/org/products",
            new { name = "体能体验课E2E", type = "Flexible", price = 299m, sessionCount = 10 });
        Assert.Equal(HttpStatusCode.Created, productResp.StatusCode);
        var product = await productResp.Content.ReadFromJsonAsync<JsonElement>();
        var productId = product.GetProperty("id").GetInt32();
        Assert.Equal("Flexible", product.GetProperty("type").GetString());
        Assert.Equal(10, product.GetProperty("sessionCount").GetInt32());

        // 5. 建学员 phone=13811112222（Task 7 回归）→ 201。
        var studentResp = await admin.PostAsJsonAsync("/api/org/students",
            new { name = "端到端学员", phone = "13811112222" });
        Assert.Equal(HttpStatusCode.Created, studentResp.StatusCode);
        var studentId = (await studentResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();

        // 6. 创建订单并确认（Task 10 回归）：确认产出课时包（10/10）与家长账号。
        var orderResp = await admin.PostAsJsonAsync("/api/org/orders",
            new { studentId, productId, amount = 299m });
        Assert.Equal(HttpStatusCode.Created, orderResp.StatusCode);
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("id").GetInt32();
        var orderNo = orderBody.GetProperty("orderNo").GetString();
        Assert.Equal("Pending", orderBody.GetProperty("status").GetString());
        Assert.Matches(@"^SO\d{14}$", orderNo);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/org/orders/{orderId}/confirm", null)).StatusCode);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/org/orders/{orderId}");
        Assert.Equal("Active", detail.GetProperty("status").GetString());
        var pack = detail.GetProperty("pack");
        var packId = pack.GetProperty("id").GetInt32();
        Assert.Equal(10, pack.GetProperty("totalSessions").GetInt32());
        Assert.Equal(10, pack.GetProperty("remainingSessions").GetInt32());
        Assert.Equal("Active", pack.GetProperty("status").GetString());

        // 7. 上传订单凭证（Task 9 回归）：小 png multipart → 201 {id,url}。
        var voucher = await admin.PostAsync(
            $"/api/org/files/vouchers/{orderId}", ImageForm(PngBytes, "receipt.png"));
        Assert.Equal(HttpStatusCode.Created, voucher.StatusCode);
        var voucherBody = await voucher.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/api/files/vouchers/{voucherBody.GetProperty("id").GetInt32()}",
            voucherBody.GetProperty("url").GetString());

        // 8. 通过员工管理接口建前台并登录（Task 5 回归，初始密码 123456）。
        var staffResp = await admin.PostAsJsonAsync("/api/org/staff",
            new { name = "端到端前台", phone = "13700000002", role = "FrontDesk" });
        Assert.Equal(HttpStatusCode.Created, staffResp.StatusCode);
        var frontDesk = await LoginStaffClientAsync(factory, "13700000002");

        // 9. 前台 walk-in 核销一次（Task 14 回归）→ 200 {id, remainingSessions=9}。
        var walkIn = await frontDesk.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId, sessionPackId = packId });
        Assert.Equal(HttpStatusCode.OK, walkIn.StatusCode);
        Assert.Equal(9, (await walkIn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("remainingSessions").GetInt32());

        // 10. 家长登录（Task 15 回归）：订单确认已用学员手机号物化家长账号。
        var parent = AuthedClient(factory, await TokenOfAsync(await anon.PostAsJsonAsync(
            "/api/parent/auth/login", new { phone = "13811112222", password = DefaultPassword.Initial })));

        // 11. 家长课时包：Remaining==9、Total==10（钱和课时的最终数字）。
        var packsResp = await parent.GetAsync($"/api/parent/packs?studentId={studentId}");
        Assert.Equal(HttpStatusCode.OK, packsResp.StatusCode);
        var packs = await packsResp.Content.ReadFromJsonAsync<JsonElement>();
        var only = Assert.Single(packs.EnumerateArray());
        Assert.Equal(packId, only.GetProperty("packId").GetInt32());
        Assert.Equal("体能体验课E2E", only.GetProperty("productName").GetString());
        Assert.Equal(10, only.GetProperty("totalSessions").GetInt32());
        Assert.Equal(9, only.GetProperty("remainingSessions").GetInt32());
        Assert.Equal("Active", only.GetProperty("status").GetString());
        Assert.Equal(orderNo, only.GetProperty("orderNo").GetString());

        // 12. 家长流水：恰 1 条，Direction=+1、Kind=WalkIn。
        var recordsResp = await parent.GetAsync($"/api/parent/records?studentId={studentId}");
        Assert.Equal(HttpStatusCode.OK, recordsResp.StatusCode);
        var records = await recordsResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, records.GetProperty("total").GetInt32());
        var row = Assert.Single(records.GetProperty("items").EnumerateArray());
        Assert.Equal(1, row.GetProperty("direction").GetInt32());
        Assert.Equal("WalkIn", row.GetProperty("kind").GetString());
        Assert.Equal("体能体验课E2E", row.GetProperty("productName").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reason").ValueKind);
    }

    /// <summary>种子后全库各表的行数快照（record 相等即"一行没多"）。</summary>
    private sealed record SeedSnapshot(
        int PlatformUsers, int Institutions, int Staff, int Students, int Products,
        int ClassTemplates, int ClassEnrollments, int Orders, int SessionPacks, int ParentAccounts);

    private static async Task<SeedSnapshot> SnapshotAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new SeedSnapshot(
            await db.PlatformUsers.CountAsync(),
            await db.Institutions.CountAsync(),
            await db.Staff.IgnoreQueryFilters().CountAsync(),
            await db.Students.IgnoreQueryFilters().CountAsync(),
            await db.Products.IgnoreQueryFilters().CountAsync(),
            await db.ClassTemplates.IgnoreQueryFilters().CountAsync(),
            await db.ClassEnrollments.IgnoreQueryFilters().CountAsync(),
            await db.Orders.IgnoreQueryFilters().CountAsync(),
            await db.SessionPacks.IgnoreQueryFilters().CountAsync(),
            await db.ParentAccounts.CountAsync());
    }

    [Fact]
    public async Task SeedDemoData_is_idempotent_and_demo_admin_can_login()
    {
        // 钉住：本测试用启动种子工厂（SeedDemoData=true）——首次触达 Services
        // 即真实跑一遍 Program 的 迁移 + SeedMinimalAsync + SeedDemoDataAsync。
        using var factory = new ApiFactory(new Dictionary<string, string>
        {
            ["SeedDemoData"] = "true",
        });
        _ = factory.Services;

        // 演示机构四个员工账号全部能用初始密码登录，角色/姓名/机构如实。
        var adminLogin = await StaffLoginBodyAsync(factory, "13900000001");
        Assert.Equal("Admin", adminLogin.GetProperty("role").GetString());
        Assert.Equal("demo", adminLogin.GetProperty("institutionCode").GetString());
        var salesLogin = await StaffLoginBodyAsync(factory, "13900000002");
        Assert.Equal("王小销", salesLogin.GetProperty("name").GetString());
        Assert.Equal("Sales", salesLogin.GetProperty("role").GetString());
        var teacherLogin = await StaffLoginBodyAsync(factory, "13900000003");
        Assert.Equal("李老师", teacherLogin.GetProperty("name").GetString());
        Assert.Equal("Teacher", teacherLogin.GetProperty("role").GetString());
        var frontLogin = await StaffLoginBodyAsync(factory, "13900000004");
        Assert.Equal("赵前台", frontLogin.GetProperty("name").GetString());
        Assert.Equal("FrontDesk", frontLogin.GetProperty("role").GetString());

        // 已确认订单物化的家长账号也能登录。
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync(
            "/api/parent/auth/login",
            new { phone = "13800001111", password = DefaultPassword.Initial })).StatusCode);

        var before = await SnapshotAsync(factory);

        // 第二次完整种子（第二个作用域，模拟应用重启）：一行都不许多。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            await SeedData.SeedMinimalAsync(db, hasher);
            await SeedData.SeedDemoDataAsync(db, hasher);
        }

        var after = await SnapshotAsync(factory);
        Assert.Equal(before, after);

        // 行数恰为规格值（幂等 + 内容都钉住）。
        Assert.Equal(
            new SeedSnapshot(
                PlatformUsers: 1, Institutions: 1, Staff: 4, Students: 3, Products: 2,
                ClassTemplates: 2, ClassEnrollments: 2, Orders: 2, SessionPacks: 1, ParentAccounts: 1),
            after);

        // 种子内容与规格逐项核对（无租户上下文 → IgnoreQueryFilters）。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var demo = await db.Institutions.SingleAsync(i => i.Code == "demo");
            var sales = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Phone == "13900000002");
            var teacher = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Phone == "13900000003");
            Assert.All(await db.Staff.IgnoreQueryFilters()
                .Where(s => s.InstitutionId == demo.Id).ToListAsync(), s => Assert.True(s.IsActive));

            // 产品：少儿舞蹈基础班(Fixed,1980,24)、体能体验课(Flexible,299,10)，均已上架。
            var dance = await db.Products.IgnoreQueryFilters()
                .SingleAsync(p => p.InstitutionId == demo.Id && p.Name == "少儿舞蹈基础班");
            Assert.Equal(ProductType.Fixed, dance.Type);
            Assert.Equal(1980m, dance.Price);
            Assert.Equal(24, dance.SessionCount);
            Assert.True(dance.IsPublished);
            var trial = await db.Products.IgnoreQueryFilters()
                .SingleAsync(p => p.InstitutionId == demo.Id && p.Name == "体能体验课");
            Assert.Equal(ProductType.Flexible, trial.Type);
            Assert.Equal(299m, trial.Price);
            Assert.Equal(10, trial.SessionCount);
            Assert.True(trial.IsPublished);

            // 学员：张小明/张小红兄妹同手机号、李华，全部归属王小销且在籍。
            var ming = await db.Students.IgnoreQueryFilters()
                .SingleAsync(s => s.InstitutionId == demo.Id && s.Name == "张小明");
            var hong = await db.Students.IgnoreQueryFilters()
                .SingleAsync(s => s.InstitutionId == demo.Id && s.Name == "张小红");
            var lihua = await db.Students.IgnoreQueryFilters()
                .SingleAsync(s => s.InstitutionId == demo.Id && s.Name == "李华");
            Assert.Equal("13800001111", ming.Phone);
            Assert.Equal(ming.Phone, hong.Phone);
            Assert.Equal("13800002222", lihua.Phone);
            Assert.Equal(sales.Id, ming.AssignedSalesId);
            Assert.Equal(sales.Id, hong.AssignedSalesId);
            Assert.Equal(sales.Id, lihua.AssignedSalesId);
            Assert.True(ming.IsActive && hong.IsActive && lihua.IsActive);

            // 模板：基础班 周二/周四 18:00-19:30 李老师，容量 20，启用。
            var templates = await db.ClassTemplates.IgnoreQueryFilters()
                .Where(t => t.InstitutionId == demo.Id).ToListAsync();
            Assert.Equal(2, templates.Count);
            Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday],
                templates.Select(t => t.DayOfWeek).Order().ToList());
            Assert.All(templates, t =>
            {
                Assert.Equal(dance.Id, t.ProductId);
                Assert.Equal(teacher.Id, t.TeacherId);
                Assert.Equal(new TimeOnly(18, 0), t.StartTime);
                Assert.Equal(new TimeOnly(19, 30), t.EndTime);
                Assert.Equal(20, t.Capacity);
                Assert.True(t.IsActive);
            });

            // 张小明在两个模板上都是 Enrolled。
            var enrollments = await db.ClassEnrollments.IgnoreQueryFilters()
                .Where(e => e.InstitutionId == demo.Id).ToListAsync();
            Assert.Equal(2, enrollments.Count);
            Assert.All(enrollments, e =>
            {
                Assert.Equal(ming.Id, e.StudentId);
                Assert.Equal(EnrollmentStatus.Enrolled, e.Status);
                Assert.Contains(e.TemplateId, templates.Select(t => t.Id));
            });

            // 张小明已确认订单（SO20260101000001，基础班，1980，王小销）+ 24/24 课时包。
            var mingOrder = await db.Orders.IgnoreQueryFilters()
                .SingleAsync(o => o.OrderNo == "SO20260101000001");
            Assert.Equal(demo.Id, mingOrder.InstitutionId);
            Assert.Equal(ming.Id, mingOrder.StudentId);
            Assert.Equal(dance.Id, mingOrder.ProductId);
            Assert.Equal(1980m, mingOrder.Amount);
            Assert.Equal(sales.Id, mingOrder.SalesId);
            Assert.Equal(OrderStatus.Active, mingOrder.Status);
            Assert.NotNull(mingOrder.ConfirmedById);
            Assert.NotNull(mingOrder.ConfirmedAt);
            var mingPack = await db.SessionPacks.IgnoreQueryFilters()
                .SingleAsync(p => p.OrderId == mingOrder.Id);
            Assert.Equal(demo.Id, mingPack.InstitutionId);
            Assert.Equal(ming.Id, mingPack.StudentId);
            Assert.Equal(dance.Id, mingPack.ProductId);
            Assert.Equal(24, mingPack.TotalSessions);
            Assert.Equal(24, mingPack.RemainingSessions);
            Assert.Equal(PackStatus.Active, mingPack.Status);

            // 家长账号 13800001111 存在且初始密码可验证。
            var parent = await db.ParentAccounts.SingleAsync(p => p.Phone == "13800001111");
            Assert.True(hasher.Verify(parent.PasswordHash, DefaultPassword.Initial));

            // 李华 Pending 订单（体能体验课，299，王小销），无课时包。
            var lihuaOrder = await db.Orders.IgnoreQueryFilters()
                .SingleAsync(o => o.InstitutionId == demo.Id && o.StudentId == lihua.Id);
            Assert.Equal(trial.Id, lihuaOrder.ProductId);
            Assert.Equal(299m, lihuaOrder.Amount);
            Assert.Equal(sales.Id, lihuaOrder.SalesId);
            Assert.Equal(OrderStatus.Pending, lihuaOrder.Status);
            Assert.Equal(0, await db.SessionPacks.IgnoreQueryFilters()
                .CountAsync(p => p.OrderId == lihuaOrder.Id));
        }
    }
}
