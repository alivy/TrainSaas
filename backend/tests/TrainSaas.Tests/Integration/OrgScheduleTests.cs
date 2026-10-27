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
/// 排课集成测试（Task 11）：班级模板 CRUD（AdminOnly）+ 课次惰性生成与查询。
/// 覆盖：模板创建守卫（仅 Fixed 产品可建班、教师须为本机构在职 Teacher/Admin、
/// endTime&gt;startTime、HH:mm 时间格式、dayOfWeek 0-6、capacity≥1）；PUT 只影响
/// 之后新生成的课次、已生成课次字段不变；deactivate/activate 控制后续生成；
/// GET /api/org/sessions?from&amp;to 先生成后查询（窗口 ≤60 天、非法日期 400、
/// Teacher 只见自己授课的课次）；课次详情含 roster（仅 Enrolled）与既有
/// Attendance，Teacher 访问他人课次 404（不泄露存在性）。
///
/// ApiFactory 运行于 SeedDemoData=false：每个测试经 factory.Services.CreateScope()
/// 自行播种，并以真实登录令牌走真实 HTTP。机构编码/手机号在共享库内全局唯一。
/// </summary>
public class OrgScheduleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>模板响应（201 与列表项）的精确字段集。</summary>
    private static readonly string[] TemplateKeys =
        ["capacity", "dayOfWeek", "endTime", "id", "isActive", "productId", "room", "startTime", "teacherId"];

    /// <summary>课次列表项的精确字段集。</summary>
    private static readonly string[] SessionKeys =
        ["endAt", "enrolledCount", "id", "productId", "productName", "room", "startAt", "status",
            "teacherId", "teacherName", "templateId"];

    /// <summary>课次详情顶层字段集。</summary>
    private static readonly string[] SessionDetailKeys =
        ["attendance", "endAt", "id", "roster", "startAt", "status", "teacherId", "teacherName",
            "template", "templateId"];

    /// <summary>课次详情内嵌模板信息的字段集。</summary>
    private static readonly string[] SessionTemplateKeys =
        ["capacity", "dayOfWeek", "endTime", "isActive", "productId", "productName", "room", "startTime"];

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

    private async Task<Product> SeedProductAsync(
        int institutionId, string name, ProductType type = ProductType.Fixed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = type,
            Price = 1980m,
            SessionCount = 20,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private async Task<Student> SeedStudentAsync(int institutionId, string name, string phone)
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
        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!);
        return authed;
    }

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>POST /api/org/templates（成功路径）→ 201 响应体。</summary>
    private static async Task<JsonElement> CreateTemplateAsync(
        HttpClient client,
        int productId,
        int teacherId,
        int dayOfWeek = 1,
        string startTime = "18:00",
        string endTime = "19:30",
        string? room = null,
        int? capacity = null)
    {
        var resp = await client.PostAsJsonAsync("/api/org/templates",
            new { productId, dayOfWeek, startTime, endTime, teacherId, room, capacity });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>GET /api/org/sessions?from&amp;to → 200 的课次数组。</summary>
    private static async Task<List<JsonElement>> ListSessionsAsync(HttpClient client, string from, string to)
    {
        var resp = await client.GetAsync($"/api/org/sessions?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        return body.EnumerateArray().ToList();
    }

    [Fact]
    public async Task Create_template_for_flexible_product_returns_400()
    {
        var institution = await SeedInstitutionAsync("sc1");
        await SeedStaffAsync(institution.Id, "13966600001");
        var teacher = await SeedStaffAsync(institution.Id, "13966600101", StaffRole.Teacher);
        var flexible = await SeedProductAsync(institution.Id, "灵活约课包", ProductType.Flexible);
        var client = await LoginStaffClientAsync("13966600001");

        var resp = await client.PostAsJsonAsync("/api/org/templates",
            new
            {
                productId = flexible.Id,
                dayOfWeek = 2,
                startTime = "18:00",
                endTime = "19:30",
                teacherId = teacher.Id,
            });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await ProblemDetailAsync(resp);
        Assert.Contains("固定排课", problem.GetProperty("detail").GetString());

        // 未知产品同样过不了"存在且 Fixed"这道闸。
        var unknown = await client.PostAsJsonAsync("/api/org/templates",
            new
            {
                productId = 999999,
                dayOfWeek = 2,
                startTime = "18:00",
                endTime = "19:30",
                teacherId = teacher.Id,
            });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ClassTemplates.IgnoreQueryFilters()
                .CountAsync(t => t.InstitutionId == institution.Id));
        }
    }

    [Fact]
    public async Task Create_template_with_sales_as_teacher_returns_400()
    {
        var institution = await SeedInstitutionAsync("sc2");
        await SeedStaffAsync(institution.Id, "13966600002");
        var sales = await SeedStaffAsync(institution.Id, "13966600202", StaffRole.Sales);
        var frontDesk = await SeedStaffAsync(institution.Id, "13966600203", StaffRole.FrontDesk);
        var inactiveTeacher = await SeedStaffAsync(institution.Id, "13966600204", StaffRole.Teacher, isActive: false);
        var away = await SeedInstitutionAsync("sc2x");
        var awayTeacher = await SeedStaffAsync(away.Id, "13966600205", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13966600002");

        // 角色不符：销售/前台都不能当授课教师……
        foreach (var badTeacherId in new[] { sales.Id, frontDesk.Id })
        {
            var resp = await client.PostAsJsonAsync("/api/org/templates",
                new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = badTeacherId });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await ProblemDetailAsync(resp);
            Assert.Contains("教师", problem.GetProperty("detail").GetString());
        }

        // ……停用的教师不行……
        var inactive = await client.PostAsJsonAsync("/api/org/templates",
            new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = inactiveTeacher.Id });
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);

        // ……外机构教师被租户过滤器隐藏，同样 400（不泄露存在性）。
        var crossTenant = await client.PostAsJsonAsync("/api/org/templates",
            new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = awayTeacher.Id });
        Assert.Equal(HttpStatusCode.BadRequest, crossTenant.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ClassTemplates.IgnoreQueryFilters()
                .CountAsync(t => t.InstitutionId == institution.Id));
        }
    }

    [Fact]
    public async Task Teacher_list_sessions_sees_only_own()
    {
        var institution = await SeedInstitutionAsync("sc3");
        var admin = await SeedStaffAsync(institution.Id, "13966600003");
        var teacher1 = await SeedStaffAsync(institution.Id, "13966600303", StaffRole.Teacher);
        var teacher2 = await SeedStaffAsync(institution.Id, "13966600304", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "少儿英语固定班");
        var adminClient = await LoginStaffClientAsync("13966600003");

        var templateA = await CreateTemplateAsync(adminClient, product.Id, teacher1.Id,
            dayOfWeek: 1, room: "A101", capacity: 12);
        var templateB = await CreateTemplateAsync(adminClient, product.Id, teacher2.Id,
            dayOfWeek: 2, room: "B202");

        // 在班 2 人 + 退班 1 人：列表的 enrolledCount 只数 Enrolled。
        var student1 = await SeedStudentAsync(institution.Id, "报班学员一", "13855500001");
        var student2 = await SeedStudentAsync(institution.Id, "报班学员二", "13855500002");
        var student3 = await SeedStudentAsync(institution.Id, "退班学员", "13855500003");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClassEnrollments.AddRange(
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateA.GetProperty("id").GetInt32(),
                    StudentId = student1.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Enrolled,
                },
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateA.GetProperty("id").GetInt32(),
                    StudentId = student2.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Enrolled,
                },
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateA.GetProperty("id").GetInt32(),
                    StudentId = student3.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Withdrawn,
                });
            await db.SaveChangesAsync();
        }

        // Teacher1 的查询先触发生成（按机构整体生成，再按角色过滤查询结果）。
        var teacher1Client = await LoginStaffClientAsync("13966600303");
        var items = await ListSessionsAsync(teacher1Client, "2026-11-02", "2026-11-30");

        var expectedStarts = new[]
        {
            new DateTime(2026, 11, 2, 18, 0, 0),
            new DateTime(2026, 11, 9, 18, 0, 0),
            new DateTime(2026, 11, 16, 18, 0, 0),
            new DateTime(2026, 11, 23, 18, 0, 0),
            new DateTime(2026, 11, 30, 18, 0, 0),
        };
        Assert.Equal(expectedStarts, items.Select(i => i.GetProperty("startAt").GetDateTime()).ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal(SessionKeys, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
            Assert.Equal(templateA.GetProperty("id").GetInt32(), item.GetProperty("templateId").GetInt32());
            Assert.Equal(teacher1.Id, item.GetProperty("teacherId").GetInt32());
            Assert.Equal(teacher1.Name, item.GetProperty("teacherName").GetString());
            Assert.Equal(product.Id, item.GetProperty("productId").GetInt32());
            Assert.Equal("少儿英语固定班", item.GetProperty("productName").GetString());
            Assert.Equal("A101", item.GetProperty("room").GetString());
            Assert.Equal("Scheduled", item.GetProperty("status").GetString());
            Assert.Equal(2, item.GetProperty("enrolledCount").GetInt32());
            Assert.Equal(
                item.GetProperty("startAt").GetDateTime().AddMinutes(90),
                item.GetProperty("endAt").GetDateTime());
        });

        // Teacher2 对称：只看到自己周二模板的 4 节课，没有报名 → 0。
        var teacher2Client = await LoginStaffClientAsync("13966600304");
        var teacher2Items = await ListSessionsAsync(teacher2Client, "2026-11-02", "2026-11-30");
        Assert.Equal(4, teacher2Items.Count);
        Assert.All(teacher2Items, item =>
        {
            Assert.Equal(templateB.GetProperty("id").GetInt32(), item.GetProperty("templateId").GetInt32());
            Assert.Equal(teacher2.Id, item.GetProperty("teacherId").GetInt32());
            Assert.Equal("B202", item.GetProperty("room").GetString());
            Assert.Equal(0, item.GetProperty("enrolledCount").GetInt32());
        });

        // Admin 看到全机构 9 节；先后不同调用者触发的生成不产生重复。
        var all = await ListSessionsAsync(adminClient, "2026-11-02", "2026-11-30");
        Assert.Equal(9, all.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(9, await db.ClassSessions.IgnoreQueryFilters()
                .CountAsync(cs => cs.InstitutionId == institution.Id));
        }
    }

    [Fact]
    public async Task Template_create_validation_and_admin_only()
    {
        var institution = await SeedInstitutionAsync("sc4");
        await SeedStaffAsync(institution.Id, "13966600004");
        var teacher = await SeedStaffAsync(institution.Id, "13966600404", StaffRole.Teacher);
        var frontDesk = await SeedStaffAsync(institution.Id, "13966600405", StaffRole.FrontDesk);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13966600004");
        var url = "/api/org/templates";

        // 成功路径：201 精确回显文档字段；新建模板显式激活。
        var created = await CreateTemplateAsync(client, product.Id, teacher.Id,
            dayOfWeek: 2, room: "A101", capacity: 12);
        Assert.Equal(TemplateKeys, created.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.True(created.GetProperty("id").GetInt32() > 0);
        Assert.Equal(product.Id, created.GetProperty("productId").GetInt32());
        Assert.Equal(2, created.GetProperty("dayOfWeek").GetInt32());
        Assert.Equal("18:00:00", created.GetProperty("startTime").GetString());
        Assert.Equal("19:30:00", created.GetProperty("endTime").GetString());
        Assert.Equal(teacher.Id, created.GetProperty("teacherId").GetInt32());
        Assert.Equal("A101", created.GetProperty("room").GetString());
        Assert.Equal(12, created.GetProperty("capacity").GetInt32());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        // dayOfWeek 越界（0=周日 … 6=周六）。
        foreach (var badDay in new[] { 7, -1 })
        {
            var resp = await client.PostAsJsonAsync(url,
                new { productId = product.Id, dayOfWeek = badDay, startTime = "18:00", endTime = "19:30", teacherId = teacher.Id });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await ProblemDetailAsync(resp);
            Assert.Contains("dayOfWeek", problem.GetProperty("detail").GetString());
        }

        // 时间必须是严格 HH:mm。
        foreach (var badTime in new[] { "9:00", "25:00", "1800", "18:00:00" })
        {
            var resp = await client.PostAsJsonAsync(url,
                new { productId = product.Id, dayOfWeek = 2, startTime = badTime, endTime = "19:30", teacherId = teacher.Id });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await ProblemDetailAsync(resp);
            Assert.Contains("HH:mm", problem.GetProperty("detail").GetString());
        }
        var emptyTime = await client.PostAsJsonAsync(url,
            new { productId = product.Id, dayOfWeek = 2, startTime = "", endTime = "19:30", teacherId = teacher.Id });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTime.StatusCode);

        // endTime 必须严格晚于 startTime。
        foreach (var (start, end) in new[] { ("18:00", "18:00"), ("18:00", "17:00") })
        {
            var resp = await client.PostAsJsonAsync(url,
                new { productId = product.Id, dayOfWeek = 2, startTime = start, endTime = end, teacherId = teacher.Id });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await ProblemDetailAsync(resp);
            Assert.Contains("结束", problem.GetProperty("detail").GetString());
        }

        // capacity 若填必须 ≥1。
        var zeroCapacity = await client.PostAsJsonAsync(url,
            new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = teacher.Id, capacity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zeroCapacity.StatusCode);
        var problem0 = await ProblemDetailAsync(zeroCapacity);
        Assert.Contains("容量", problem0.GetProperty("detail").GetString());

        // 前台不是可授课角色。
        var frontDeskTeacher = await client.PostAsJsonAsync(url,
            new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = frontDesk.Id });
        Assert.Equal(HttpStatusCode.BadRequest, frontDeskTeacher.StatusCode);

        // 模板管理是 AdminOnly：教师/前台一律 403。
        var teacherClient = await LoginStaffClientAsync("13966600404");
        var denied = await teacherClient.PostAsJsonAsync(url,
            new { productId = product.Id, dayOfWeek = 2, startTime = "18:00", endTime = "19:30", teacherId = teacher.Id });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // 所有被拒的请求都没有落库：库里只有成功那一行。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ClassTemplates.IgnoreQueryFilters()
                .CountAsync(t => t.InstitutionId == institution.Id));
        }
    }

    [Fact]
    public async Task Sessions_query_validates_dates_and_window()
    {
        var institution = await SeedInstitutionAsync("sc5");
        await SeedStaffAsync(institution.Id, "13966600005");
        var teacher = await SeedStaffAsync(institution.Id, "13966600505", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13966600005");
        await CreateTemplateAsync(client, product.Id, teacher.Id, dayOfWeek: 1);

        // from/to 缺一不可……
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/org/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/org/sessions?from=2026-11-02")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/org/sessions?to=2026-11-30")).StatusCode);

        // ……非法日期 400……
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/org/sessions?from=notadate&to=2026-11-30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/org/sessions?from=2026-11-02&to=2026-13-99")).StatusCode);

        // ……to 早于 from 400……
        var reversed = await client.GetAsync("/api/org/sessions?from=2026-11-10&to=2026-11-05");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);

        // ……跨度超过 60 天 400。
        var tooWide = await client.GetAsync("/api/org/sessions?from=2026-11-01&to=2027-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
        var problem = await ProblemDetailAsync(tooWide);
        Assert.Contains("60", problem.GetProperty("detail").GetString());

        // 校验失败发生在生成之前：此刻库里仍无任何课次。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ClassSessions.IgnoreQueryFilters()
                .CountAsync(cs => cs.InstitutionId == institution.Id));
        }

        // 恰好 60 天合法：窗口 [11-01, 12-31]，生成仍被钳制到 28 天
        // （周一模板 → 11-02/09/16/23 共 4 节）。
        var sixtyDays = await ListSessionsAsync(client, "2026-11-01", "2026-12-31");
        Assert.Equal(4, sixtyDays.Count);

        // from==to 的单日窗口也合法（补生成 11-30 周一这一节）。
        var singleDay = await ListSessionsAsync(client, "2026-11-30", "2026-11-30");
        var item = Assert.Single(singleDay);
        Assert.Equal(new DateTime(2026, 11, 30, 18, 0, 0), item.GetProperty("startAt").GetDateTime());
    }

    [Fact]
    public async Task Template_update_only_affects_future_sessions()
    {
        var institution = await SeedInstitutionAsync("sc6");
        await SeedStaffAsync(institution.Id, "13966600006");
        var teacher1 = await SeedStaffAsync(institution.Id, "13966600606", StaffRole.Teacher);
        var teacher2 = await SeedStaffAsync(institution.Id, "13966600607", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13966600006");

        var template = await CreateTemplateAsync(client, product.Id, teacher1.Id,
            dayOfWeek: 1, room: "A101", capacity: 12);
        var templateId = template.GetProperty("id").GetInt32();

        // 先在 11 月生成 5 节周一课。
        var november = await ListSessionsAsync(client, "2026-11-02", "2026-11-30");
        Assert.Equal(5, november.Count);
        Assert.All(november, item => Assert.Equal(teacher1.Id, item.GetProperty("teacherId").GetInt32()));

        // PUT 的校验与创建同规格：endTime<=startTime → 400；换成灵活型产品 → 400；
        // 换成销售当教师 → 400；未知/跨机构 id → 404。
        var badPut = await client.PutAsJsonAsync($"/api/org/templates/{templateId}",
            new { endTime = "17:00" });
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);
        var flexibleProduct = await SeedProductAsync(institution.Id, "灵活约课包", ProductType.Flexible);
        var flexiblePut = await client.PutAsJsonAsync($"/api/org/templates/{templateId}",
            new { productId = flexibleProduct.Id });
        Assert.Equal(HttpStatusCode.BadRequest, flexiblePut.StatusCode);
        var sales = await SeedStaffAsync(institution.Id, "13966600608", StaffRole.Sales);
        var salesPut = await client.PutAsJsonAsync($"/api/org/templates/{templateId}",
            new { teacherId = sales.Id });
        Assert.Equal(HttpStatusCode.BadRequest, salesPut.StatusCode);
        var zeroCapacityPut = await client.PutAsJsonAsync($"/api/org/templates/{templateId}",
            new { capacity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zeroCapacityPut.StatusCode);
        var unknownPut = await client.PutAsJsonAsync("/api/org/templates/999999", new { room = "X" });
        Assert.Equal(HttpStatusCode.NotFound, unknownPut.StatusCode);

        // 改模板：周二 19:00-20:30、换教师、换教室、改容量。
        var put = await client.PutAsJsonAsync($"/api/org/templates/{templateId}",
            new
            {
                dayOfWeek = 2,
                startTime = "19:00",
                endTime = "20:30",
                teacherId = teacher2.Id,
                room = "B202",
                capacity = 15,
            });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // 12 月窗口（[12-07, 12-31] 全在 28 天钳制内）按新模板生成 4 节周二课：
        // 新教师、新时段、新教室、按 startAt 升序。
        var december = await ListSessionsAsync(client, "2026-12-07", "2026-12-31");
        Assert.Equal(
            new[] { 8, 15, 22, 29 },
            december.Select(i => i.GetProperty("startAt").GetDateTime().Day).ToArray());
        Assert.All(december, item =>
        {
            Assert.Equal(teacher2.Id, item.GetProperty("teacherId").GetInt32());
            Assert.Equal(teacher2.Name, item.GetProperty("teacherName").GetString());
            Assert.Equal(19, item.GetProperty("startAt").GetDateTime().Hour);
            Assert.Equal(20, item.GetProperty("endAt").GetDateTime().Hour);
            Assert.Equal(30, item.GetProperty("endAt").GetDateTime().Minute);
        });

        // 再查 11 月窗口："已生成实例不变"——旧的 5 节周一课一字未动；同时
        // 新模板在这个窗口补生成 4 节周二课（修改只影响之后新生成的实例）。
        var novemberAgain = await ListSessionsAsync(client, "2026-11-02", "2026-11-30");
        Assert.Equal(9, novemberAgain.Count);
        Assert.Equal(
            novemberAgain.Select(i => i.GetProperty("startAt").GetDateTime()).OrderBy(d => d).ToArray(),
            novemberAgain.Select(i => i.GetProperty("startAt").GetDateTime()).ToArray());

        var mondays = novemberAgain
            .Where(i => i.GetProperty("startAt").GetDateTime().DayOfWeek == DayOfWeek.Monday).ToList();
        Assert.Equal(5, mondays.Count);
        Assert.All(mondays, item =>
        {
            Assert.Equal(teacher1.Id, item.GetProperty("teacherId").GetInt32());
            Assert.Equal(teacher1.Name, item.GetProperty("teacherName").GetString());
            Assert.Equal(18, item.GetProperty("startAt").GetDateTime().Hour);
            Assert.Equal(19, item.GetProperty("endAt").GetDateTime().Hour);
            Assert.Equal(30, item.GetProperty("endAt").GetDateTime().Minute);
        });

        var newTuesdays = novemberAgain
            .Where(i => i.GetProperty("startAt").GetDateTime().DayOfWeek == DayOfWeek.Tuesday).ToList();
        Assert.Equal(
            new[] { 3, 10, 17, 24 },
            newTuesdays.Select(i => i.GetProperty("startAt").GetDateTime().Day).ToArray());
        Assert.All(newTuesdays, item => Assert.Equal(teacher2.Id, item.GetProperty("teacherId").GetInt32()));

        // room 不在 ClassSession 上，读时从模板解析：修改后历史课次也显示新教室
        //（授课教师与起止时间才是"已生成实例不变"的字段）。
        Assert.All(novemberAgain.Concat(december), item => Assert.Equal("B202", item.GetProperty("room").GetString()));

        // 模板列表按 productId 过滤，字段与 201 一致。
        var listResp = await client.GetAsync($"/api/org/templates?productId={product.Id}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var paged = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            new[] { "items", "total" },
            paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(1, paged.GetProperty("total").GetInt32());
        var listItem = paged.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(TemplateKeys, listItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(templateId, listItem.GetProperty("id").GetInt32());
        Assert.Equal(2, listItem.GetProperty("dayOfWeek").GetInt32());
        Assert.Equal("19:00:00", listItem.GetProperty("startTime").GetString());
        Assert.Equal("20:30:00", listItem.GetProperty("endTime").GetString());
        Assert.Equal(teacher2.Id, listItem.GetProperty("teacherId").GetInt32());
        Assert.Equal(15, listItem.GetProperty("capacity").GetInt32());

        var otherProduct = await client.GetAsync("/api/org/templates?productId=999999");
        var otherPaged = await otherProduct.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, otherPaged.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Deactivate_then_activate_controls_generation()
    {
        var institution = await SeedInstitutionAsync("sc7");
        await SeedStaffAsync(institution.Id, "13966600007");
        var teacher = await SeedStaffAsync(institution.Id, "13966600707", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13966600007");

        var template = await CreateTemplateAsync(client, product.Id, teacher.Id, dayOfWeek: 1);
        var templateId = template.GetProperty("id").GetInt32();

        var november = await ListSessionsAsync(client, "2026-11-02", "2026-11-30");
        Assert.Equal(5, november.Count);

        // 停用 → 204；模板列表立即反映 isActive=false。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/org/templates/{templateId}/deactivate", null)).StatusCode);
        var paged = await (await client.GetAsync($"/api/org/templates?productId={product.Id}")).Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.False(paged.GetProperty("items").EnumerateArray().Single().GetProperty("isActive").GetBoolean());

        // 停用模板不再生成：12 月窗口为空。
        var december = await ListSessionsAsync(client, "2026-12-07", "2026-12-31");
        Assert.Empty(december);

        // 已生成的 11 月课次原样保留。
        var stillThere = await ListSessionsAsync(client, "2026-11-02", "2026-11-30");
        Assert.Equal(5, stillThere.Count);

        // 重新激活 → 后续窗口恢复生成（12 月的 4 个周一）。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/org/templates/{templateId}/activate", null)).StatusCode);
        var resumed = await ListSessionsAsync(client, "2026-12-07", "2026-12-31");
        Assert.Equal(
            new[] { 7, 14, 21, 28 },
            resumed.Select(i => i.GetProperty("startAt").GetDateTime().Day).ToArray());

        // 未知模板 id 的停用/激活 → 404。
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/org/templates/999999/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/org/templates/999999/activate", null)).StatusCode);
    }

    [Fact]
    public async Task Session_detail_returns_roster_attendance_and_scopes_teacher()
    {
        var institution = await SeedInstitutionAsync("sc8");
        var admin = await SeedStaffAsync(institution.Id, "13966600008");
        var teacher1 = await SeedStaffAsync(institution.Id, "13966600808", StaffRole.Teacher);
        var teacher2 = await SeedStaffAsync(institution.Id, "13966600909", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var adminClient = await LoginStaffClientAsync("13966600008");

        var template = await CreateTemplateAsync(adminClient, product.Id, teacher1.Id,
            dayOfWeek: 1, room: "A101", capacity: 12);
        var templateId = template.GetProperty("id").GetInt32();
        var items = await ListSessionsAsync(adminClient, "2026-11-02", "2026-11-30");
        var sessionId = items[0].GetProperty("id").GetInt32();

        var student1 = await SeedStudentAsync(institution.Id, "详情学员一", "13855500010");
        var student2 = await SeedStudentAsync(institution.Id, "详情学员二", "13855500011");
        var student3 = await SeedStudentAsync(institution.Id, "详情退班学员", "13855500012");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClassEnrollments.AddRange(
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateId,
                    StudentId = student1.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Enrolled,
                },
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateId,
                    StudentId = student2.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Enrolled,
                },
                new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = templateId,
                    StudentId = student3.Id,
                    EnrolledById = admin.Id,
                    Status = EnrollmentStatus.Withdrawn,
                });
            // 既有点名记录（Task 13 才提供写入口，这里直插一行验证读取）。
            db.Attendances.Add(new Attendance
            {
                InstitutionId = institution.Id,
                ClassSessionId = sessionId,
                StudentId = student1.Id,
                Mark = AttendanceMark.Present,
                OperatorId = admin.Id,
            });
            await db.SaveChangesAsync();
        }

        // Teacher2 看别人的课次：404（不泄露存在性），与未知 id 同一表现。
        var teacher2Client = await LoginStaffClientAsync("13966600909");
        Assert.Equal(HttpStatusCode.NotFound,
            (await teacher2Client.GetAsync($"/api/org/sessions/{sessionId}")).StatusCode);

        // Teacher1 看自己授课的课次详情。
        var teacher1Client = await LoginStaffClientAsync("13966600808");
        var detailResp = await teacher1Client.GetAsync($"/api/org/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SessionDetailKeys, detail.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        Assert.Equal(sessionId, detail.GetProperty("id").GetInt32());
        Assert.Equal(templateId, detail.GetProperty("templateId").GetInt32());
        Assert.Equal(teacher1.Id, detail.GetProperty("teacherId").GetInt32());
        Assert.Equal(teacher1.Name, detail.GetProperty("teacherName").GetString());
        Assert.Equal(new DateTime(2026, 11, 2, 18, 0, 0), detail.GetProperty("startAt").GetDateTime());
        Assert.Equal(new DateTime(2026, 11, 2, 19, 30, 0), detail.GetProperty("endAt").GetDateTime());
        Assert.Equal("Scheduled", detail.GetProperty("status").GetString());

        var templateInfo = detail.GetProperty("template");
        Assert.Equal(SessionTemplateKeys, templateInfo.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(product.Id, templateInfo.GetProperty("productId").GetInt32());
        Assert.Equal("固定班课包", templateInfo.GetProperty("productName").GetString());
        Assert.Equal(1, templateInfo.GetProperty("dayOfWeek").GetInt32());
        Assert.Equal("18:00:00", templateInfo.GetProperty("startTime").GetString());
        Assert.Equal("19:30:00", templateInfo.GetProperty("endTime").GetString());
        Assert.Equal("A101", templateInfo.GetProperty("room").GetString());
        Assert.Equal(12, templateInfo.GetProperty("capacity").GetInt32());
        Assert.True(templateInfo.GetProperty("isActive").GetBoolean());

        // roster 只含 Enrolled 学员，含手机号；退班学员不在其中。
        var roster = detail.GetProperty("roster").EnumerateArray().ToList();
        Assert.Equal(
            new[] { student1.Id, student2.Id },
            roster.Select(r => r.GetProperty("studentId").GetInt32()).OrderBy(id => id).ToArray());
        Assert.All(roster, r =>
            Assert.Equal(
                new[] { "name", "phone", "studentId" },
                r.EnumerateObject().Select(p => p.Name).OrderBy(n => n)));
        Assert.Equal("详情学员一", roster.Single(r => r.GetProperty("studentId").GetInt32() == student1.Id)
            .GetProperty("name").GetString());
        Assert.Equal("13855500010", roster.Single(r => r.GetProperty("studentId").GetInt32() == student1.Id)
            .GetProperty("phone").GetString());
        Assert.DoesNotContain(roster, r => r.GetProperty("studentId").GetInt32() == student3.Id);

        // attendance：既有的一行点名，字段精确。
        var attendanceItem = detail.GetProperty("attendance").EnumerateArray().Single();
        Assert.Equal(
            new[] { "createdAt", "mark", "operatorId", "studentId" },
            attendanceItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(student1.Id, attendanceItem.GetProperty("studentId").GetInt32());
        Assert.Equal("Present", attendanceItem.GetProperty("mark").GetString());
        Assert.Equal(admin.Id, attendanceItem.GetProperty("operatorId").GetInt32());
        Assert.True(attendanceItem.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        // Admin 同样可见；未知课次 id → 404。
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.GetAsync($"/api/org/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminClient.GetAsync("/api/org/sessions/999999")).StatusCode);
    }
}
