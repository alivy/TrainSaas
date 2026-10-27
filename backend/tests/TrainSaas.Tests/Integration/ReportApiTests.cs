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
/// 报表路由的集成测试（Task 15）：GET /api/org/reports/sales 与
/// GET /api/org/reports/teachers，全部走真实 HTTP + 真实员工登录令牌。
/// 口径（Active+ConfirmedAt 窗口 / Completed+Present 窗口）在
/// <see cref="Unit.ReportTests"/> 用 TestDb 逐条钉住，这里钉住 HTTP 面：
/// 响应行形态、角色闸门（销售报表 Admin 全员 / Sales 仅自己 / 其他 403；
/// 教师报表 Admin 全员 / Teacher 仅自己 / 其他 403）、from/to 必填且严格
/// yyyy-MM-dd（非法或缺失 400）、from&gt;to 400、匿名 401。
/// </summary>
public class ReportApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] SalesRowKeys = ["orderCount", "staffId", "staffName", "totalAmount"];
    private static readonly string[] TeacherRowKeys =
        ["completedSessions", "presentMarks", "teacherId", "teacherName"];

    private const string Window = "from=2026-10-01&to=2026-10-31";

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

    private async Task<Institution> SeedInstitutionAsync(string code) =>
        await WithDbAsync(async db =>
        {
            var institution = new Institution
            {
                Code = code,
                Name = $"机构{code}",
                Status = InstitutionStatus.Active,
            };
            db.Institutions.Add(institution);
            await db.SaveChangesAsync();
            return institution;
        });

    private async Task<Staff> SeedStaffAsync(int institutionId, string phone, StaffRole role, string name) =>
        await WithDbAsync(async db =>
        {
            var hasher = factory.Services.GetRequiredService<IPasswordHasherService>();
            var staff = new Staff
            {
                InstitutionId = institutionId,
                Name = name,
                Phone = phone,
                PasswordHash = hasher.Hash(DefaultPassword.Initial),
                Role = role,
                IsActive = true,
            };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            return staff;
        });

    private async Task SeedOrderAsync(
        int institutionId, string orderNo, int studentId, int productId,
        int? salesId, decimal amount, OrderStatus status, DateTime? confirmedAt) =>
        await WithDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                InstitutionId = institutionId,
                OrderNo = orderNo,
                StudentId = studentId,
                ProductId = productId,
                Amount = amount,
                SalesId = salesId,
                Status = status,
                CreatedById = salesId ?? 1,
                ConfirmedAt = confirmedAt,
            });
            await db.SaveChangesAsync();
        });

    private async Task<ClassSession> SeedSessionAsync(
        int institutionId, int teacherId, DateTime startAt, SessionStatus status) =>
        await WithDbAsync(async db =>
        {
            var session = new ClassSession
            {
                InstitutionId = institutionId,
                TemplateId = 950 + institutionId,
                TeacherId = teacherId,
                StartAt = startAt,
                EndAt = startAt.AddMinutes(90),
                Status = status,
            };
            db.ClassSessions.Add(session);
            await db.SaveChangesAsync();
            return session;
        });

    private async Task SeedAttendanceAsync(
        int institutionId, int classSessionId, int studentId, AttendanceMark mark) =>
        await WithDbAsync(async db =>
        {
            db.Attendances.Add(new Attendance
            {
                InstitutionId = institutionId,
                ClassSessionId = classSessionId,
                StudentId = studentId,
                Mark = mark,
                OperatorId = 1,
            });
            await db.SaveChangesAsync();
        });

    private async Task<HttpClient> LoginStaffClientAsync(string phone)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/org/auth/login", new { phone, password = DefaultPassword.Initial });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!);
        return authed;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Sales_report_route_returns_rows_for_admin_and_own_row_for_sales()
    {
        var org = await SeedInstitutionAsync("rp1");
        var admin = await SeedStaffAsync(org.Id, "13913001001", StaffRole.Admin, "管理员");
        var salesA = await SeedStaffAsync(org.Id, "13913001002", StaffRole.Sales, "销售甲");
        var salesB = await SeedStaffAsync(org.Id, "13913001003", StaffRole.Sales, "销售乙");
        var teacher = await SeedStaffAsync(org.Id, "13913001004", StaffRole.Teacher, "教师甲");
        var frontDesk = await SeedStaffAsync(org.Id, "13913001005", StaffRole.FrontDesk, "前台甲");

        var (studentId, productId) = await WithDbAsync(async db =>
        {
            var student = new Student
            {
                InstitutionId = org.Id, Name = "报表学员", Phone = "13813001001", IsActive = true,
            };
            var product = new Product
            {
                InstitutionId = org.Id, Name = "报表课包", Type = ProductType.Flexible,
                Price = 1000m, SessionCount = 10,
            };
            db.Students.Add(student);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return (student.Id, product.Id);
        });

        // 窗口内：甲两单、乙一单；另有作废单与窗外单不计。
        await SeedOrderAsync(org.Id, "SO2026110001", studentId, productId, salesA.Id,
            1000.50m, OrderStatus.Active, new DateTime(2026, 10, 5, 10, 0, 0));
        await SeedOrderAsync(org.Id, "SO2026110002", studentId, productId, salesA.Id,
            500.25m, OrderStatus.Active, new DateTime(2026, 10, 6, 10, 0, 0));
        await SeedOrderAsync(org.Id, "SO2026110003", studentId, productId, salesB.Id,
            300m, OrderStatus.Active, new DateTime(2026, 10, 7, 10, 0, 0));
        await SeedOrderAsync(org.Id, "SO2026110004", studentId, productId, salesA.Id,
            800m, OrderStatus.Voided, new DateTime(2026, 10, 8, 10, 0, 0));
        await SeedOrderAsync(org.Id, "SO2026110005", studentId, productId, salesA.Id,
            100m, OrderStatus.Active, new DateTime(2026, 9, 1, 10, 0, 0));

        // Admin：全员（有成交的）行，按 staffId 升序。
        var adminClient = await LoginStaffClientAsync(admin.Phone);
        var rows = await GetAsync(adminClient, $"/api/org/reports/sales?{Window}");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(SalesRowKeys, rows[0].EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(salesA.Id, rows[0].GetProperty("staffId").GetInt32());
        Assert.Equal("销售甲", rows[0].GetProperty("staffName").GetString());
        Assert.Equal(2, rows[0].GetProperty("orderCount").GetInt32());
        Assert.Equal(1500.75m, rows[0].GetProperty("totalAmount").GetDecimal());
        Assert.Equal(salesB.Id, rows[1].GetProperty("staffId").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("orderCount").GetInt32());
        Assert.Equal(300m, rows[1].GetProperty("totalAmount").GetDecimal());

        // Sales：只有本人一行。
        var salesClient = await LoginStaffClientAsync(salesA.Phone);
        var own = await GetAsync(salesClient, $"/api/org/reports/sales?{Window}");
        var single = Assert.Single(own.EnumerateArray());
        Assert.Equal(salesA.Id, single.GetProperty("staffId").GetInt32());
        Assert.Equal(2, single.GetProperty("orderCount").GetInt32());

        // 其他角色 403（服务层裁决，令牌本身有效）。
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginStaffClientAsync(teacher.Phone))
            .GetAsync($"/api/org/reports/sales?{Window}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginStaffClientAsync(frontDesk.Phone))
            .GetAsync($"/api/org/reports/sales?{Window}")).StatusCode);
        // 匿名 401。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync($"/api/org/reports/sales?{Window}")).StatusCode);
    }

    [Fact]
    public async Task Teacher_report_route_returns_rows_for_admin_and_own_row_for_teacher()
    {
        var org = await SeedInstitutionAsync("rp2");
        var admin = await SeedStaffAsync(org.Id, "13913002001", StaffRole.Admin, "管理员");
        var teacherA = await SeedStaffAsync(org.Id, "13913002002", StaffRole.Teacher, "教师甲");
        var teacherB = await SeedStaffAsync(org.Id, "13913002003", StaffRole.Teacher, "教师乙");
        var sales = await SeedStaffAsync(org.Id, "13913002004", StaffRole.Sales, "销售甲");

        // 甲：窗内 2 个 Completed（+1 个 Scheduled 不计）、3 个 Present（Leave 不计）。
        var a1 = await SeedSessionAsync(org.Id, teacherA.Id, new DateTime(2026, 10, 5, 18, 0, 0), SessionStatus.Completed);
        var a2 = await SeedSessionAsync(org.Id, teacherA.Id, new DateTime(2026, 10, 12, 18, 0, 0), SessionStatus.Completed);
        await SeedSessionAsync(org.Id, teacherA.Id, new DateTime(2026, 10, 19, 18, 0, 0), SessionStatus.Scheduled);
        await SeedAttendanceAsync(org.Id, a1.Id, 601, AttendanceMark.Present);
        await SeedAttendanceAsync(org.Id, a1.Id, 602, AttendanceMark.Present);
        await SeedAttendanceAsync(org.Id, a1.Id, 603, AttendanceMark.Leave);
        await SeedAttendanceAsync(org.Id, a2.Id, 601, AttendanceMark.Present);
        // 乙：窗内 1 个 Completed，无点名。
        await SeedSessionAsync(org.Id, teacherB.Id, new DateTime(2026, 10, 6, 18, 0, 0), SessionStatus.Completed);

        var adminClient = await LoginStaffClientAsync(admin.Phone);
        var rows = await GetAsync(adminClient, $"/api/org/reports/teachers?{Window}");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(TeacherRowKeys, rows[0].EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(teacherA.Id, rows[0].GetProperty("teacherId").GetInt32());
        Assert.Equal("教师甲", rows[0].GetProperty("teacherName").GetString());
        Assert.Equal(2, rows[0].GetProperty("completedSessions").GetInt32());
        Assert.Equal(3, rows[0].GetProperty("presentMarks").GetInt32());
        Assert.Equal(teacherB.Id, rows[1].GetProperty("teacherId").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("completedSessions").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("presentMarks").GetInt32());

        // Teacher：只有本人一行。
        var teacherClient = await LoginStaffClientAsync(teacherA.Phone);
        var own = await GetAsync(teacherClient, $"/api/org/reports/teachers?{Window}");
        var single = Assert.Single(own.EnumerateArray());
        Assert.Equal(teacherA.Id, single.GetProperty("teacherId").GetInt32());
        Assert.Equal(2, single.GetProperty("completedSessions").GetInt32());
        Assert.Equal(3, single.GetProperty("presentMarks").GetInt32());

        // 其他角色（Sales/FrontDesk）403。
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginStaffClientAsync(sales.Phone))
            .GetAsync($"/api/org/reports/teachers?{Window}")).StatusCode);
    }

    [Fact]
    public async Task Report_routes_reject_missing_or_invalid_date_window()
    {
        var org = await SeedInstitutionAsync("rp3");
        var admin = await SeedStaffAsync(org.Id, "13913003001", StaffRole.Admin, "管理员");
        var client = await LoginStaffClientAsync(admin.Phone);

        foreach (var path in new[] { "/api/org/reports/sales", "/api/org/reports/teachers" })
        {
            // from/to 必填；严格 yyyy-MM-dd；from>to 一律 400（安全解析，绝不 500）。
            foreach (var query in new[]
                     {
                         "", "?from=2026-10-01", "?to=2026-10-31",
                         "?from=2026/10/01&to=2026-10-31", "?from=2026-10-01&to=31-10-2026",
                         "?from=banana&to=2026-10-31", "?from=2026-11-01&to=2026-10-01",
                     })
            {
                var resp = await client.GetAsync($"{path}{query}");
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            }

            // 合法窗口（哪怕空数据）→ 200 空数组。
            var ok = await GetAsync(client, $"{path}?{Window}");
            Assert.Equal(0, ok.GetArrayLength());
        }
    }
}
