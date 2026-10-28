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
/// 课时流水路由集成测试（Task 14）：POST /api/org/consumptions/walk-in
/// [AdminOrFrontDesk]、GET /api/org/consumptions [StaffOnly]、
/// POST /api/org/consumptions/{id}/reverse [StaffOnly]。真实登录令牌走真实
/// HTTP，覆盖：前台核销 200 响应形态 {id,remainingSessions}、WalkIn 行
/// ClassSessionId=null 且记录操作人、列表 {total,items} 字段形态与名称解析、
/// 撤销 201 {id} 且包余量回补；教师核销被策略拒绝 403（Teacher 的列表
/// 范围也看不到前台操作的行）。
/// </summary>
public class OrgConsumptionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>列表项的精确字段集（含 null 值键）。</summary>
    private static readonly string[] ItemKeys =
    [
        "classSessionStartAt", "createdAt", "direction", "id", "kind",
        "operatorName", "productName", "reason", "studentId", "studentName",
    ];

    private sealed record Seeded(
        Staff FrontDesk, Staff Teacher, Student Student, Product Product, SessionPack Pack);

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
        var frontDesk = Staff($"139666{seq}0001", StaffRole.FrontDesk);
        var teacher = Staff($"139666{seq}0002", StaffRole.Teacher);
        db.Staff.AddRange(frontDesk, teacher);

        var product = new Product
        {
            InstitutionId = institution.Id,
            Name = "随到随上课包",
            Type = ProductType.Flexible,
            Price = 1500m,
            SessionCount = 10,
        };
        db.Products.Add(product);

        var student = new Student
        {
            InstitutionId = institution.Id,
            Name = "到店学员",
            Phone = $"138666{seq}0001",
            IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var pack = new SessionPack
        {
            InstitutionId = institution.Id,
            OrderId = 9_000 + seq * 10 + 1,
            StudentId = student.Id,
            ProductId = product.Id,
            TotalSessions = 5,
            RemainingSessions = 5,
            Status = PackStatus.Active,
        };
        db.SessionPacks.Add(pack);
        await db.SaveChangesAsync();

        return new Seeded(frontDesk, teacher, student, product, pack);
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
    public async Task FrontDesk_walkin_happy_path_via_api()
    {
        var seeded = await SeedAsync("csm1", 1);
        var client = await LoginStaffClientAsync(seeded.FrontDesk.Phone);

        // 到店核销 → 200 {id, remainingSessions}（核销后该包余量）。
        var resp = await client.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId = seeded.Student.Id, sessionPackId = seeded.Pack.Id });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["id", "remainingSessions"],
            body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, body.GetProperty("remainingSessions").GetInt32());
        var consumptionId = body.GetProperty("id").GetInt32();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Consumptions.IgnoreQueryFilters()
                .SingleAsync(c => c.Id == consumptionId);
            Assert.Equal(seeded.Student.Id, row.StudentId);
            Assert.Equal(seeded.Pack.Id, row.SessionPackId);
            // WalkIn 行不带课次 id；操作人是登录的前台。
            Assert.Null(row.ClassSessionId);
            Assert.Equal(ConsumptionKind.WalkIn, row.Kind);
            Assert.Equal(1, row.Direction);
            Assert.Equal(seeded.FrontDesk.Id, row.OperatorId);
            Assert.Equal(4, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.Pack.Id).Select(p => p.RemainingSessions).SingleAsync());
        }

        // 流水列表 → 200 {total, items}，列表项字段形态与名称解析。
        var listResp = await client.GetAsync(
            $"/api/org/consumptions?studentId={seeded.Student.Id}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["items", "total"],
            list.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(ItemKeys,
            item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(consumptionId, item.GetProperty("id").GetInt32());
        Assert.Equal(seeded.Student.Id, item.GetProperty("studentId").GetInt32());
        Assert.Equal("到店学员", item.GetProperty("studentName").GetString());
        Assert.Equal("随到随上课包", item.GetProperty("productName").GetString());
        Assert.Equal("WalkIn", item.GetProperty("kind").GetString());
        Assert.Equal(1, item.GetProperty("direction").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("reason").ValueKind);
        Assert.Equal($"员工{seeded.FrontDesk.Phone}", item.GetProperty("operatorName").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("classSessionStartAt").ValueKind);

        // 非法 kind 安全解析 → 400（绝不 500）。
        var badKind = await client.GetAsync("/api/org/consumptions?kind=Banana");
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        // M1：数字串 "1"（此前被 Enum.TryParse+IsDefined 误收为 WalkIn）现与
        // 未定义名一样 400——QueryParsers 全站统一口径。
        var numericKind = await client.GetAsync("/api/org/consumptions?kind=1");
        Assert.Equal(HttpStatusCode.BadRequest, numericKind.StatusCode);

        var badFrom = await client.GetAsync("/api/org/consumptions?from=2026/10/01");
        Assert.Equal(HttpStatusCode.BadRequest, badFrom.StatusCode);

        // 撤销本人当天的核销 → 201 {id}；包余量回补，原行不动。
        var reverseResp = await client.PostAsJsonAsync(
            $"/api/org/consumptions/{consumptionId}/reverse", new { reason = " 客户临时有事 " });
        Assert.Equal(HttpStatusCode.Created, reverseResp.StatusCode);
        var reverseBody = await reverseResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id"],
            reverseBody.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        var mirrorId = reverseBody.GetProperty("id").GetInt32();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mirror = await db.Consumptions.IgnoreQueryFilters()
                .SingleAsync(c => c.Id == mirrorId);
            Assert.Equal(-1, mirror.Direction);
            Assert.Equal(ConsumptionKind.Reversal, mirror.Kind);
            Assert.Equal("客户临时有事", mirror.Reason);
            Assert.Equal(consumptionId, mirror.ReversesConsumptionId);
            Assert.Equal(seeded.Pack.Id, mirror.SessionPackId);
            Assert.Equal(seeded.FrontDesk.Id, mirror.OperatorId);
            // 原行 append-only；余量恢复为 5。
            var original = await db.Consumptions.IgnoreQueryFilters()
                .SingleAsync(c => c.Id == consumptionId);
            Assert.Equal(1, original.Direction);
            Assert.Null(original.Reason);
            Assert.Equal(5, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.Pack.Id).Select(p => p.RemainingSessions).SingleAsync());
        }

        // 重复撤销 → 409。
        var again = await client.PostAsJsonAsync(
            $"/api/org/consumptions/{consumptionId}/reverse", new { reason = "再撤一次" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Teacher_walkin_returns_403()
    {
        var seeded = await SeedAsync("csm2", 2);

        // 前台先核销成功，制造一条流水（教师列表范围的对照组）。
        var frontDeskClient = await LoginStaffClientAsync(seeded.FrontDesk.Phone);
        var ok = await frontDeskClient.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId = seeded.Student.Id, sessionPackId = seeded.Pack.Id });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 教师令牌真实有效，但 walk-in 策略 [AdminOrFrontDesk] 拒绝 → 403。
        var teacherClient = await LoginStaffClientAsync(seeded.Teacher.Phone);
        var denied = await teacherClient.PostAsJsonAsync("/api/org/consumptions/walk-in",
            new { studentId = seeded.Student.Id, sessionPackId = seeded.Pack.Id });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // 教师列表（[StaffOnly] 可访问）范围：非本人操作、无课次的行不可见。
        var listResp = await teacherClient.GetAsync("/api/org/consumptions");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetProperty("total").GetInt32());

        // 被拒请求不落新行、不动包：只有前台那一条流水，包只扣了一次。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Consumptions.IgnoreQueryFilters()
                .Where(c => c.StudentId == seeded.Student.Id).ToListAsync());
            Assert.Equal(4, await db.SessionPacks.IgnoreQueryFilters()
                .Where(p => p.Id == seeded.Pack.Id).Select(p => p.RemainingSessions).SingleAsync());
        }
    }
}
