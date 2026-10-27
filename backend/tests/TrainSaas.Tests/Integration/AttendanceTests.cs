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
/// 点名路由集成测试（Task 13）：POST /api/org/sessions/{id}/attendance
/// [StaffOnly]，细权限（Admin 或本课次授课教师）在服务层。真实登录令牌走
/// 真实 HTTP，覆盖：200 响应形态 {results:[{studentId,ok,error}]}、点名后
/// 课次转 Completed 且详情可见点名记录、课时包扣减；非法 mark 字符串
/// （大小写不符/未知值/非字符串/缺字段）→ 400；marks 缺失或空数组 → 400；
/// 非授课教师 → 403；名单外无包学员单项失败（部分成功）不影响其他学员。
/// </summary>
public class AttendanceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>点名结果项的精确字段集（error 为 null 时键仍在）。</summary>
    private static readonly string[] ResultKeys = ["error", "ok", "studentId"];

    private sealed record Seeded(
        Staff Teacher,
        Staff OtherTeacher,
        Staff Admin,
        Product Product,
        ClassTemplate Template,
        ClassSession Session,
        Student Enrolled,
        Student Adhoc,
        Student Stranger,
        SessionPack EnrolledPack,
        SessionPack AdhocPack);

    private async Task<Seeded> SeedAsync(string code, int seq)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var institution = new Institution
        {
            Code = code,
            Name = $"机构{code}",
            Status = InstitutionStatus.Active,
        };
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();

        Staff Staff(string phone, StaffRole role) => new()
        {
            InstitutionId = institution.Id,
            Name = $"员工{phone}",
            Phone = phone,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
            Role = role,
            IsActive = true,
        };
        var teacher = Staff($"139777{seq}0001", StaffRole.Teacher);
        var otherTeacher = Staff($"139777{seq}0002", StaffRole.Teacher);
        var admin = Staff($"139777{seq}0003", StaffRole.Admin);
        db.Staff.AddRange(teacher, otherTeacher, admin);

        var product = new Product
        {
            InstitutionId = institution.Id,
            Name = "少儿英语固定班",
            Type = ProductType.Fixed,
            Price = 1980m,
            SessionCount = 10,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var template = new ClassTemplate
        {
            InstitutionId = institution.Id,
            ProductId = product.Id,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            TeacherId = teacher.Id,
            IsActive = true,
        };
        db.ClassTemplates.Add(template);
        await db.SaveChangesAsync();

        var session = new ClassSession
        {
            InstitutionId = institution.Id,
            TemplateId = template.Id,
            TeacherId = teacher.Id,
            StartAt = new DateTime(2026, 11, 2, 18, 0, 0),
            EndAt = new DateTime(2026, 11, 2, 19, 30, 0),
            Status = SessionStatus.Scheduled,
        };
        db.ClassSessions.Add(session);

        Student Student(string name, string phone) => new()
        {
            InstitutionId = institution.Id,
            Name = name,
            Phone = phone,
            IsActive = true,
        };
        var enrolled = Student("在班学员", $"138777{seq}0001");
        var adhoc = Student("临时学员", $"138777{seq}0002");
        var stranger = Student("名单外学员", $"138777{seq}0003");
        db.Students.AddRange(enrolled, adhoc, stranger);
        await db.SaveChangesAsync();

        db.ClassEnrollments.Add(new ClassEnrollment
        {
            InstitutionId = institution.Id,
            TemplateId = template.Id,
            StudentId = enrolled.Id,
            EnrolledById = admin.Id,
            Status = EnrollmentStatus.Enrolled,
        });
        var enrolledPack = new SessionPack
        {
            InstitutionId = institution.Id,
            OrderId = 8_000 + seq * 10 + 1,
            StudentId = enrolled.Id,
            ProductId = product.Id,
            TotalSessions = 5,
            RemainingSessions = 5,
            Status = PackStatus.Active,
        };
        var adhocPack = new SessionPack
        {
            InstitutionId = institution.Id,
            OrderId = 8_000 + seq * 10 + 2,
            StudentId = adhoc.Id,
            ProductId = product.Id,
            TotalSessions = 3,
            RemainingSessions = 3,
            Status = PackStatus.Active,
        };
        db.SessionPacks.AddRange(enrolledPack, adhocPack);
        await db.SaveChangesAsync();

        return new Seeded(
            teacher, otherTeacher, admin, product, template, session,
            enrolled, adhoc, stranger, enrolledPack, adhocPack);
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

    [Fact]
    public async Task Roll_call_returns_results_deducts_pack_and_completes_session()
    {
        var seeded = await SeedAsync("att1", 1);
        var teacherClient = await LoginStaffClientAsync(seeded.Teacher.Phone);

        var resp = await teacherClient.PostAsJsonAsync(
            $"/api/org/sessions/{seeded.Session.Id}/attendance",
            new
            {
                marks = new object[]
                {
                    new { studentId = seeded.Stranger.Id, mark = "Present" },
                    new { studentId = seeded.Enrolled.Id, mark = "Present" },
                },
            });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["results"], body.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        // 部分成功：名单外无包学员失败，在班学员成功，顺序与请求一致。
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        var failed = results[0];
        Assert.Equal(ResultKeys, failed.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(seeded.Stranger.Id, failed.GetProperty("studentId").GetInt32());
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal("不在名单且无可用课时包", failed.GetProperty("error").GetString());
        var ok = results[1];
        Assert.Equal(seeded.Enrolled.Id, ok.GetProperty("studentId").GetInt32());
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("error").ValueKind);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 只为成功学员落一行点名 + 一条 +1 流水；包扣 1。
            var row = Assert.Single(await db.Attendances.IgnoreQueryFilters()
                .Where(a => a.ClassSessionId == seeded.Session.Id).ToListAsync());
            Assert.Equal(seeded.Enrolled.Id, row.StudentId);
            Assert.Equal(AttendanceMark.Present, row.Mark);
            Assert.Equal(seeded.Teacher.Id, row.OperatorId);
            Assert.Single(await db.Consumptions.IgnoreQueryFilters()
                .Where(c => c.SessionPackId == seeded.EnrolledPack.Id && c.Direction == 1).ToListAsync());
            Assert.Equal(4, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.EnrolledPack.Id).Select(p => p.RemainingSessions).SingleAsync());
            Assert.Equal(SessionStatus.Completed, await db.ClassSessions.IgnoreQueryFilters()
                .Where(s => s.Id == seeded.Session.Id).Select(s => s.Status).SingleAsync());
        }

        // 详情回读：attendance 带新行、status=Completed。
        var detail = await (await teacherClient.GetAsync($"/api/org/sessions/{seeded.Session.Id}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", detail.GetProperty("status").GetString());
        var attendanceItem = detail.GetProperty("attendance").EnumerateArray().Single();
        Assert.Equal(seeded.Enrolled.Id, attendanceItem.GetProperty("studentId").GetInt32());
        Assert.Equal("Present", attendanceItem.GetProperty("mark").GetString());
        Assert.Equal(seeded.Teacher.Id, attendanceItem.GetProperty("operatorId").GetInt32());
    }

    [Fact]
    public async Task Invalid_mark_string_returns_400()
    {
        var seeded = await SeedAsync("att2", 2);
        var adminClient = await LoginStaffClientAsync(seeded.Admin.Phone);
        var url = $"/api/org/sessions/{seeded.Session.Id}/attendance";

        // mark 是区分大小写的字符串枚举：大小写不符/未知值一律 400。
        foreach (var badMark in new[] { "present", "PRESENT", "Late", "", "请假" })
        {
            var resp = await adminClient.PostAsJsonAsync(url,
                new { marks = new object[] { new { studentId = seeded.Enrolled.Id, mark = badMark } } });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 非字符串与缺失字段同样 400（绝不 500，也绝不默认成 Present）。
        var numeric = await adminClient.PostAsJsonAsync(url,
            new { marks = new object[] { new { studentId = seeded.Enrolled.Id, mark = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        var missing = await adminClient.PostAsJsonAsync(url,
            new { marks = new object[] { new { studentId = seeded.Enrolled.Id } } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // marks 缺失 / null / 空数组 → 400（BusinessRule）。
        var noMarks = await adminClient.PostAsJsonAsync(url, new { });
        Assert.Equal(HttpStatusCode.BadRequest, noMarks.StatusCode);
        var nullMarks = await adminClient.PostAsJsonAsync(url, new { marks = (object?)null });
        Assert.Equal(HttpStatusCode.BadRequest, nullMarks.StatusCode);
        var emptyMarks = await adminClient.PostAsJsonAsync(url, new { marks = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, emptyMarks.StatusCode);
        Assert.Contains("点名", (await emptyMarks.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString());

        // 所有被拒请求都不落任何行。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Attendances.IgnoreQueryFilters()
                .Where(a => a.ClassSessionId == seeded.Session.Id).ToListAsync());
            Assert.Empty(await db.Consumptions.IgnoreQueryFilters()
                .Where(c => c.SessionPackId == seeded.EnrolledPack.Id).ToListAsync());
            Assert.Equal(5, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.EnrolledPack.Id).Select(p => p.RemainingSessions).SingleAsync());
            Assert.Equal(SessionStatus.Scheduled, await db.ClassSessions.IgnoreQueryFilters()
                .Where(s => s.Id == seeded.Session.Id).Select(s => s.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Other_teacher_marking_returns_403_and_adhoc_with_pack_succeeds()
    {
        var seeded = await SeedAsync("att3", 3);

        // 非授课教师（教师令牌真实有效）：403，不落行。
        var otherClient = await LoginStaffClientAsync(seeded.OtherTeacher.Phone);
        var denied = await otherClient.PostAsJsonAsync(
            $"/api/org/sessions/{seeded.Session.Id}/attendance",
            new { marks = new object[] { new { studentId = seeded.Enrolled.Id, mark = "Present" } } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // 未知课次 → 404。
        var unknown = await otherClient.PostAsJsonAsync(
            "/api/org/sessions/999999/attendance",
            new { marks = new object[] { new { studentId = seeded.Enrolled.Id, mark = "Present" } } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // 名单外临时学员持可用包：点名成功且扣自己的包。
        var teacherClient = await LoginStaffClientAsync(seeded.Teacher.Phone);
        var resp = await teacherClient.PostAsJsonAsync(
            $"/api/org/sessions/{seeded.Session.Id}/attendance",
            new { marks = new object[] { new { studentId = seeded.Adhoc.Id, mark = "Present" } } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("results").EnumerateArray().Single();
        Assert.True(result.GetProperty("ok").GetBoolean());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Attendances.IgnoreQueryFilters()
                .Where(a => a.ClassSessionId == seeded.Session.Id && a.StudentId == seeded.Adhoc.Id)
                .ToListAsync());
            Assert.Single(await db.Consumptions.IgnoreQueryFilters()
                .Where(c => c.SessionPackId == seeded.AdhocPack.Id && c.Direction == 1).ToListAsync());
            Assert.Equal(2, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.AdhocPack.Id).Select(p => p.RemainingSessions).SingleAsync());
            // 被拒的其他教师请求没有留下任何点名/流水。
            Assert.Single(await db.Attendances.IgnoreQueryFilters()
                .Where(a => a.ClassSessionId == seeded.Session.Id).ToListAsync());
        }
    }
}
