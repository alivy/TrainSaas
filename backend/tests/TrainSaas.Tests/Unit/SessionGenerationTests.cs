using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// SessionGenerationService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）。
/// 惰性生成的核心裁定（Review Focus 2/5）：
/// <list type="bullet">
/// <item>窗口钳制：to = Min(to, from+28 天)，28 天内的匹配日全量落库
/// （周二模板：from=2026-11-02 周一 → 11-03/11-10/11-17/11-24 恰 4 节）；</item>
/// <item>幂等：已存在 (TemplateId,StartAt) 的课次跳过，重复执行/重叠窗口
/// 都不产生重复行（Review Focus 2）；</item>
/// <item>跨年：日期迭代基于本地日期，12 月到 1 月自然跨越（Review Focus 5）；</item>
/// <item>停用模板不生成；生成的课次携带模板教师、Status=Scheduled、
/// 归属机构由 SaveChanges 按员工令牌自动填充。</item>
/// <item>R11 并发：两个请求同时首化同一窗口时，(TemplateId,StartAt) 唯一
/// 索引兜底 + DbUpdateException 捕获收敛，无重复行、无异常逃逸。</item>
/// </list>
/// </summary>
public class SessionGenerationTests
{
    /// <summary>当前用户：机构 1 的管理员（StaffId=1）。</summary>
    private readonly FakeCurrentUser _user = new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = 1,
        Role = StaffRole.Admin,
        Phone = "13900000001",
        Name = "管理员",
    };

    private (AppDbContext Db, SessionGenerationService Sessions, SqliteConnection Conn) CreateSessions(
        ICurrentUser? user = null)
    {
        var (db, conn) = TestDb.Create(user ?? _user);
        return (db, new SessionGenerationService(db), conn);
    }

    private static async Task<Staff> SeedTeacherAsync(AppDbContext db, int institutionId = 1)
    {
        var teacher = new Staff
        {
            InstitutionId = institutionId,
            Name = "教师甲",
            Phone = $"1391111{institutionId:00000}",
            PasswordHash = "not-a-real-hash",
            Role = StaffRole.Teacher,
            IsActive = true,
        };
        db.Staff.Add(teacher);
        await db.SaveChangesAsync();
        return teacher;
    }

    private static async Task<ClassTemplate> SeedTemplateAsync(
        AppDbContext db,
        int teacherId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        int institutionId = 1,
        bool isActive = true)
    {
        var template = new ClassTemplate
        {
            InstitutionId = institutionId,
            ProductId = 1,
            DayOfWeek = dayOfWeek,
            StartTime = start,
            EndTime = end,
            TeacherId = teacherId,
            IsActive = isActive,
        };
        db.ClassTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    [Fact]
    public async Task Tuesday_template_generates_exactly_4_sessions_within_28_day_window()
    {
        var (db, sessions, conn) = CreateSessions();
        var teacher = await SeedTeacherAsync(db);
        var template = await SeedTemplateAsync(
            db, teacher.Id, DayOfWeek.Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 30));

        // from=2026-11-02（周一），to=2026-12-31 远超钳制边界：实际生成窗口
        // 是 [11-02, 11-30]，其中的周二只有 4 个。
        await sessions.EnsureSessionsAsync(new DateTime(2026, 11, 2), new DateTime(2026, 12, 31));

        var rows = await db.ClassSessions.AsNoTracking()
            .OrderBy(cs => cs.StartAt)
            .ToListAsync();

        var expected = new[]
        {
            new DateTime(2026, 11, 3, 18, 0, 0),
            new DateTime(2026, 11, 10, 18, 0, 0),
            new DateTime(2026, 11, 17, 18, 0, 0),
            new DateTime(2026, 11, 24, 18, 0, 0),
        };
        Assert.Equal(4, rows.Count);
        Assert.Equal(expected, rows.Select(cs => cs.StartAt).ToArray());
        Assert.All(rows, cs =>
        {
            Assert.Equal(template.Id, cs.TemplateId);
            Assert.Equal(teacher.Id, cs.TeacherId);
            Assert.Equal(cs.StartAt.AddMinutes(90), cs.EndAt);
            Assert.Equal(SessionStatus.Scheduled, cs.Status);
            // InstitutionId 由 SaveChanges 按当前员工令牌自动填充。
            Assert.Equal(1, cs.InstitutionId);
        });
    }

    [Fact]
    public async Task Running_generation_twice_creates_no_duplicates()
    {
        var (db, sessions, conn) = CreateSessions();
        var teacher = await SeedTeacherAsync(db);
        await SeedTemplateAsync(db, teacher.Id, DayOfWeek.Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 30));

        var from = new DateTime(2026, 11, 2);
        var to = new DateTime(2026, 11, 30);

        // 同一窗口重复执行两次……
        await sessions.EnsureSessionsAsync(from, to);
        await sessions.EnsureSessionsAsync(from, to);
        Assert.Equal(4, await db.ClassSessions.CountAsync());

        // ……再跑一个重叠的偏移窗口（11-09..11-30，其中 11-10/11-17/11-24 均已存在）。
        await sessions.EnsureSessionsAsync(new DateTime(2026, 11, 9), to);

        var rows = await db.ClassSessions.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(cs => (cs.TemplateId, cs.StartAt)).Distinct().Count());
    }

    [Fact]
    public async Task Friday_template_generates_across_year_boundary()
    {
        var (db, sessions, conn) = CreateSessions();
        var teacher = await SeedTeacherAsync(db);
        await SeedTemplateAsync(db, teacher.Id, DayOfWeek.Friday, new TimeOnly(10, 0), new TimeOnly(11, 30));

        // from=2026-12-20（周日），to 给到 2027-02-28；钳制为 [12-20, 2027-01-17]，
        // 周五落在 12-25、2027-01-01、01-08、01-15——跨年纯本地日期运算。
        await sessions.EnsureSessionsAsync(new DateTime(2026, 12, 20), new DateTime(2027, 2, 28));

        var rows = await db.ClassSessions.AsNoTracking()
            .OrderBy(cs => cs.StartAt)
            .ToListAsync();

        var expected = new[]
        {
            new DateTime(2026, 12, 25, 10, 0, 0),
            new DateTime(2027, 1, 1, 10, 0, 0),
            new DateTime(2027, 1, 8, 10, 0, 0),
            new DateTime(2027, 1, 15, 10, 0, 0),
        };
        Assert.Equal(expected, rows.Select(cs => cs.StartAt).ToArray());
        Assert.All(rows, cs => Assert.Equal(cs.StartAt.AddMinutes(90), cs.EndAt));
    }

    [Fact]
    public async Task Deactivated_template_generates_nothing()
    {
        var (db, sessions, conn) = CreateSessions();
        var teacher = await SeedTeacherAsync(db);
        await SeedTemplateAsync(
            db, teacher.Id, DayOfWeek.Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 30), isActive: false);

        await sessions.EnsureSessionsAsync(new DateTime(2026, 11, 2), new DateTime(2026, 11, 30));

        Assert.Equal(0, await db.ClassSessions.CountAsync());
    }

    [Fact]
    public async Task Generation_only_sees_current_institution_templates()
    {
        // 机构 2 的模板与机构 1 的模板并存；机构 1 的员工令牌下生成时，
        // 租户过滤器必须把机构 2 的模板挡在外面。
        var (db, sessions, conn) = CreateSessions();
        var ownTeacher = await SeedTeacherAsync(db, institutionId: 1);
        var ownTemplate = await SeedTemplateAsync(
            db, ownTeacher.Id, DayOfWeek.Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 30), institutionId: 1);
        var awayTemplate = await SeedTemplateAsync(
            db, teacherId: 999, DayOfWeek.Wednesday, new TimeOnly(18, 0), new TimeOnly(19, 30), institutionId: 2);

        await sessions.EnsureSessionsAsync(new DateTime(2026, 11, 2), new DateTime(2026, 11, 30));

        var rows = await db.ClassSessions.IgnoreQueryFilters().ToListAsync();
        // 机构 1 的周二模板正常生成 4 节，归属机构 1……
        Assert.Equal(4, rows.Count);
        Assert.All(rows, cs => Assert.Equal(ownTemplate.Id, cs.TemplateId));
        Assert.All(rows, cs => Assert.Equal(1, cs.InstitutionId));
        // ……机构 2 的周三模板一节都不生成。
        Assert.DoesNotContain(rows, cs => cs.TemplateId == awayTemplate.Id);
    }

    /// <summary>
    /// 并发竞态的确定化重放（R11）：挂在"输家"上下文的 SaveChanges 上，在其
    /// 真正提交之前让"赢家"完整跑完同一窗口的生成——即两个请求都完成了
    /// preload、赢家先提交的竞态窗口。只拦第一次 SaveChanges（恢复重试的
    /// 提交不再注入赢家）。HookFired 防止测试因钩子未触发而空转通过。
    /// </summary>
    private sealed class WinnerCommitsDuringLoserSave(Func<Task> onSavingChanges)
        : SaveChangesInterceptor
    {
        private Func<Task>? _onSavingChanges = onSavingChanges;

        public bool HookFired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var hook = _onSavingChanges;
            if (hook is not null)
            {
                _onSavingChanges = null;
                HookFired = true;
                await hook();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static void TryDeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(dbPath + suffix);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Concurrent_generation_yields_no_duplicates()
    {
        // R11：同一临时文件库、两个独立连接的 AppDbContext 同时惰性生成同一
        // 窗口。竞态确定化：输家的 SaveChanges 提交前，赢家已按同一窗口完整
        // 生成并提交。断言：无异常逃逸、恰 4 节课、(TemplateId,StartAt) 无重复。
        var dbPath = Path.Combine(Path.GetTempPath(), $"trainsaas-gen-{Guid.NewGuid():N}.db");
        var dataSource = $"DataSource={dbPath}";
        var user1 = new FakeCurrentUser
        {
            UserType = UserType.Staff, InstitutionId = 1, StaffId = 1,
            Role = StaffRole.Admin, Phone = "13900000001", Name = "管理员一",
        };
        var user2 = new FakeCurrentUser
        {
            UserType = UserType.Staff, InstitutionId = 1, StaffId = 2,
            Role = StaffRole.Admin, Phone = "13900000002", Name = "管理员二",
        };
        try
        {
            var winnerOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(dataSource).Options;
            using (var seed = new AppDbContext(winnerOptions, user1))
            {
                seed.Database.EnsureCreated();
                seed.ClassTemplates.Add(new ClassTemplate
                {
                    InstitutionId = 1,
                    ProductId = 1,
                    DayOfWeek = DayOfWeek.Tuesday,
                    StartTime = new TimeOnly(18, 0),
                    EndTime = new TimeOnly(19, 30),
                    TeacherId = 9,
                    IsActive = true,
                });
                await seed.SaveChangesAsync();
            }

            var from = new DateTime(2026, 11, 2);
            var to = new DateTime(2026, 11, 30);
            using var winnerDb = new AppDbContext(winnerOptions, user1);
            var winner = new SessionGenerationService(winnerDb);

            var interceptor = new WinnerCommitsDuringLoserSave(
                () => winner.EnsureSessionsAsync(from, to));
            using var loserDb = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(dataSource)
                    .AddInterceptors(interceptor)
                    .Options,
                user2);
            var loser = new SessionGenerationService(loserDb);

            // 输家的整窗生成（含与赢家的唯一索引冲突及恢复）不得有异常逃逸。
            await loser.EnsureSessionsAsync(from, to);

            // 钩子确实触发过（否则测试没有走到竞态窗口）。
            Assert.True(interceptor.HookFired);

            // 终态一致：恰好 4 节周二课，无重复键。
            var rows = await winnerDb.ClassSessions.AsNoTracking()
                .IgnoreQueryFilters().ToListAsync();
            Assert.Equal(4, rows.Count);
            Assert.Equal(4, rows.Select(cs => (cs.TemplateId, cs.StartAt)).Distinct().Count());
        }
        finally
        {
            TryDeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public async Task ClassSession_template_and_start_unique_index_rejects_duplicate()
    {
        // R11 数据库兜底：即使绕过服务直插，(TemplateId,StartAt) 唯一索引也
        // 拒绝同一课次的第二行（把并发竞态变成 DbUpdateException，由
        // EnsureSessionsAsync 捕获后收敛，而非静默重复课次）。
        var (db, sessions, conn) = CreateSessions();
        var teacher = await SeedTeacherAsync(db);
        var template = await SeedTemplateAsync(
            db, teacher.Id, DayOfWeek.Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 30));

        db.ClassSessions.Add(new ClassSession
        {
            TemplateId = template.Id,
            TeacherId = teacher.Id,
            StartAt = new DateTime(2026, 11, 3, 18, 0, 0),
            EndAt = new DateTime(2026, 11, 3, 19, 30, 0),
            Status = SessionStatus.Scheduled,
        });
        db.ClassSessions.Add(new ClassSession
        {
            TemplateId = template.Id,
            TeacherId = teacher.Id,
            StartAt = new DateTime(2026, 11, 3, 18, 0, 0),
            EndAt = new DateTime(2026, 11, 3, 19, 30, 0),
            Status = SessionStatus.Scheduled,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
