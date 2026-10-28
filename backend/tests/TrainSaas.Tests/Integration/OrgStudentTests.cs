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
/// Integration tests for the student management API (Task 7): the parent
/// phone is not unique (two siblings may share it), search filters by
/// name-substring (case-insensitive) and exact phone, the detail endpoint
/// aggregates packs/orders/recent consumptions/enrollments, and access is
/// role-scoped identically for search and detail (Admin/FrontDesk → whole
/// institution, Sales → own assigned students, Teacher → students enrolled
/// in own class templates) with out-of-scope ids answering 404 like
/// cross-tenant ones. Sales creation forces AssignedSalesId to self; PUT
/// permissions differ per role (Admin any field, Sales gender/birthday/notes
/// on own students, FrontDesk notes only).
///
/// ApiFactory runs with SeedDemoData=false, so each test seeds its own
/// institutions/staff/students via factory.Services.CreateScope() and drives
/// every behavior over real HTTP with real tokens. Codes/phones are unique
/// per test because the class fixture shares one database.
/// </summary>
public class OrgStudentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The exact response shape of list items.</summary>
    private static readonly string[] ListItemKeys =
        ["assignedSalesId", "assignedSalesName", "birthday", "createdAt", "gender", "id", "isActive", "name", "phone"];

    /// <summary>The exact response shape of both the 201 create body and detail student.</summary>
    private static readonly string[] StudentKeys =
        ["assignedSalesId", "birthday", "createdAt", "gender", "id", "isActive", "name", "notes", "phone"];

    /// <summary>The top-level keys of the detail aggregate.</summary>
    private static readonly string[] DetailKeys =
        ["enrollments", "orders", "packs", "recentConsumptions", "student"];

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
    /// Seeds a student row straight into the DB (bypassing HTTP). The seeding
    /// scope has no tenant context, so InstitutionId must be set explicitly.
    /// </summary>
    private async Task<Student> SeedStudentAsync(
        int institutionId, string name, string phone, int? assignedSalesId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var student = new Student
        {
            InstitutionId = institutionId,
            Name = name,
            Phone = phone,
            AssignedSalesId = assignedSalesId,
            IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    private async Task<Product> SeedProductAsync(int institutionId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = ProductType.Fixed,
            Price = 1980m,
            SessionCount = 20,
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

    private static async Task<JsonElement> ProblemDetailAsync(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates a student over HTTP as the client's user and returns the 201 body.</summary>
    private static async Task<JsonElement> CreateStudentAsync(HttpClient client, string name, string phone)
    {
        var resp = await client.PostAsJsonAsync("/api/org/students", new { name, phone });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>GETs the paged list and returns its items array.</summary>
    private static async Task<List<JsonElement>> ListItemsAsync(HttpClient client, string query = "")
    {
        var list = await client.GetAsync($"/api/org/students{query}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var paged = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            new[] { "items", "total" },
            paged.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        return paged.GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<JsonElement> GetDetailAsync(HttpClient client, int id)
    {
        var detail = await client.GetAsync($"/api/org/students/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return await detail.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Two_siblings_can_share_one_parent_phone()
    {
        var institution = await SeedInstitutionAsync("os1");
        await SeedStaffAsync(institution.Id, "13977700001", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13977700001");

        // First child: the 201 body is exactly the documented shape and new
        // students are active from the start.
        var first = await client.PostAsJsonAsync("/api/org/students",
            new { name = "张小明", phone = "13822200001", gender = "男", birthday = new DateOnly(2016, 3, 1) });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(StudentKeys, firstBody.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        var firstId = firstBody.GetProperty("id").GetInt32();
        Assert.True(firstId > 0);
        Assert.True(firstBody.GetProperty("isActive").GetBoolean());
        Assert.Equal("张小明", firstBody.GetProperty("name").GetString());
        Assert.Equal("13822200001", firstBody.GetProperty("phone").GetString());
        Assert.Equal("男", firstBody.GetProperty("gender").GetString());
        Assert.Equal("2016-03-01", firstBody.GetProperty("birthday").GetString());
        Assert.Equal(JsonValueKind.Null, firstBody.GetProperty("assignedSalesId").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstBody.GetProperty("notes").ValueKind);
        Assert.True(firstBody.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        // The same phone again — the second child of the same parent: must
        // succeed (no uniqueness on Student.Phone), with a distinct id.
        var second = await client.PostAsJsonAsync("/api/org/students",
            new { name = "张小红", phone = "13822200001", notes = "张小明的妹妹" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondBody.GetProperty("id").GetInt32();
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("13822200001", secondBody.GetProperty("phone").GetString());
        Assert.Equal("张小明的妹妹", secondBody.GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.Null, secondBody.GetProperty("gender").ValueKind);

        // Both rows really persisted with the shared phone.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.Students.IgnoreQueryFilters()
                .CountAsync(s => s.InstitutionId == institution.Id && s.Phone == "13822200001"));
        }
    }

    [Fact]
    public async Task Search_by_phone_returns_both_siblings()
    {
        var institution = await SeedInstitutionAsync("os2");
        await SeedStaffAsync(institution.Id, "13977700002", StaffRole.Admin);
        var client = await LoginStaffClientAsync("13977700002");

        var brother = await CreateStudentAsync(client, "张小明", "13822200002");
        var sister = await CreateStudentAsync(client, "张小红", "13822200002");
        var emma = await CreateStudentAsync(client, "Emma", "13822200003");
        Assert.All(new[] { brother, sister, emma }, b => Assert.True(b.GetProperty("id").GetInt32() > 0));

        // The phone filter returns both siblings (exact match), in id order,
        // with the documented list-item shape.
        var list = await client.GetAsync("/api/org/students?phone=13822200002");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var paged = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, paged.GetProperty("total").GetInt32());
        var items = paged.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            new[] { brother.GetProperty("id").GetInt32(), sister.GetProperty("id").GetInt32() },
            items.Select(i => i.GetProperty("id").GetInt32()));
        foreach (var item in items)
        {
            Assert.Equal(ListItemKeys, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        }

        // An unknown phone finds nobody.
        var none = await client.GetAsync("/api/org/students?phone=13822200999");
        Assert.Empty((await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());

        // q matches a name substring, case-insensitively for ASCII…
        var lower = await client.GetAsync("/api/org/students?q=emma");
        var lowerPaged = await lower.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(emma.GetProperty("id").GetInt32(),
            lowerPaged.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());

        // …also the other way around…
        var upper = await client.GetAsync("/api/org/students?q=EMMA");
        Assert.Equal(emma.GetProperty("id").GetInt32(),
            (await upper.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")
                .EnumerateArray().Single().GetProperty("id").GetInt32());

        // …and matches Chinese substrings for the siblings' surname.
        var surname = await client.GetAsync("/api/org/students?q=张");
        Assert.Equal(2, (await surname.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());

        // q and phone combine.
        var combined = await client.GetAsync("/api/org/students?q=张&phone=13822200002");
        Assert.Equal(2, (await combined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Student_detail_returns_packs_orders_consumptions_enrollments()
    {
        var institution = await SeedInstitutionAsync("os3");
        var admin = await SeedStaffAsync(institution.Id, "13977700003", StaffRole.Admin);
        var teacher = await SeedStaffAsync(institution.Id, "13977700303", StaffRole.Teacher);
        var client = await LoginStaffClientAsync("13977700003");

        var created = await CreateStudentAsync(client, "李雷", "13822200004");
        var studentId = created.GetProperty("id").GetInt32();

        var product = await SeedProductAsync(institution.Id, "少儿英语课包");

        // One row in each related table, seeded straight into the DB so the
        // aggregate is genuinely asserted against real data.
        int orderId, packId, templateId, consumptionId, enrollmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = new Order
            {
                InstitutionId = institution.Id,
                OrderNo = $"OS-{Guid.NewGuid():N}",
                StudentId = studentId,
                ProductId = product.Id,
                Amount = 1980m,
                Status = OrderStatus.Active,
                SalesId = admin.Id,
                CreatedById = admin.Id,
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var pack = new SessionPack
            {
                InstitutionId = institution.Id,
                OrderId = order.Id,
                StudentId = studentId,
                ProductId = product.Id,
                TotalSessions = 20,
                RemainingSessions = 18,
                Status = PackStatus.Active,
            };
            db.SessionPacks.Add(pack);
            await db.SaveChangesAsync();

            var consumption = new Consumption
            {
                InstitutionId = institution.Id,
                StudentId = studentId,
                SessionPackId = pack.Id,
                Kind = ConsumptionKind.RollCall,
                Direction = 1,
                Reason = "定时班点名",
                OperatorId = admin.Id,
            };
            db.Consumptions.Add(consumption);

            var template = new ClassTemplate
            {
                InstitutionId = institution.Id,
                ProductId = product.Id,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 30),
                TeacherId = teacher.Id,
                IsActive = true,
            };
            db.ClassTemplates.Add(template);
            await db.SaveChangesAsync();

            var enrollment = new ClassEnrollment
            {
                InstitutionId = institution.Id,
                TemplateId = template.Id,
                StudentId = studentId,
                EnrolledById = admin.Id,
                Status = EnrollmentStatus.Enrolled,
            };
            db.ClassEnrollments.Add(enrollment);
            await db.SaveChangesAsync();

            orderId = order.Id;
            packId = pack.Id;
            templateId = template.Id;
            consumptionId = consumption.Id;
            enrollmentId = enrollment.Id;
        }

        var body = await GetDetailAsync(client, studentId);
        Assert.Equal(DetailKeys, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n));

        var student = body.GetProperty("student");
        Assert.Equal(StudentKeys, student.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("李雷", student.GetProperty("name").GetString());
        Assert.Equal("13822200004", student.GetProperty("phone").GetString());

        var packItem = body.GetProperty("packs").EnumerateArray().Single();
        Assert.Equal(
            new[] { "packId", "productId", "productName", "remaining", "status", "total" },
            packItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(packId, packItem.GetProperty("packId").GetInt32());
        Assert.Equal(product.Id, packItem.GetProperty("productId").GetInt32());
        Assert.Equal("少儿英语课包", packItem.GetProperty("productName").GetString());
        Assert.Equal(20, packItem.GetProperty("total").GetInt32());
        Assert.Equal(18, packItem.GetProperty("remaining").GetInt32());
        Assert.Equal("Active", packItem.GetProperty("status").GetString());

        var orderItem = body.GetProperty("orders").EnumerateArray().Single();
        Assert.Equal(
            new[] { "amount", "createdAt", "id", "orderNo", "productId", "productName", "status" },
            orderItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(orderId, orderItem.GetProperty("id").GetInt32());
        Assert.StartsWith("OS-", orderItem.GetProperty("orderNo").GetString());
        Assert.Equal(product.Id, orderItem.GetProperty("productId").GetInt32());
        Assert.Equal("少儿英语课包", orderItem.GetProperty("productName").GetString());
        Assert.Equal(1980m, orderItem.GetProperty("amount").GetDecimal());
        Assert.Equal("Active", orderItem.GetProperty("status").GetString());
        Assert.True(orderItem.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        var consumptionItem = body.GetProperty("recentConsumptions").EnumerateArray().Single();
        Assert.Equal(
            new[] { "createdAt", "direction", "id", "kind", "productName" },
            consumptionItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(consumptionId, consumptionItem.GetProperty("id").GetInt32());
        Assert.Equal("RollCall", consumptionItem.GetProperty("kind").GetString());
        Assert.Equal(1, consumptionItem.GetProperty("direction").GetInt32());
        Assert.Equal("少儿英语课包", consumptionItem.GetProperty("productName").GetString());
        Assert.True(consumptionItem.GetProperty("createdAt").GetDateTime() >= DateTime.Now.AddMinutes(-5));

        var enrollmentItem = body.GetProperty("enrollments").EnumerateArray().Single();
        Assert.Equal(
            new[] { "dayOfWeek", "endTime", "startTime", "status", "teacherName", "templateId" },
            enrollmentItem.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(templateId, enrollmentItem.GetProperty("templateId").GetInt32());
        Assert.Equal("Monday", enrollmentItem.GetProperty("dayOfWeek").GetString());
        Assert.Equal("09:00:00", enrollmentItem.GetProperty("startTime").GetString());
        Assert.Equal("10:30:00", enrollmentItem.GetProperty("endTime").GetString());
        Assert.Equal(teacher.Name, enrollmentItem.GetProperty("teacherName").GetString());
        Assert.Equal("Enrolled", enrollmentItem.GetProperty("status").GetString());
        Assert.True(enrollmentId > 0);
        Assert.True(packId > 0);
    }

    [Fact]
    public async Task Staff_of_other_institution_getting_student_detail_returns_404()
    {
        var home = await SeedInstitutionAsync("os4");
        await SeedStaffAsync(home.Id, "13977700004", StaffRole.Admin);
        var away = await SeedInstitutionAsync("os4x");
        await SeedStaffAsync(away.Id, "13977700404", StaffRole.Admin);
        var homeClient = await LoginStaffClientAsync("13977700004");
        var awayClient = await LoginStaffClientAsync("13977700404");

        var created = await CreateStudentAsync(homeClient, "王小明", "13822200005");
        var studentId = created.GetProperty("id").GetInt32();

        // Away staff cannot see the student at all: detail and update are 404
        // (no cross-tenant existence leak), not 403.
        var detail = await awayClient.GetAsync($"/api/org/students/{studentId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        var problem = await ProblemDetailAsync(detail);
        Assert.Contains("学员", problem.GetProperty("detail").GetString());

        var put = await awayClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { notes = "越权" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        // Unknown ids are 404 for the home admin too.
        Assert.Equal(HttpStatusCode.NotFound, (await homeClient.GetAsync("/api/org/students/999999")).StatusCode);

        // And the away staff's list never contains the student.
        var awayList = await awayClient.GetAsync("/api/org/students");
        Assert.Equal(0, (await awayList.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Sales_sees_only_own_assigned_students_in_search()
    {
        var institution = await SeedInstitutionAsync("os5");
        await SeedStaffAsync(institution.Id, "13977700005", StaffRole.Admin);
        var sales1 = await SeedStaffAsync(institution.Id, "13977700505", StaffRole.Sales);
        var sales2 = await SeedStaffAsync(institution.Id, "13977700506", StaffRole.Sales);

        var ofSales1 = await SeedStudentAsync(institution.Id, "销售一名下学员", "13822200006", sales1.Id);
        var ofSales2 = await SeedStudentAsync(institution.Id, "销售二名下学员", "13822200007", sales2.Id);
        var unassigned = await SeedStudentAsync(institution.Id, "无归属学员", "13822200008");

        var sales1Client = await LoginStaffClientAsync("13977700505");
        var items = await ListItemsAsync(sales1Client);
        Assert.Equal(new[] { ofSales1.Id }, items.Select(i => i.GetProperty("id").GetInt32()));
        var only = items.Single();
        Assert.Equal("销售一名下学员", only.GetProperty("name").GetString());
        Assert.Equal(sales1.Id, only.GetProperty("assignedSalesId").GetInt32());
        Assert.Equal(sales1.Name, only.GetProperty("assignedSalesName").GetString());

        // Detail scoping is identical to search: out-of-scope ids (another
        // sales's student, an unassigned one) are 404, own ones are 200.
        Assert.Equal(HttpStatusCode.OK, (await sales1Client.GetAsync($"/api/org/students/{ofSales1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sales1Client.GetAsync($"/api/org/students/{ofSales2.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sales1Client.GetAsync($"/api/org/students/{unassigned.Id}")).StatusCode);

        var sales2Client = await LoginStaffClientAsync("13977700506");
        Assert.Equal(new[] { ofSales2.Id },
            (await ListItemsAsync(sales2Client)).Select(i => i.GetProperty("id").GetInt32()));

        // Unassigned students show a null sales in the list…
        var adminClient = await LoginStaffClientAsync("13977700005");
        var allItems = await ListItemsAsync(adminClient);
        Assert.Equal(3, allItems.Count);
        var unassignedItem = allItems.Single(i => i.GetProperty("id").GetInt32() == unassigned.Id);
        Assert.Equal(JsonValueKind.Null, unassignedItem.GetProperty("assignedSalesId").ValueKind);
        Assert.Equal(JsonValueKind.Null, unassignedItem.GetProperty("assignedSalesName").ValueKind);

        // FrontDesk sees the whole institution, exactly like Admin: the list
        // shows all three students…
        await SeedStaffAsync(institution.Id, "13977700507", StaffRole.FrontDesk);
        var frontDeskClient = await LoginStaffClientAsync("13977700507");
        Assert.Equal(3, (await ListItemsAsync(frontDeskClient)).Count);

        // …and every one of them is addressable in detail, including the
        // unassigned student that Sales got a 404 for.
        foreach (var student in new[] { ofSales1, ofSales2, unassigned })
        {
            Assert.Equal(HttpStatusCode.OK,
                (await frontDeskClient.GetAsync($"/api/org/students/{student.Id}")).StatusCode);
        }
    }

    [Fact]
    public async Task Teacher_sees_only_students_enrolled_in_own_classes()
    {
        var institution = await SeedInstitutionAsync("os6");
        var admin = await SeedStaffAsync(institution.Id, "13977700006", StaffRole.Admin);
        var teacher1 = await SeedStaffAsync(institution.Id, "13977700606", StaffRole.Teacher);
        var teacher2 = await SeedStaffAsync(institution.Id, "13977700607", StaffRole.Teacher);
        var product = await SeedProductAsync(institution.Id, "固定班课包");

        var ownStudent = await SeedStudentAsync(institution.Id, "教师一班学员", "13822200009");
        var otherStudent = await SeedStudentAsync(institution.Id, "教师二班学员", "13822200010");
        var withdrawnStudent = await SeedStudentAsync(institution.Id, "退班学员", "13822200011");
        var stranger = await SeedStudentAsync(institution.Id, "未报班学员", "13822200012");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template1 = new ClassTemplate
            {
                InstitutionId = institution.Id,
                ProductId = product.Id,
                DayOfWeek = DayOfWeek.Saturday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 30),
                TeacherId = teacher1.Id,
                IsActive = true,
            };
            var template2 = new ClassTemplate
            {
                InstitutionId = institution.Id,
                ProductId = product.Id,
                DayOfWeek = DayOfWeek.Sunday,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(15, 30),
                TeacherId = teacher2.Id,
                IsActive = true,
            };
            db.ClassTemplates.AddRange(template1, template2);
            await db.SaveChangesAsync();

            var seedEnrollment = (ClassTemplate template, int studentId, EnrollmentStatus status) =>
                db.ClassEnrollments.Add(new ClassEnrollment
                {
                    InstitutionId = institution.Id,
                    TemplateId = template.Id,
                    StudentId = studentId,
                    EnrolledById = admin.Id,
                    Status = status,
                });
            seedEnrollment(template1, ownStudent.Id, EnrollmentStatus.Enrolled);
            seedEnrollment(template2, otherStudent.Id, EnrollmentStatus.Enrolled);
            // Enrolled into teacher1's template but withdrawn: must NOT count.
            seedEnrollment(template1, withdrawnStudent.Id, EnrollmentStatus.Withdrawn);
            await db.SaveChangesAsync();
        }

        // Teacher1 sees only the student enrolled (status Enrolled) in a
        // template they teach.
        var teacher1Client = await LoginStaffClientAsync("13977700606");
        Assert.Equal(new[] { ownStudent.Id },
            (await ListItemsAsync(teacher1Client)).Select(i => i.GetProperty("id").GetInt32()));

        // Detail scoping is identical: own-class students are 200, everything
        // else is 404 without leaking existence.
        Assert.Equal(HttpStatusCode.OK, (await teacher1Client.GetAsync($"/api/org/students/{ownStudent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await teacher1Client.GetAsync($"/api/org/students/{otherStudent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await teacher1Client.GetAsync($"/api/org/students/{withdrawnStudent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await teacher1Client.GetAsync($"/api/org/students/{stranger.Id}")).StatusCode);

        // Teacher2 is symmetric.
        var teacher2Client = await LoginStaffClientAsync("13977700607");
        Assert.Equal(new[] { otherStudent.Id },
            (await ListItemsAsync(teacher2Client)).Select(i => i.GetProperty("id").GetInt32()));

        // Admin still sees all four.
        var adminClient = await LoginStaffClientAsync("13977700006");
        Assert.Equal(4, (await ListItemsAsync(adminClient)).Count);
    }

    [Fact]
    public async Task Sales_creating_student_auto_assigns_self()
    {
        var institution = await SeedInstitutionAsync("os7");
        await SeedStaffAsync(institution.Id, "13977700007", StaffRole.Admin);
        var sales = await SeedStaffAsync(institution.Id, "13977700707", StaffRole.Sales);
        var otherSales = await SeedStaffAsync(institution.Id, "13977700708", StaffRole.Sales);
        var salesClient = await LoginStaffClientAsync("13977700707");

        // Even when another sales's id is supplied, Sales creation forces self.
        var createResp = await salesClient.PostAsJsonAsync("/api/org/students",
            new { name = "销售自建学员", phone = "13822200013", assignedSalesId = otherSales.Id });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sales.Id, created.GetProperty("assignedSalesId").GetInt32());

        // Omitting the field changes nothing.
        var second = await CreateStudentAsync(salesClient, "销售自建学员二", "13822200014");
        Assert.Equal(sales.Id, second.GetProperty("assignedSalesId").GetInt32());

        // Both rows really carry the sales's own id.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Students.IgnoreQueryFilters()
                .Where(s => s.InstitutionId == institution.Id
                    && (s.Name == "销售自建学员" || s.Name == "销售自建学员二"))
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(sales.Id, row.AssignedSalesId));
        }
    }

    [Fact]
    public async Task Student_create_validation()
    {
        var institution = await SeedInstitutionAsync("os8");
        await SeedStaffAsync(institution.Id, "13977700008", StaffRole.Admin);
        var activeSales = await SeedStaffAsync(institution.Id, "13977700808", StaffRole.Sales);
        var inactiveSales = await SeedStaffAsync(institution.Id, "13977700809", StaffRole.Sales);
        var teacher = await SeedStaffAsync(institution.Id, "13977700810", StaffRole.Teacher);
        await SeedStaffAsync(institution.Id, "13977700812", StaffRole.FrontDesk);
        var otherInstitution = await SeedInstitutionAsync("os8x");
        var awaySales = await SeedStaffAsync(otherInstitution.Id, "13977700811", StaffRole.Sales);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Id == inactiveSales.Id);
            row.IsActive = false;
            await db.SaveChangesAsync();
        }

        var client = await LoginStaffClientAsync("13977700008");
        var url = "/api/org/students";

        // Name: required…
        var noName = await client.PostAsJsonAsync(url, new { name = "", phone = "13822200015" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        var problem = await ProblemDetailAsync(noName);
        Assert.Contains("姓名", problem.GetProperty("detail").GetString());

        // …and at most 50 characters.
        var longName = await client.PostAsJsonAsync(url,
            new { name = new string('名', 51), phone = "13822200015" });
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);

        // Phone: required…
        var noPhone = await client.PostAsJsonAsync(url, new { name = "无手机号学员", phone = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
        problem = await ProblemDetailAsync(noPhone);
        Assert.Contains("手机号", problem.GetProperty("detail").GetString());

        // …and exactly ^1\d{10}$.
        foreach (var badPhone in new[] { "12345", "23822200015", "138222000153", "+8613822200015" })
        {
            var bad = await client.PostAsJsonAsync(url, new { name = "坏手机号学员", phone = badPhone });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            problem = await ProblemDetailAsync(bad);
            Assert.Contains("手机号", problem.GetProperty("detail").GetString());
        }

        // assignedSalesId, when supplied by Admin/FrontDesk, must be an active
        // Sales of this institution: not a teacher…
        var notSales = await client.PostAsJsonAsync(url,
            new { name = "指定教师学员", phone = "13822200016", assignedSalesId = teacher.Id });
        Assert.Equal(HttpStatusCode.BadRequest, notSales.StatusCode);
        problem = await ProblemDetailAsync(notSales);
        Assert.Contains("销售", problem.GetProperty("detail").GetString());

        // …not an inactive sales…
        var inactive = await client.PostAsJsonAsync(url,
            new { name = "指定停用销售学员", phone = "13822200017", assignedSalesId = inactiveSales.Id });
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);

        // …and not a sales of another institution (tenant filter hides it).
        var away = await client.PostAsJsonAsync(url,
            new { name = "指定外机构销售学员", phone = "13822200018", assignedSalesId = awaySales.Id });
        Assert.Equal(HttpStatusCode.BadRequest, away.StatusCode);

        // No rejected payload ever reached the database.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Students.IgnoreQueryFilters().AnyAsync(s =>
                s.Name == "坏手机号学员" || s.Name == "指定教师学员"
                || s.Name == "指定停用销售学员" || s.Name == "指定外机构销售学员"
                || s.Name == "无手机号学员"));
        }

        // Valid admin create with an explicit sales assignment.
        var assigned = await client.PostAsJsonAsync(url,
            new { name = "指定销售学员", phone = "13822200019", assignedSalesId = activeSales.Id });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var assignedBody = await assigned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(activeSales.Id, assignedBody.GetProperty("assignedSalesId").GetInt32());

        // Admin/FrontDesk may also leave the assignment empty.
        var noAssignment = await client.PostAsJsonAsync(url, new { name = "无归属新学员", phone = "13822200020" });
        Assert.Equal(HttpStatusCode.Created, noAssignment.StatusCode);
        Assert.Equal(JsonValueKind.Null,
            (await noAssignment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assignedSalesId").ValueKind);

        var frontDeskClient = await LoginStaffClientAsync("13977700812");
        var frontDeskCreate = await frontDeskClient.PostAsJsonAsync(url,
            new { name = "前台建学员", phone = "13822200021" });
        Assert.Equal(HttpStatusCode.Created, frontDeskCreate.StatusCode);

        // Teachers are not allowed to create students at all.
        var teacherClient = await LoginStaffClientAsync("13977700810");
        var denied = await teacherClient.PostAsJsonAsync(url,
            new { name = "教师建学员", phone = "13822200022" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Student_update_follows_role_rules()
    {
        var institution = await SeedInstitutionAsync("os9");
        var admin = await SeedStaffAsync(institution.Id, "13977700009", StaffRole.Admin);
        var sales = await SeedStaffAsync(institution.Id, "13977700909", StaffRole.Sales);
        await SeedStaffAsync(institution.Id, "13977700910", StaffRole.FrontDesk);
        await SeedStaffAsync(institution.Id, "13977700911", StaffRole.Teacher);
        var otherSales = await SeedStaffAsync(institution.Id, "13977700912", StaffRole.Sales);

        var adminClient = await LoginStaffClientAsync("13977700009");
        var salesClient = await LoginStaffClientAsync("13977700909");
        var frontDeskClient = await LoginStaffClientAsync("13977700910");
        var teacherClient = await LoginStaffClientAsync("13977700911");

        // Sales creates their own student (auto-assigned).
        var created = await CreateStudentAsync(salesClient, "更新学员", "13822200023");
        var studentId = created.GetProperty("id").GetInt32();

        // Sales may update gender/birthday/notes on their own student…
        var put = await salesClient.PutAsJsonAsync($"/api/org/students/{studentId}",
            new { gender = "女", birthday = new DateOnly(2017, 6, 1), notes = "销售备注" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var afterSales = await GetDetailAsync(salesClient, studentId);
        var afterStudent = afterSales.GetProperty("student");
        Assert.Equal("女", afterStudent.GetProperty("gender").GetString());
        Assert.Equal("2017-06-01", afterStudent.GetProperty("birthday").GetString());
        Assert.Equal("销售备注", afterStudent.GetProperty("notes").GetString());
        Assert.Equal("更新学员", afterStudent.GetProperty("name").GetString());
        Assert.Equal("13822200023", afterStudent.GetProperty("phone").GetString());
        Assert.Equal(sales.Id, afterStudent.GetProperty("assignedSalesId").GetInt32());

        // …but providing name/phone/assignedSalesId is 403 and changes nothing.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { name = "销售改名" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { phone = "13822200024" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { assignedSalesId = otherSales.Id })).StatusCode);
        afterStudent = (await GetDetailAsync(salesClient, studentId)).GetProperty("student");
        Assert.Equal("更新学员", afterStudent.GetProperty("name").GetString());
        Assert.Equal("13822200023", afterStudent.GetProperty("phone").GetString());
        Assert.Equal(sales.Id, afterStudent.GetProperty("assignedSalesId").GetInt32());

        // Sales cannot touch a student not assigned to them: 404, like
        // search/detail scoping (no existence leak).
        var unassigned = await SeedStudentAsync(institution.Id, "无归属学员", "13822200025");
        Assert.Equal(HttpStatusCode.NotFound,
            (await salesClient.PutAsJsonAsync($"/api/org/students/{unassigned.Id}", new { notes = "x" })).StatusCode);

        // FrontDesk may only update notes.
        Assert.Equal(HttpStatusCode.NoContent,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { notes = "前台备注" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { name = "前台改名" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { phone = "13822200026" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { gender = "男" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { birthday = new DateOnly(2017, 6, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await frontDeskClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { assignedSalesId = otherSales.Id })).StatusCode);
        afterStudent = (await GetDetailAsync(adminClient, studentId)).GetProperty("student");
        Assert.Equal("前台备注", afterStudent.GetProperty("notes").GetString());
        Assert.Equal("更新学员", afterStudent.GetProperty("name").GetString());

        // Teachers have no write access to students at all (policy level).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacherClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { notes = "教师备注" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacherClient.PostAsJsonAsync("/api/org/students", new { name = "教师建学员", phone = "13822200027" })).StatusCode);

        // Admin may update any field with create-grade validation…
        var adminPut = await adminClient.PutAsJsonAsync($"/api/org/students/{studentId}",
            new
            {
                name = "管理员改名",
                phone = "13822200028",
                gender = "男",
                birthday = new DateOnly(2016, 1, 2),
                assignedSalesId = otherSales.Id,
                notes = "管理员备注",
            });
        Assert.Equal(HttpStatusCode.NoContent, adminPut.StatusCode);

        // …and invalid values are rejected with 400.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { phone = "12345" })).StatusCode);
        // An Admin-role staff is not a Sales, so it cannot be assigned.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PutAsJsonAsync($"/api/org/students/{studentId}", new { assignedSalesId = admin.Id })).StatusCode);

        // Final state is visible through search (list + detail).
        var item = (await ListItemsAsync(adminClient, "?phone=13822200028")).Single();
        Assert.Equal(studentId, item.GetProperty("id").GetInt32());
        Assert.Equal("管理员改名", item.GetProperty("name").GetString());
        Assert.Equal(otherSales.Id, item.GetProperty("assignedSalesId").GetInt32());
        Assert.Equal(otherSales.Name, item.GetProperty("assignedSalesName").GetString());
        var finalStudent = (await GetDetailAsync(adminClient, studentId)).GetProperty("student");
        Assert.Equal("管理员备注", finalStudent.GetProperty("notes").GetString());
        Assert.Equal("男", finalStudent.GetProperty("gender").GetString());
        Assert.Equal("2016-01-02", finalStudent.GetProperty("birthday").GetString());
    }
}
