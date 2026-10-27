using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Integration;

/// <summary>
/// 家长端与机构页设置的集成测试（Task 15），全部走真实 HTTP + 真实登录令牌
/// （ApiFactory，SeedDemoData=false，每个测试自造数据、机构编码/手机号互不冲突）。
///
/// 覆盖两条设计内的租户过滤豁免：
/// <list type="bullet">
/// <item>家长端（ParentService）：跨机构看自己的孩子（同手机号在两个机构各有
/// 一个学员 → 两行都可见）；他人孩子一律 404（Review Focus 1 的家长版：与不存在
/// 的 id 同态响应，绝不泄露存在性）；课时包反映前台核销后的余量（含 Voided 包的
/// 历史）；流水含冲正行（direction=-1 + reason）；课表只含在班（Enrolled）且启用
/// 模板的未来 Scheduled 实例；days 安全解析并钳制 1..60。</item>
/// <item>机构页设置（OrgProfileController）：Institution 非 ITenanted，读写走
/// IgnoreQueryFilters + 显式 Id==令牌机构 —— 隔壁机构管理员改不到本机构，且改动
/// 立刻反映在匿名公开页上。</item>
/// </list>
/// 家长端路由全部 [ParentOnly]：匿名 401、员工令牌 403。
/// </summary>
public class ParentApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] ChildKeys =
        ["institutionCode", "institutionId", "institutionName", "name", "studentId"];
    private static readonly string[] PackKeys =
        ["orderNo", "packId", "productName", "purchasedAt", "remainingSessions", "status", "totalSessions"];
    private static readonly string[] RecordKeys =
        ["classSessionStartAt", "createdAt", "direction", "id", "kind", "productName", "reason"];
    private static readonly string[] ScheduleKeys =
        ["endAt", "institutionName", "productName", "room", "startAt", "teacherName"];
    private static readonly string[] ProfileKeys =
        ["address", "code", "contactPhone", "introText", "logoPath", "name"];

    /// <summary>在一个作用域内造数并返回结果（造数作用域无租户上下文，InstitutionId 必须显式赋值）。</summary>
    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await seed(db);
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
    }

    private IPasswordHasherService Hasher => factory.Services.GetRequiredService<IPasswordHasherService>();

    private async Task<Institution> SeedInstitutionAsync(string code, string? name = null) =>
        await WithDbAsync(async db =>
        {
            var institution = new Institution
            {
                Code = code,
                Name = name ?? $"机构{code}",
                Status = InstitutionStatus.Active,
            };
            db.Institutions.Add(institution);
            await db.SaveChangesAsync();
            return institution;
        });

    private async Task<Staff> SeedStaffAsync(
        int institutionId, string phone, StaffRole role, string? name = null) =>
        await WithDbAsync(async db =>
        {
            var staff = new Staff
            {
                InstitutionId = institutionId,
                Name = name ?? $"员工{phone}",
                Phone = phone,
                PasswordHash = Hasher.Hash(DefaultPassword.Initial),
                Role = role,
                IsActive = true,
            };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            return staff;
        });

    /// <summary>家长账号（非租户实体，按手机号全局唯一），初始密码可直接登录。</summary>
    private async Task SeedParentAsync(string phone) =>
        await WithDbAsync(async db =>
        {
            db.ParentAccounts.Add(new ParentAccount
            {
                Phone = phone,
                PasswordHash = Hasher.Hash(DefaultPassword.Initial),
            });
            await db.SaveChangesAsync();
        });

    private async Task<Student> SeedStudentAsync(
        int institutionId, string phone, string name, bool isActive = true) =>
        await WithDbAsync(async db =>
        {
            var student = new Student
            {
                InstitutionId = institutionId,
                Name = name,
                Phone = phone,
                IsActive = isActive,
            };
            db.Students.Add(student);
            await db.SaveChangesAsync();
            return student;
        });

    private async Task<Product> SeedProductAsync(
        int institutionId, string name, ProductType type = ProductType.Flexible, int sessionCount = 10) =>
        await WithDbAsync(async db =>
        {
            var product = new Product
            {
                InstitutionId = institutionId,
                Name = name,
                Type = type,
                Price = 1500m,
                SessionCount = sessionCount,
                IsPublished = false,
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product;
        });

    private async Task<Order> SeedOrderAsync(
        int institutionId, string orderNo, int studentId, int productId,
        DateTime confirmedAt, DateTime createdAt) =>
        await WithDbAsync(async db =>
        {
            var order = new Order
            {
                InstitutionId = institutionId,
                OrderNo = orderNo,
                StudentId = studentId,
                ProductId = productId,
                Amount = 1500m,
                Status = OrderStatus.Active,
                CreatedById = 1,
                ConfirmedAt = confirmedAt,
                CreatedAt = createdAt,
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order;
        });

    private async Task<SessionPack> SeedPackAsync(
        int institutionId, int orderId, int studentId, int productId,
        int total, int remaining, DateTime createdAt, PackStatus status = PackStatus.Active) =>
        await WithDbAsync(async db =>
        {
            var pack = new SessionPack
            {
                InstitutionId = institutionId,
                OrderId = orderId,
                StudentId = studentId,
                ProductId = productId,
                TotalSessions = total,
                RemainingSessions = remaining,
                Status = status,
                CreatedAt = createdAt,
            };
            db.SessionPacks.Add(pack);
            await db.SaveChangesAsync();
            return pack;
        });

    private async Task<ClassTemplate> SeedTemplateAsync(
        int institutionId, int productId, int teacherId, string room, bool isActive = true) =>
        await WithDbAsync(async db =>
        {
            var template = new ClassTemplate
            {
                InstitutionId = institutionId,
                ProductId = productId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = new TimeOnly(18, 0),
                EndTime = new TimeOnly(19, 30),
                TeacherId = teacherId,
                Room = room,
                IsActive = isActive,
            };
            db.ClassTemplates.Add(template);
            await db.SaveChangesAsync();
            return template;
        });

    private async Task SeedEnrollmentAsync(
        int institutionId, int templateId, int studentId,
        EnrollmentStatus status = EnrollmentStatus.Enrolled) =>
        await WithDbAsync(async db =>
        {
            db.ClassEnrollments.Add(new ClassEnrollment
            {
                InstitutionId = institutionId,
                TemplateId = templateId,
                StudentId = studentId,
                EnrolledById = 1,
                Status = status,
            });
            await db.SaveChangesAsync();
        });

    private async Task<ClassSession> SeedSessionAsync(
        int institutionId, int templateId, int teacherId, DateTime startAt,
        SessionStatus status = SessionStatus.Scheduled) =>
        await WithDbAsync(async db =>
        {
            var session = new ClassSession
            {
                InstitutionId = institutionId,
                TemplateId = templateId,
                TeacherId = teacherId,
                StartAt = startAt,
                EndAt = startAt.AddMinutes(90),
                Status = status,
            };
            db.ClassSessions.Add(session);
            await db.SaveChangesAsync();
            return session;
        });

    private async Task<HttpClient> LoginStaffClientAsync(string phone) =>
        AuthedClient(await LoginAsync("/api/org/auth/login", phone));

    private async Task<HttpClient> LoginParentClientAsync(string phone) =>
        AuthedClient(await LoginAsync("/api/parent/auth/login", phone));

    private async Task<string> LoginAsync(string path, string phone)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(path, new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return token!;
    }

    private HttpClient AuthedClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string[] KeysOf(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Parent_with_children_in_two_institutions_sees_both()
    {
        const string phone = "13812001001";
        var orgA = await SeedInstitutionAsync("pa1", "甲舞蹈");
        var orgB = await SeedInstitutionAsync("pa2", "乙体能");
        var orgC = await SeedInstitutionAsync("pa3");
        await SeedParentAsync(phone);

        // 同一手机号在两个机构各一个孩子（设计内豁免：跨机构可见）；第三个机构的
        // 孩子已停用（不可见）；别的手机号的孩子同样不可见。
        var childA = await SeedStudentAsync(orgA.Id, phone, "小明");
        var childB = await SeedStudentAsync(orgB.Id, phone, "小红");
        await SeedStudentAsync(orgC.Id, phone, "已停用", isActive: false);
        await SeedStudentAsync(orgA.Id, "13812001099", "别人家孩子");

        var client = await LoginParentClientAsync(phone);
        var resp = await client.GetAsync("/api/parent/children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var children = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, children.GetArrayLength());

        // 按 institutionId、studentId 升序。
        var first = children[0];
        Assert.Equal(ChildKeys, KeysOf(first));
        Assert.Equal(childA.Id, first.GetProperty("studentId").GetInt32());
        Assert.Equal("小明", first.GetProperty("name").GetString());
        Assert.Equal(orgA.Id, first.GetProperty("institutionId").GetInt32());
        Assert.Equal("甲舞蹈", first.GetProperty("institutionName").GetString());
        Assert.Equal("pa1", first.GetProperty("institutionCode").GetString());
        var second = children[1];
        Assert.Equal(childB.Id, second.GetProperty("studentId").GetInt32());
        Assert.Equal("小红", second.GetProperty("name").GetString());
        Assert.Equal(orgB.Id, second.GetProperty("institutionId").GetInt32());
        Assert.Equal("乙体能", second.GetProperty("institutionName").GetString());
        Assert.Equal("pa2", second.GetProperty("institutionCode").GetString());

        // [ParentOnly]：匿名 401、员工令牌 403（家长端不接受员工身份）。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync("/api/parent/children")).StatusCode);
        var staff = await SeedStaffAsync(orgA.Id, "13912001001", StaffRole.Admin);
        var staffClient = await LoginStaffClientAsync(staff.Phone);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staffClient.GetAsync("/api/parent/children")).StatusCode);
    }

    [Fact]
    public async Task Parent_querying_other_parents_child_returns_404()
    {
        const string mine = "13812002001";
        const string other = "13812002002";
        var org = await SeedInstitutionAsync("pb1");
        await SeedParentAsync(mine);
        await SeedParentAsync(other);
        var myChild = await SeedStudentAsync(org.Id, mine, "我的孩子");
        var otherChild = await SeedStudentAsync(org.Id, other, "别人的孩子");
        // 同手机号但已停用的孩子：与不存在同态 404。
        var inactiveChild = await SeedStudentAsync(org.Id, mine, "已停用孩子", isActive: false);

        var client = await LoginParentClientAsync(other);
        foreach (var path in new[] { "packs", "records", "schedule" })
        {
            // 别人的孩子 → 404；不存在的 id → 同样 404（响应同态，不泄露存在性）。
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/parent/{path}?studentId={myChild.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/parent/{path}?studentId={myChild.Id + 100_000}")).StatusCode);
            // 自己名下但已停用的孩子 → 404。
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/parent/{path}?studentId={inactiveChild.Id}")).StatusCode);
        }

        // 路由本身可用：真正的归属者拿 200 —— 404 来自归属校验，不是路由坏了。
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/parent/packs?studentId={otherChild.Id}")).StatusCode);
        var owner = await LoginParentClientAsync(mine);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.GetAsync($"/api/parent/packs?studentId={myChild.Id}")).StatusCode);

        // studentId 缺失 → 400（不静默当成 0）。
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/parent/packs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/parent/records")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/parent/schedule")).StatusCode);
    }

    [Fact]
    public async Task Parent_packs_reflect_walkin_deduction()
    {
        const string phone = "13812003001";
        var org = await SeedInstitutionAsync("pc1");
        var frontDesk = await SeedStaffAsync(org.Id, "13912003001", StaffRole.FrontDesk);
        await SeedParentAsync(phone);
        var child = await SeedStudentAsync(org.Id, phone, "核销学员");
        var product = await SeedProductAsync(org.Id, "少儿舞蹈课包", sessionCount: 10);

        // 先买过一张后来作废的包（历史仍可见），再买当前在用的包。
        var voidedOrder = await SeedOrderAsync(org.Id, "SO2026100001", child.Id, product.Id,
            confirmedAt: new DateTime(2026, 10, 1, 9, 0, 0), createdAt: new DateTime(2026, 10, 1, 8, 0, 0));
        var voidedPack = await SeedPackAsync(org.Id, voidedOrder.Id, child.Id, product.Id,
            total: 5, remaining: 5, createdAt: new DateTime(2026, 10, 1, 9, 0, 0), PackStatus.Voided);
        var order = await SeedOrderAsync(org.Id, "SO2026102002", child.Id, product.Id,
            confirmedAt: new DateTime(2026, 10, 20, 10, 0, 0), createdAt: new DateTime(2026, 10, 20, 9, 0, 0));
        var pack = await SeedPackAsync(org.Id, order.Id, child.Id, product.Id,
            total: 10, remaining: 10, createdAt: new DateTime(2026, 10, 20, 10, 0, 0));

        // 前台真实核销一次（Task 14 路由）。
        var staffClient = await LoginStaffClientAsync(frontDesk.Phone);
        var walkIn = await staffClient.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId = child.Id, sessionPackId = pack.Id });
        Assert.Equal(HttpStatusCode.OK, walkIn.StatusCode);
        Assert.Equal(9, (await walkIn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("remainingSessions").GetInt32());

        var client = await LoginParentClientAsync(phone);
        var resp = await client.GetAsync($"/api/parent/packs?studentId={child.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var packs = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, packs.GetArrayLength());

        // 新包在前（Id 降序）：余量已反映核销，名称/订单号批量解析。
        var active = packs[0];
        Assert.Equal(PackKeys, KeysOf(active));
        Assert.Equal(pack.Id, active.GetProperty("packId").GetInt32());
        Assert.Equal("少儿舞蹈课包", active.GetProperty("productName").GetString());
        Assert.Equal(10, active.GetProperty("totalSessions").GetInt32());
        Assert.Equal(9, active.GetProperty("remainingSessions").GetInt32());
        Assert.Equal("Active", active.GetProperty("status").GetString());
        Assert.Equal("SO2026102002", active.GetProperty("orderNo").GetString());
        // purchasedAt = 课时包创建时刻（订单确认时刻），不是下单时刻。
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0), active.GetProperty("purchasedAt").GetDateTime());

        // 作废包也列出，状态如实。
        var voided = packs[1];
        Assert.Equal(voidedPack.Id, voided.GetProperty("packId").GetInt32());
        Assert.Equal("Voided", voided.GetProperty("status").GetString());
        Assert.Equal("SO2026100001", voided.GetProperty("orderNo").GetString());
        Assert.Equal(5, voided.GetProperty("remainingSessions").GetInt32());
    }

    [Fact]
    public async Task Parent_records_include_reversal_rows_with_direction()
    {
        const string phone = "13812004001";
        var org = await SeedInstitutionAsync("pd1");
        var frontDesk = await SeedStaffAsync(org.Id, "13912004001", StaffRole.FrontDesk);
        await SeedParentAsync(phone);
        var child = await SeedStudentAsync(org.Id, phone, "流水学员");
        var product = await SeedProductAsync(org.Id, "体能课包", sessionCount: 6);
        var confirmedAt = new DateTime(2026, 10, 21, 10, 0, 0);
        var order = await SeedOrderAsync(org.Id, "SO2026102101", child.Id, product.Id,
            confirmedAt, createdAt: confirmedAt.AddHours(-1));
        var pack = await SeedPackAsync(org.Id, order.Id, child.Id, product.Id,
            total: 6, remaining: 6, createdAt: confirmedAt);

        var staffClient = await LoginStaffClientAsync(frontDesk.Phone);
        var walkIn = await staffClient.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId = child.Id, sessionPackId = pack.Id });
        Assert.Equal(HttpStatusCode.OK, walkIn.StatusCode);
        var consumptionId = (await walkIn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();
        // 当日操作人本人撤销 → 201 镜像行（direction=-1，reason 已 trim）。
        Assert.Equal(HttpStatusCode.Created, (await staffClient.PostAsJsonAsync(
            $"/api/org/consumptions/{consumptionId}/reverse", new { reason = " 客户临时有事 " })).StatusCode);

        var client = await LoginParentClientAsync(phone);
        var resp = await client.GetAsync($"/api/parent/records?studentId={child.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["items", "total"], KeysOf(body));
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        // CreatedAt desc, Id desc：冲正行在前，原扣减行在后（原行 append-only 不改）。
        var reversal = items[0];
        Assert.Equal(RecordKeys, KeysOf(reversal));
        Assert.Equal("Reversal", reversal.GetProperty("kind").GetString());
        Assert.Equal(-1, reversal.GetProperty("direction").GetInt32());
        Assert.Equal("客户临时有事", reversal.GetProperty("reason").GetString());
        Assert.Equal("体能课包", reversal.GetProperty("productName").GetString());
        Assert.Equal(JsonValueKind.Null, reversal.GetProperty("classSessionStartAt").ValueKind);
        var deduction = items[1];
        Assert.Equal(consumptionId, deduction.GetProperty("id").GetInt32());
        Assert.Equal("WalkIn", deduction.GetProperty("kind").GetString());
        Assert.Equal(1, deduction.GetProperty("direction").GetInt32());
        Assert.Equal(JsonValueKind.Null, deduction.GetProperty("reason").ValueKind);

        // 日期窗与分页（from 含当日零点、to 排他次日零点，与员工端同语义）。
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Equal(2, await GetTotalAsync(client, child.Id, $"from={today}&to={today}"));
        Assert.Equal(0, await GetTotalAsync(client, child.Id, $"from={tomorrow}"));
        Assert.Equal(0, await GetTotalAsync(client, child.Id, $"to={yesterday}"));
        // 非法日期 → 400（安全解析，绝不 500）。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/parent/records?studentId={child.Id}&from=2026/10/01")).StatusCode);

        var paged = await client.GetAsync($"/api/parent/records?studentId={child.Id}&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        var pagedBody = await paged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, pagedBody.GetProperty("total").GetInt32());
        Assert.Equal(1, pagedBody.GetProperty("items").GetArrayLength());
    }

    private async Task<int> GetTotalAsync(HttpClient client, int studentId, string query)
    {
        var resp = await client.GetAsync($"/api/parent/records?studentId={studentId}&{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("total").GetInt32();
    }

    [Fact]
    public async Task Parent_schedule_returns_only_future_sessions_of_enrolled_classes()
    {
        const string phone = "13812005001";
        var org = await SeedInstitutionAsync("pe1", "戊舞蹈工作室");
        var teacher = await SeedStaffAsync(org.Id, "13912005001", StaffRole.Teacher, "李老师");
        await SeedParentAsync(phone);
        var child = await SeedStudentAsync(org.Id, phone, "课表学员");
        var product = await SeedProductAsync(org.Id, "少儿舞蹈基础班", ProductType.Fixed, 24);

        // 在班且启用的模板（可见）、退班模板（不可见）、从未报名的模板（不可见）、
        // 已停用的模板（不可见，即便报名行仍是 Enrolled）。
        var enrolled = await SeedTemplateAsync(org.Id, product.Id, teacher.Id, "301");
        var withdrawn = await SeedTemplateAsync(org.Id, product.Id, teacher.Id, "302");
        var neverEnrolled = await SeedTemplateAsync(org.Id, product.Id, teacher.Id, "303");
        var inactive = await SeedTemplateAsync(org.Id, product.Id, teacher.Id, "304", isActive: false);
        await SeedEnrollmentAsync(org.Id, enrolled.Id, child.Id);
        await SeedEnrollmentAsync(org.Id, withdrawn.Id, child.Id, EnrollmentStatus.Withdrawn);
        await SeedEnrollmentAsync(org.Id, inactive.Id, child.Id);

        var now = DateTime.Now;
        var visible = await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(2).Date.AddHours(18));
        var later = await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(5).Date.AddHours(18));
        // 过去的、已完成的、已取消的实例都不属于"未来课表"。
        await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(-1).Date.AddHours(18));
        await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(3).Date.AddHours(18), SessionStatus.Completed);
        await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(4).Date.AddHours(18), SessionStatus.Cancelled);
        await SeedSessionAsync(org.Id, withdrawn.Id, teacher.Id, now.AddDays(2).Date.AddHours(19));
        await SeedSessionAsync(org.Id, neverEnrolled.Id, teacher.Id, now.AddDays(2).Date.AddHours(20));
        await SeedSessionAsync(org.Id, inactive.Id, teacher.Id, now.AddDays(2).Date.AddHours(21));
        // 超出默认 14 天窗口：默认不可见，days=30 时可见。
        var beyond = await SeedSessionAsync(org.Id, enrolled.Id, teacher.Id, now.AddDays(20).Date.AddHours(18));

        var client = await LoginParentClientAsync(phone);
        var resp = await client.GetAsync($"/api/parent/schedule?studentId={child.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, rows.GetArrayLength());

        // StartAt 升序；room 读时取自模板，teacherName/productName/institutionName 批量解析。
        Assert.Equal(ScheduleKeys, KeysOf(rows[0]));
        Assert.Equal(visible.StartAt, rows[0].GetProperty("startAt").GetDateTime());
        Assert.Equal(visible.EndAt, rows[0].GetProperty("endAt").GetDateTime());
        Assert.Equal("少儿舞蹈基础班", rows[0].GetProperty("productName").GetString());
        Assert.Equal("李老师", rows[0].GetProperty("teacherName").GetString());
        Assert.Equal("301", rows[0].GetProperty("room").GetString());
        Assert.Equal("戊舞蹈工作室", rows[0].GetProperty("institutionName").GetString());
        Assert.Equal(later.StartAt, rows[1].GetProperty("startAt").GetDateTime());

        // days=30：把 20 天后那节也纳入（仍按时间升序，排在最后）。
        var wider = await client.GetAsync($"/api/parent/schedule?studentId={child.Id}&days=30");
        Assert.Equal(HttpStatusCode.OK, wider.StatusCode);
        var wideRows = await wider.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, wideRows.GetArrayLength());
        Assert.Equal(beyond.StartAt, wideRows[2].GetProperty("startAt").GetDateTime());
    }

    [Fact]
    public async Task Parent_schedule_days_param_is_safe_parsed_and_clamped()
    {
        const string phone = "13812006001";
        var org = await SeedInstitutionAsync("pf1");
        var teacher = await SeedStaffAsync(org.Id, "13912006001", StaffRole.Teacher);
        await SeedParentAsync(phone);
        var child = await SeedStudentAsync(org.Id, phone, "窗口学员");
        var product = await SeedProductAsync(org.Id, "窗口课包", ProductType.Fixed, 24);
        var template = await SeedTemplateAsync(org.Id, product.Id, teacher.Id, "101");
        await SeedEnrollmentAsync(org.Id, template.Id, child.Id);

        var now = DateTime.Now;
        var inHalfADay = await SeedSessionAsync(org.Id, template.Id, teacher.Id, now.AddHours(12));
        var inTwoDays = await SeedSessionAsync(org.Id, template.Id, teacher.Id, now.AddDays(2));
        var inThirtyDays = await SeedSessionAsync(org.Id, template.Id, teacher.Id, now.AddDays(30));
        var inSeventyDays = await SeedSessionAsync(org.Id, template.Id, teacher.Id, now.AddDays(70));

        var client = await LoginParentClientAsync(phone);

        // 非法 days → 400（安全解析，绝不 500）。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/parent/schedule?studentId={child.Id}&days=abc")).StatusCode);

        // days=0 → 钳制为 1：12 小时后的可见，2 天后的不可见。
        Assert.Equal([inHalfADay.StartAt], await StartAtsAsync(client, child.Id, "days=0"));

        // days=1000 → 钳制为 60：30 天内的可见，70 天后的不可见。
        var upper = await StartAtsAsync(client, child.Id, "days=1000");
        Assert.Equal([inHalfADay.StartAt, inTwoDays.StartAt, inThirtyDays.StartAt], upper);
        Assert.DoesNotContain(inSeventyDays.StartAt, upper);
    }

    private async Task<List<DateTime>> StartAtsAsync(HttpClient client, int studentId, string query)
    {
        var resp = await client.GetAsync($"/api/parent/schedule?studentId={studentId}&{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return rows.EnumerateArray()
            .Select(r => r.GetProperty("startAt").GetDateTime())
            .ToList();
    }

    [Fact]
    public async Task Profile_update_reflects_in_public_org_page()
    {
        var org = await SeedInstitutionAsync("pg1", "旧名称");
        var other = await SeedInstitutionAsync("pg2", "隔壁机构");
        var admin = await SeedStaffAsync(org.Id, "13912007001", StaffRole.Admin);
        var teacher = await SeedStaffAsync(org.Id, "13912007002", StaffRole.Teacher);
        var otherAdmin = await SeedStaffAsync(other.Id, "13912007003", StaffRole.Admin);

        // 员工（任意角色）可读；匿名不可读。
        var teacherClient = await LoginStaffClientAsync(teacher.Phone);
        var get = await teacherClient.GetAsync("/api/org/profile");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var profile = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProfileKeys, KeysOf(profile));
        Assert.Equal("pg1", profile.GetProperty("code").GetString());
        Assert.Equal("旧名称", profile.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync("/api/org/profile")).StatusCode);

        // 写入口 [AdminOnly]：教师 403。
        Assert.Equal(HttpStatusCode.Forbidden, (await teacherClient.PutAsJsonAsync(
            "/api/org/profile", new { name = "教师改名" })).StatusCode);

        // 管理员改全部字段 → 204，且匿名公开页立刻反映。
        var adminClient = await LoginStaffClientAsync(admin.Phone);
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PutAsJsonAsync(
            "/api/org/profile",
            new
            {
                name = "新名称",
                logoPath = "/uploads/logo/pg1.png",
                introText = "新简介",
                contactPhone = "13900001111",
                address = "新地址 1 号",
            })).StatusCode);

        var publicBody = await (await factory.CreateClient().GetAsync("/api/public/orgs/pg1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("新名称", publicBody.GetProperty("name").GetString());
        Assert.Equal("/uploads/logo/pg1.png", publicBody.GetProperty("logoPath").GetString());
        Assert.Equal("新简介", publicBody.GetProperty("introText").GetString());
        Assert.Equal("13900001111", publicBody.GetProperty("contactPhone").GetString());
        Assert.Equal("新地址 1 号", publicBody.GetProperty("address").GetString());

        // 只提交部分字段：其余字段保持不变（仅非 null 字段更新）。
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PutAsJsonAsync(
            "/api/org/profile", new { introText = "只改简介" })).StatusCode);
        var after = await (await adminClient.GetAsync("/api/org/profile"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("新名称", after.GetProperty("name").GetString());
        Assert.Equal("只改简介", after.GetProperty("introText").GetString());
        Assert.Equal("新地址 1 号", after.GetProperty("address").GetString());

        // name 校验：空白 400、超 50 字 400、恰好 50 字通过。
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(
            "/api/org/profile", new { name = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(
            "/api/org/profile", new { name = new string('名', 51) })).StatusCode);
        var exact50 = new string('名', 50);
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PutAsJsonAsync(
            "/api/org/profile", new { name = exact50 })).StatusCode);
        Assert.Equal(exact50, (await (await adminClient.GetAsync("/api/org/profile"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        // Institution 非 ITenanted：读写走 IgnoreQueryFilters + 显式 Id==令牌机构。
        // 隔壁机构的管理员只能改自己那份，本机构的名字纹丝不动。
        var otherAdminClient = await LoginStaffClientAsync(otherAdmin.Phone);
        Assert.Equal(HttpStatusCode.NoContent, (await otherAdminClient.PutAsJsonAsync(
            "/api/org/profile", new { name = "隔壁新名称" })).StatusCode);
        Assert.Equal(exact50, (await (await factory.CreateClient().GetAsync("/api/public/orgs/pg1"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal("隔壁新名称", (await (await factory.CreateClient().GetAsync("/api/public/orgs/pg2"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }
}
