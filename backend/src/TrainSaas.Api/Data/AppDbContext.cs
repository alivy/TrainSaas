using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Data;

/// <summary>
/// The application DbContext.
///
/// Tenant isolation: every <see cref="ITenanted"/> entity gets a global query
/// filter <c>e =&gt; e.InstitutionId == _currentUser.InstitutionId</c>, so all
/// LINQ queries (including lookups by id) only ever see the current
/// institution's rows; anonymous/parent users (null institution) see none.
/// <see cref="SaveChanges(bool)"/> additionally auto-fills the institution of
/// newly added tenanted rows from the current user.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options)
{
    private readonly ICurrentUser _currentUser = currentUser;

    public DbSet<Institution> Institutions => Set<Institution>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<ParentAccount> ParentAccounts => Set<ParentAccount>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<SessionPack> SessionPacks => Set<SessionPack>();
    public DbSet<ClassTemplate> ClassTemplates => Set<ClassTemplate>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<ClassEnrollment> ClassEnrollments => Set<ClassEnrollment>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<Consumption> Consumptions => Set<Consumption>();
    public DbSet<PurchaseIntent> PurchaseIntents => Set<PurchaseIntent>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        FillInstitutionIds();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        FillInstitutionIds();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ApplyTenantQueryFilters(modelBuilder);
        ConfigureIndexesAndRelationships(modelBuilder);
    }

    /// <summary>
    /// Reflects over all entity types and attaches the tenant query filter to
    /// every <see cref="ITenanted"/> one. The filter body is built exactly like
    /// the C# lambda <c>e =&gt; e.InstitutionId == _currentUser.InstitutionId</c>:
    /// the field access is rooted at this context instance, so EF Core
    /// parameterizes it and re-evaluates it for every query.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenanted).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            // Same shape the compiler emits for e => e.InstitutionId == _currentUser.InstitutionId:
            // the int operand is lifted to int? so a null institution (anonymous/parent)
            // compares as "no rows" instead of throwing.
            var body = Expression.Equal(
                Expression.Convert(Expression.Property(parameter, nameof(ITenanted.InstitutionId)), typeof(int?)),
                Expression.Property(
                    Expression.Field(Expression.Constant(this), nameof(_currentUser)),
                    nameof(ICurrentUser.InstitutionId)));
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    private static void ConfigureIndexesAndRelationships(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Institution>()
            .HasIndex(i => i.Code)
            .IsUnique();

        // Staff phones are login accounts and must be globally unique (a phone
        // cannot be staff of two institutions).
        modelBuilder.Entity<Staff>()
            .HasIndex(s => s.Phone)
            .IsUnique();

        modelBuilder.Entity<ParentAccount>()
            .HasIndex(p => p.Phone)
            .IsUnique();

        modelBuilder.Entity<Order>()
            .HasIndex(o => o.OrderNo)
            .IsUnique();

        modelBuilder.Entity<Attendance>()
            .HasIndex(a => new { a.ClassSessionId, a.StudentId })
            .IsUnique();

        modelBuilder.Entity<Student>()
            .HasIndex(s => s.Phone);

        modelBuilder.Entity<Student>()
            .HasIndex(s => s.InstitutionId);

        modelBuilder.Entity<SessionPack>()
            .HasIndex(sp => new { sp.StudentId, sp.ProductId, sp.Status });

        // 订单确认只产出一个课时包：唯一索引是并发双确认的数据库兜底
        // （与 OrderService 的 CAS 状态翻转配合，把竞态从"静默双包"
        // 变成唯一的 DbUpdateException → 409）。
        modelBuilder.Entity<SessionPack>()
            .HasIndex(sp => sp.OrderId)
            .IsUnique();

        modelBuilder.Entity<Consumption>()
            .HasIndex(c => new { c.InstitutionId, c.StudentId });

        // 一条扣减流水至多被冲正一次：ReversesConsumptionId 唯一索引是并发
        // 双撤销的数据库兜底（SQLite 把 NULL 视为互异，多条 NULL 的扣减行
        // 不受影响）；输家的插入被变成 DbUpdateException，由
        // ConsumptionService.ReverseAsync 捕获重查后收敛为 409，而非静默
        // 双回补课时。
        modelBuilder.Entity<Consumption>()
            .HasIndex(c => c.ReversesConsumptionId)
            .IsUnique();

        // (TemplateId, StartAt) 唯一索引是惰性生成的并发兜底：两个请求同时
        // 首化同一窗口时，输家的插入被变成 DbUpdateException，由
        // SessionGenerationService 捕获后重查收敛（而非静默重复课次）。
        modelBuilder.Entity<ClassSession>()
            .HasIndex(cs => new { cs.TemplateId, cs.StartAt })
            .IsUnique();

        modelBuilder.Entity<ClassSession>()
            .HasIndex(cs => new { cs.TeacherId, cs.StartAt });

        // 报名行复用语义：同一 (TemplateId,StudentId) 至多一行（曾退班后
        // 重报复用原行翻回 Enrolled，服务层保证），唯一索引是并发双报的
        // 数据库兜底：输家的插入被变成 DbUpdateException，由
        // EnrollmentService 捕获重读后收敛为 409，而非静默双行。
        modelBuilder.Entity<ClassEnrollment>()
            .HasIndex(e => new { e.TemplateId, e.StudentId })
            .IsUnique();

        modelBuilder.Entity<Order>()
            .HasMany(o => o.Vouchers)
            .WithOne()
            .HasForeignKey(v => v.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Auto-assigns the current user's institution to newly added tenanted
    /// entities whose InstitutionId is still 0. Adding such an entity without
    /// any tenant context (anonymous/platform) is a bug, not silent data loss,
    /// so it throws <see cref="ForbiddenActionException"/>.
    /// </summary>
    private void FillInstitutionIds()
    {
        foreach (var entry in ChangeTracker.Entries<ITenanted>())
        {
            if (entry.State != EntityState.Added || entry.Entity.InstitutionId != 0)
            {
                continue;
            }

            if (_currentUser.InstitutionId is not int institutionId)
            {
                throw new ForbiddenActionException("无租户上下文");
            }

            entry.Entity.InstitutionId = institutionId;
        }
    }
}
