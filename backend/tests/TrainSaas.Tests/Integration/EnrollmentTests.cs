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
/// 班级报名集成测试（Task 12）：报班/退班/名册。覆盖：
/// 报名 201 {id}（重报复用同一行）；重复报名 409；超容量 400 "班级已满"
///（在班数只数 Enrolled，退班释放名额）；模板/学员停用 400；重报复用原行
/// 翻回 Enrolled（同 id、EnrolledById/EnrolledAt 更新）；无在班记录退班 404；
/// 名册仅 Enrolled、按 studentId 升序、字段 {studentId,name,phone,status,
/// enrolledAt}；名册访问 = Admin 或该模板授课教师（其他教师/销售/前台 403），
/// 写入口 AdminOnly（教师 403）；跨机构模板/学员与未知 id 一律 404（租户
/// 过滤器，不泄露存在性）；模板列表含 productName/teacherName/enrolledCount。
///
/// ApiFactory 运行于 SeedDemoData=false：每个测试经 factory.Services.CreateScope()
/// 自行播种，并以真实登录令牌走真实 HTTP。机构编码/手机号在共享库内全局唯一。
/// </summary>
public class EnrollmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>报名 201 响应的精确字段集：只有 {id}。</summary>
    private static readonly string[] EnrollmentKeys = ["id"];

    /// <summary>名册项的精确字段集。</summary>
    private static readonly string[] RosterKeys =
        ["enrolledAt", "name", "phone", "status", "studentId"];

    /// <summary>模板列表项的精确字段集（Task 12 增 productName/teacherName/enrolledCount）。</summary>
    private static readonly string[] TemplateListKeys =
        ["capacity", "dayOfWeek", "endTime", "enrolledCount", "id", "isActive", "productId",
            "productName", "room", "startTime", "teacherId", "teacherName"];

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

    private async Task<Student> SeedStudentAsync(
        int institutionId, string name, string phone, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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

    /// <summary>POST /api/org/templates/{id}/enrollments → 响应（状态码由调用方断言）。</summary>
    private static Task<HttpResponseMessage> EnrollAsync(HttpClient client, int templateId, int studentId) =>
        client.PostAsJsonAsync($"/api/org/templates/{templateId}/enrollments", new { studentId });

    [Fact]
    public async Task Enroll_then_roster_contains_student()
    {
        var institution = await SeedInstitutionAsync("en1");
        var admin = await SeedStaffAsync(institution.Id, "13977600001");
        var teacher = await SeedStaffAsync(institution.Id, "13977600101", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "少儿英语固定班");
        var client = await LoginStaffClientAsync("13977600001");

        var template = await CreateTemplateAsync(client, product.Id, teacher.Id, room: "A101", capacity: 12);
        var templateId = template.GetProperty("id").GetInt32();

        var student1 = await SeedStudentAsync(institution.Id, "报名学员一", "13877600001");
        var student2 = await SeedStudentAsync(institution.Id, "报名学员二", "13877600002");
        var student3 = await SeedStudentAsync(institution.Id, "退班学员", "13877600003");

        // 退班行是历史：直接播种，名册不得包含。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClassEnrollments.Add(new ClassEnrollment
            {
                InstitutionId = institution.Id,
                TemplateId = templateId,
                StudentId = student3.Id,
                EnrolledById = admin.Id,
                Status = EnrollmentStatus.Withdrawn,
            });
            await db.SaveChangesAsync();
        }

        // 报名 → 201，响应体恰为 {id}。
        foreach (var student in new[] { student1, student2 })
        {
            var resp = await EnrollAsync(client, templateId, student.Id);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(EnrollmentKeys, created.EnumerateObject().Select(p => p.Name));
            Assert.True(created.GetProperty("id").GetInt32() > 0);
        }

        // 名册（Admin）：仅 Enrolled、按 studentId 升序、字段精确。
        var rosterResp = await client.GetAsync($"/api/org/templates/{templateId}/enrollments");
        Assert.Equal(HttpStatusCode.OK, rosterResp.StatusCode);
        var roster = await rosterResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, roster.ValueKind);
        var items = roster.EnumerateArray().ToList();
        Assert.Equal(
            new[] { student1.Id, student2.Id },
            items.Select(r => r.GetProperty("studentId").GetInt32()).ToArray());
        Assert.All(items, r =>
        {
            Assert.Equal(RosterKeys, r.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
            Assert.Equal("Enrolled", r.GetProperty("status").GetString());
            Assert.True(r.GetProperty("enrolledAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));
        });
        var roster1 = items.Single(r => r.GetProperty("studentId").GetInt32() == student1.Id);
        Assert.Equal("报名学员一", roster1.GetProperty("name").GetString());
        Assert.Equal("13877600001", roster1.GetProperty("phone").GetString());
        Assert.DoesNotContain(items, r => r.GetProperty("studentId").GetInt32() == student3.Id);

        // 模板列表：Task 12 增 productName/teacherName/enrolledCount（只数 Enrolled）。
        var listResp = await client.GetAsync($"/api/org/templates?productId={product.Id}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var paged = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, paged.GetProperty("total").GetInt32());
        var listItem = paged.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(TemplateListKeys, listItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(templateId, listItem.GetProperty("id").GetInt32());
        Assert.Equal(product.Id, listItem.GetProperty("productId").GetInt32());
        Assert.Equal("少儿英语固定班", listItem.GetProperty("productName").GetString());
        Assert.Equal(teacher.Id, listItem.GetProperty("teacherId").GetInt32());
        Assert.Equal(teacher.Name, listItem.GetProperty("teacherName").GetString());
        Assert.Equal(12, listItem.GetProperty("capacity").GetInt32());
        Assert.Equal(2, listItem.GetProperty("enrolledCount").GetInt32());

        // 库内：两行 Enrolled + 一行退班历史。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ClassEnrollments.IgnoreQueryFilters()
                .Where(e => e.InstitutionId == institution.Id).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, rows.Count(e => e.Status == EnrollmentStatus.Enrolled));
            Assert.All(rows, e => Assert.Equal(templateId, e.TemplateId));
        }
    }

    [Fact]
    public async Task Duplicate_enroll_returns_409()
    {
        var institution = await SeedInstitutionAsync("en2");
        await SeedStaffAsync(institution.Id, "13977600002");
        var teacher = await SeedStaffAsync(institution.Id, "13977600202", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13977600002");
        var template = await CreateTemplateAsync(client, product.Id, teacher.Id);
        var templateId = template.GetProperty("id").GetInt32();
        var student = await SeedStudentAsync(institution.Id, "重复学员", "13877600011");

        // 首报 201，重报 409（双击与并发收敛同文案）。
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(client, templateId, student.Id)).StatusCode);
        var duplicate = await EnrollAsync(client, templateId, student.Id);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await ProblemDetailAsync(duplicate);
        Assert.Contains("已在该班级", problem.GetProperty("detail").GetString());

        // 409 不落第二行。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ClassEnrollments.IgnoreQueryFilters()
                .CountAsync(e => e.InstitutionId == institution.Id
                    && e.TemplateId == templateId && e.StudentId == student.Id));
        }
    }

    [Fact]
    public async Task Enroll_over_capacity_returns_400()
    {
        var institution = await SeedInstitutionAsync("en3");
        await SeedStaffAsync(institution.Id, "13977600003");
        var teacher = await SeedStaffAsync(institution.Id, "13977600303", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13977600003");
        var template = await CreateTemplateAsync(client, product.Id, teacher.Id, capacity: 1);
        var templateId = template.GetProperty("id").GetInt32();
        var studentA = await SeedStudentAsync(institution.Id, "容量学员A", "13877600021");
        var studentB = await SeedStudentAsync(institution.Id, "容量学员B", "13877600022");
        var inactiveStudent = await SeedStudentAsync(institution.Id, "停用学员", "13877600023", isActive: false);

        // 容量 1：首报占满……
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(client, templateId, studentA.Id)).StatusCode);

        // ……第二名 400，文案精确为"班级已满"，且不落库。
        var full = await EnrollAsync(client, templateId, studentB.Id);
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        var problem = await ProblemDetailAsync(full);
        Assert.Equal("班级已满", problem.GetProperty("detail").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ClassEnrollments.IgnoreQueryFilters()
                .CountAsync(e => e.InstitutionId == institution.Id));
        }

        // 停用学员 400（学员校验先于容量：班已满但文案仍是"学员已停用"）。
        var inactive = await EnrollAsync(client, templateId, inactiveStudent.Id);
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
        var inactiveProblem = await ProblemDetailAsync(inactive);
        Assert.Equal("学员已停用", inactiveProblem.GetProperty("detail").GetString());

        // 退班释放名额：在班数只数 Enrolled，B 补报成功。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/org/templates/{templateId}/enrollments/{studentA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(client, templateId, studentB.Id)).StatusCode);

        // 名额再次占满：曾退班的 A 重报（复用行路径）同样受容量约束 → 400。
        var refull = await EnrollAsync(client, templateId, studentA.Id);
        Assert.Equal(HttpStatusCode.BadRequest, refull.StatusCode);
        Assert.Equal("班级已满", (await ProblemDetailAsync(refull)).GetProperty("detail").GetString());

        // 模板停用后再报（含曾退班的 A）→ 400 "班级已停用"。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/org/templates/{templateId}/deactivate", null)).StatusCode);
        var deactivated = await EnrollAsync(client, templateId, studentA.Id);
        Assert.Equal(HttpStatusCode.BadRequest, deactivated.StatusCode);
        var deactivatedProblem = await ProblemDetailAsync(deactivated);
        Assert.Equal("班级已停用", deactivatedProblem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Withdraw_then_reenroll_reuses_row_as_Enrolled()
    {
        var institution = await SeedInstitutionAsync("en4");
        var admin1 = await SeedStaffAsync(institution.Id, "13977600004");
        var admin2 = await SeedStaffAsync(institution.Id, "13977600404");
        var teacher = await SeedStaffAsync(institution.Id, "13977600405", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client1 = await LoginStaffClientAsync("13977600004");
        var client2 = await LoginStaffClientAsync("13977600404");
        var template = await CreateTemplateAsync(client1, product.Id, teacher.Id);
        var templateId = template.GetProperty("id").GetInt32();
        var student = await SeedStudentAsync(institution.Id, "复用学员", "13877600031");

        // admin1 报名 → 201，拿到报名行 id。
        var enroll1 = await EnrollAsync(client1, templateId, student.Id);
        Assert.Equal(HttpStatusCode.Created, enroll1.StatusCode);
        var rowId = (await enroll1.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();
        var afterFirstEnroll = DateTime.Now;

        // 退班 → 204；行保留、状态 Withdrawn。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client1.DeleteAsync($"/api/org/templates/{templateId}/enrollments/{student.Id}")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ClassEnrollments.IgnoreQueryFilters()
                .Where(e => e.InstitutionId == institution.Id).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(rowId, row.Id);
            Assert.Equal(EnrollmentStatus.Withdrawn, row.Status);
        }

        // admin2 重报 → 201 且复用同一行（同 id）：翻回 Enrolled、
        // EnrolledById/EnrolledAt 更新为本次操作。
        var enroll2 = await EnrollAsync(client2, templateId, student.Id);
        Assert.Equal(HttpStatusCode.Created, enroll2.StatusCode);
        var reuseId = (await enroll2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();
        Assert.Equal(rowId, reuseId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = Assert.Single(await db.ClassEnrollments.IgnoreQueryFilters()
                .Where(e => e.InstitutionId == institution.Id).ToListAsync());
            Assert.Equal(rowId, row.Id);
            Assert.Equal(EnrollmentStatus.Enrolled, row.Status);
            Assert.Equal(admin2.Id, row.EnrolledById);
            Assert.True(row.EnrolledAt >= afterFirstEnroll);
        }

        // 名册重新包含该学员。
        var roster = await (await client1.GetAsync($"/api/org/templates/{templateId}/enrollments"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(student.Id, roster.EnumerateArray().Single().GetProperty("studentId").GetInt32());

        // 退班后（无在班记录）再退班 → 404。
        Assert.Equal(HttpStatusCode.NoContent,
            (await client1.DeleteAsync($"/api/org/templates/{templateId}/enrollments/{student.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client1.DeleteAsync($"/api/org/templates/{templateId}/enrollments/{student.Id}")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 行仍保留（历史），只是 Withdrawn。
            var row = Assert.Single(await db.ClassEnrollments.IgnoreQueryFilters()
                .Where(e => e.InstitutionId == institution.Id).ToListAsync());
            Assert.Equal(EnrollmentStatus.Withdrawn, row.Status);
        }
    }

    [Fact]
    public async Task Template_teacher_can_read_roster_other_teacher_returns_403()
    {
        var institution = await SeedInstitutionAsync("en5");
        var admin = await SeedStaffAsync(institution.Id, "13977600005");
        var teacher1 = await SeedStaffAsync(institution.Id, "13977600505", StaffRole.Teacher);
        var teacher2 = await SeedStaffAsync(institution.Id, "13977600506", StaffRole.Teacher);
        await SeedStaffAsync(institution.Id, "13977600507", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977600508", StaffRole.FrontDesk);
        var product = await SeedProductAsync(institution.Id, "固定班课包");
        var client = await LoginStaffClientAsync("13977600005");
        var template = await CreateTemplateAsync(client, product.Id, teacher1.Id);
        var templateId = template.GetProperty("id").GetInt32();
        var student1 = await SeedStudentAsync(institution.Id, "名册学员一", "13877600041");
        var student2 = await SeedStudentAsync(institution.Id, "名册学员二", "13877600042");
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(client, templateId, student1.Id)).StatusCode);

        // 模板授课教师可读自己的名册。
        var teacher1Client = await LoginStaffClientAsync("13977600505");
        var resp = await teacher1Client.GetAsync($"/api/org/templates/{templateId}/enrollments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var roster = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(student1.Id, roster.EnumerateArray().Single().GetProperty("studentId").GetInt32());

        // 其他教师/销售/前台一律 403。
        var teacher2Client = await LoginStaffClientAsync("13977600506");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacher2Client.GetAsync($"/api/org/templates/{templateId}/enrollments")).StatusCode);
        var salesClient = await LoginStaffClientAsync("13977600507");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.GetAsync($"/api/org/templates/{templateId}/enrollments")).StatusCode);
        var frontDeskClient = await LoginStaffClientAsync("13977600508");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.GetAsync($"/api/org/templates/{templateId}/enrollments")).StatusCode);

        // Admin 任意模板可读；未知模板 id → 404。
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/org/templates/{templateId}/enrollments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/org/templates/999999/enrollments")).StatusCode);

        // 报名/退班写入口是 AdminOnly：教师 403，且不落库。
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacher1Client.PostAsJsonAsync(
                $"/api/org/templates/{templateId}/enrollments", new { studentId = student2.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacher1Client.DeleteAsync(
                $"/api/org/templates/{templateId}/enrollments/{student1.Id}")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ClassEnrollments.IgnoreQueryFilters()
                .CountAsync(e => e.InstitutionId == institution.Id));
        }
    }

    [Fact]
    public async Task Enroll_to_other_institution_template_returns_404()
    {
        var institutionA = await SeedInstitutionAsync("en6");
        var adminA = await SeedStaffAsync(institutionA.Id, "13977600006");
        var teacherA = await SeedStaffAsync(institutionA.Id, "13977600606", StaffRole.Teacher);
        var productA = await SeedProductAsync(institutionA.Id, "A机构固定班");
        var clientA = await LoginStaffClientAsync("13977600006");
        var templateAId = (await CreateTemplateAsync(clientA, productA.Id, teacherA.Id))
            .GetProperty("id").GetInt32();
        var studentA = await SeedStudentAsync(institutionA.Id, "A机构学员", "13877600052");

        var institutionB = await SeedInstitutionAsync("en6x");
        await SeedStaffAsync(institutionB.Id, "13977600007");
        var teacherB = await SeedStaffAsync(institutionB.Id, "13977600607", StaffRole.Teacher);
        var productB = await SeedProductAsync(institutionB.Id, "B机构固定班");
        var clientB = await LoginStaffClientAsync("13977600007");
        var templateBId = (await CreateTemplateAsync(clientB, productB.Id, teacherB.Id))
            .GetProperty("id").GetInt32();
        var studentB = await SeedStudentAsync(institutionB.Id, "B机构学员", "13877600051");

        // B 的管理员把 B 学员报进 A 的模板：A 模板被租户过滤器隐藏 → 404。
        Assert.Equal(HttpStatusCode.NotFound,
            (await EnrollAsync(clientB, templateAId, studentB.Id)).StatusCode);

        // B 的管理员把 A 学员报进 B 的模板：学员不可见 → 404。
        Assert.Equal(HttpStatusCode.NotFound,
            (await EnrollAsync(clientB, templateBId, studentA.Id)).StatusCode);

        // 未知模板/未知学员同态 404。
        Assert.Equal(HttpStatusCode.NotFound,
            (await EnrollAsync(clientB, 999999, studentB.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await EnrollAsync(clientB, templateBId, 999999)).StatusCode);

        // 跨机构读名册/退班同样 404（不泄露存在性）。
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/org/templates/{templateAId}/enrollments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.DeleteAsync($"/api/org/templates/{templateAId}/enrollments/{studentB.Id}")).StatusCode);

        // 本测试两个机构均无报名行：所有被拒请求都未落库（库内其余行属于
        // 同类共享库中其他测试的机构）。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.ClassEnrollments.IgnoreQueryFilters()
                .CountAsync(e => e.InstitutionId == institutionA.Id || e.InstitutionId == institutionB.Id));
        }
    }
}
