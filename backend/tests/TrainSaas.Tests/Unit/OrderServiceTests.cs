using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// OrderService 单元测试（TestDb 内存 SQLite + FakeCurrentUser），覆盖账务
/// 入口的全部守卫：
/// <list type="bullet">
/// <item>确认：生成 Total=Remaining=产品课时数的课时包；家长账号按手机号
/// 全局唯一（同号第二个订单确认仍只有一行，Review Focus 4）；重复确认
/// 409 ConflictException 且只生成一个包（Review Focus 2）；</item>
/// <item>作废：仅 Active；原因必填 ≤200（Review Focus 3）；作废在同一
/// SaveChanges 内把课时包一并作废；</item>
/// <item>取消：仅 Pending；调用者须是 Admin 或创建人；</item>
/// <item>创建：salesId 缺省=当前员工，显式传入须是本机构在职销售
/// （跨机构/非销售/停用一律 400）；amount ≥0。</item>
/// </list>
/// R9 裁定：业务规则违反断言 BusinessRuleException（HTTP 400），
/// 重复确认/终态作废断言 ConflictException（HTTP 409）。
/// </summary>
public class OrderServiceTests
{
    /// <summary>当前用户：机构 1 的管理员（StaffId=1）。测试内可变（模拟不同调用者）。</summary>
    private readonly FakeCurrentUser _user = new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = 1,
        Role = StaffRole.Admin,
        Phone = "13900000001",
        Name = "管理员",
    };

    private readonly IPasswordHasherService _hasher = new PasswordHasherService();

    private (AppDbContext Db, OrderService Orders, SqliteConnection Conn) CreateOrders()
    {
        var (db, conn) = TestDb.Create(_user);
        return (db, new OrderService(db, _user, _hasher), conn);
    }

    private static async Task<Student> SeedStudentAsync(
        AppDbContext db, string phone, string name = "学员", int institutionId = 1)
    {
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

    private static async Task<Product> SeedProductAsync(
        AppDbContext db, int sessionCount = 12, decimal price = 1200m, int institutionId = 1)
    {
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = "课程包",
            Type = ProductType.Flexible,
            Price = price,
            SessionCount = sessionCount,
            // 未发布产品同样可售给学员：创建订单不做发布状态校验。
            IsPublished = false,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<Staff> SeedStaffAsync(
        AppDbContext db, string phone, StaffRole role, bool isActive = true, int institutionId = 1)
    {
        var staff = new Staff
        {
            InstitutionId = institutionId,
            Name = $"员工{phone}",
            Phone = phone,
            PasswordHash = "not-a-real-hash",
            Role = role,
            IsActive = isActive,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    private static async Task<Order> CreatePendingOrderAsync(
        OrderService orders, Student student, Product product, decimal amount = 1000m) =>
        await orders.CreateAsync(new CreateOrderRequest(student.Id, product.Id, amount, null, null));

    [Fact]
    public async Task Confirm_creates_pack_with_remaining_equal_product_session_count()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110001");
        var product = await SeedProductAsync(db, sessionCount: 12);
        var order = await CreatePendingOrderAsync(orders, student, product);

        await orders.ConfirmAsync(order.Id, staffId: 1);

        var pack = await db.SessionPacks.AsNoTracking().SingleAsync(sp => sp.OrderId == order.Id);
        Assert.Equal(order.Id, pack.OrderId);
        Assert.Equal(student.Id, pack.StudentId);
        Assert.Equal(product.Id, pack.ProductId);
        Assert.Equal(12, pack.TotalSessions);
        Assert.Equal(12, pack.RemainingSessions);
        Assert.Equal(PackStatus.Active, pack.Status);

        var reloaded = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Active, reloaded.Status);
        Assert.Equal(1, reloaded.ConfirmedById);
        Assert.NotNull(reloaded.ConfirmedAt);
    }

    [Fact]
    public async Task Confirm_creates_parent_account_with_initial_password_once()
    {
        var (db, orders, conn) = CreateOrders();
        // 同手机号的两个学员（二孩，Review Focus 4）：各自一张订单，都确认。
        var kid1 = await SeedStudentAsync(db, "13811110002", "大娃");
        var kid2 = await SeedStudentAsync(db, "13811110002", "二娃");
        var product = await SeedProductAsync(db);
        var order1 = await CreatePendingOrderAsync(orders, kid1, product);
        var order2 = await CreatePendingOrderAsync(orders, kid2, product);

        await orders.ConfirmAsync(order1.Id, staffId: 1);
        await orders.ConfirmAsync(order2.Id, staffId: 1);

        // 家长账号按手机号全局唯一：两次确认后仍然只有一行，且初始密码可验证。
        var account = Assert.Single(
            await db.ParentAccounts.AsNoTracking().Where(p => p.Phone == "13811110002").ToListAsync());
        Assert.True(_hasher.Verify(account.PasswordHash, DefaultPassword.Initial));
    }

    [Fact]
    public async Task Confirm_twice_throws_Conflict_and_creates_single_pack()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110003");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);

        await orders.ConfirmAsync(order.Id, staffId: 1);

        // Review Focus 2：双击确认 → 409 订单已处理，不得生成第二个包。
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => orders.ConfirmAsync(order.Id, staffId: 1));
        Assert.Equal("订单已处理", ex.Message);
        Assert.Single(await db.SessionPacks.AsNoTracking()
            .Where(sp => sp.OrderId == order.Id).ToListAsync());
    }

    [Fact]
    public async Task Void_without_reason_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110004");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);
        await orders.ConfirmAsync(order.Id, staffId: 1);

        // Review Focus 3：原因必填——null/空白/超长都拒绝，订单保持 Active。
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.VoidAsync(order.Id, null, null, 1));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.VoidAsync(order.Id, "   ", null, 1));

        var tooLong = new string('废', 201);
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.VoidAsync(order.Id, tooLong, null, 1));
        Assert.Equal(OrderStatus.Active,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task Void_active_order_marks_pack_voided()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110005");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);
        await orders.ConfirmAsync(order.Id, staffId: 1);

        await orders.VoidAsync(order.Id, "家长申请退款", 500.5m, 1);

        var reloaded = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Voided, reloaded.Status);
        Assert.Equal(1, reloaded.VoidedById);
        Assert.NotNull(reloaded.VoidedAt);
        Assert.Equal("家长申请退款", reloaded.VoidReason);
        Assert.Equal(500.5m, reloaded.RefundAmount);

        // 作废在同一 SaveChanges 内级联到课时包。
        var pack = await db.SessionPacks.AsNoTracking().SingleAsync(sp => sp.OrderId == order.Id);
        Assert.Equal(PackStatus.Voided, pack.Status);
    }

    [Fact]
    public async Task Cancel_active_order_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110006");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);
        await orders.ConfirmAsync(order.Id, staffId: 1);

        // 已确认订单只能作废（Admin），不能取消。
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.CancelAsync(order.Id, callerStaffId: 1, role: StaffRole.Admin));
        Assert.Equal(OrderStatus.Active,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task Create_with_salesId_of_other_institution_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13811110007");
        var product = await SeedProductAsync(db);
        // 机构 2 的在职销售：对本机构经租户过滤器不可见 → 400。
        var otherSales = await SeedStaffAsync(db, "13822220001", StaffRole.Sales, institutionId: 2);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.CreateAsync(new CreateOrderRequest(student.Id, product.Id, 1000m, otherSales.Id, null)));
        Assert.Empty(await db.Orders.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_defaults_salesId_to_creator_and_generates_order_no()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330001");
        var product = await SeedProductAsync(db);
        var sales = await SeedStaffAsync(db, "13833330002", StaffRole.Sales);

        var order = await orders.CreateAsync(
            new CreateOrderRequest(student.Id, product.Id, 1000m, null, "家长议价"));
        Assert.Equal(_user.StaffId, order.SalesId);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(_user.InstitutionId, order.InstitutionId);
        Assert.Equal("家长议价", order.Remark);
        Assert.Matches(@"^SO\d{14}$", order.OrderNo);

        // 显式传入本机构在职销售同样合法；两次生成的订单号不同。
        var second = await orders.CreateAsync(
            new CreateOrderRequest(student.Id, product.Id, 2000m, sales.Id, null));
        Assert.Equal(sales.Id, second.SalesId);
        Assert.NotEqual(order.OrderNo, second.OrderNo);
    }

    [Fact]
    public async Task Create_with_negative_amount_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330003");
        var product = await SeedProductAsync(db);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.CreateAsync(new CreateOrderRequest(student.Id, product.Id, -0.01m, null, null)));
        Assert.Empty(await db.Orders.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_with_non_sales_or_inactive_salesId_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330004");
        var product = await SeedProductAsync(db);
        var admin = await SeedStaffAsync(db, "13833330005", StaffRole.Admin);
        var inactive = await SeedStaffAsync(db, "13833330006", StaffRole.Sales, isActive: false);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.CreateAsync(new CreateOrderRequest(student.Id, product.Id, 1000m, admin.Id, null)));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.CreateAsync(new CreateOrderRequest(student.Id, product.Id, 1000m, inactive.Id, null)));
        Assert.Empty(await db.Orders.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_with_cross_tenant_student_or_product_throws_NotFound()
    {
        var (db, orders, conn) = CreateOrders();
        var otherStudent = await SeedStudentAsync(db, "13822220002", institutionId: 2);
        var otherProduct = await SeedProductAsync(db, institutionId: 2);
        var ownStudent = await SeedStudentAsync(db, "13833330007");
        var ownProduct = await SeedProductAsync(db);

        // 租户过滤器下跨机构行与不存在同态：一律 404。
        await Assert.ThrowsAsync<NotFoundException>(
            () => orders.CreateAsync(new CreateOrderRequest(otherStudent.Id, ownProduct.Id, 1000m, null, null)));
        await Assert.ThrowsAsync<NotFoundException>(
            () => orders.CreateAsync(new CreateOrderRequest(ownStudent.Id, otherProduct.Id, 1000m, null, null)));
        await Assert.ThrowsAsync<NotFoundException>(
            () => orders.CreateAsync(new CreateOrderRequest(999999, ownProduct.Id, 1000m, null, null)));
        Assert.Empty(await db.Orders.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Cancel_pending_by_creator_sales_or_admin_succeeds()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330008");
        var product = await SeedProductAsync(db);
        // 订单由销售 2 创建（FakeCurrentUser 可变：模拟不同下单人）。
        _user.StaffId = 2;
        _user.Role = StaffRole.Sales;
        var order = await CreatePendingOrderAsync(orders, student, product);

        // 非创建人的销售不能取消 → 403。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => orders.CancelAsync(order.Id, callerStaffId: 3, role: StaffRole.Sales));

        // 创建人本人（销售）可以取消。
        await orders.CancelAsync(order.Id, callerStaffId: 2, role: StaffRole.Sales);
        Assert.Equal(OrderStatus.Cancelled,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);

        // 管理员（非创建人）同样可以取消另一张待确认订单。
        var other = await CreatePendingOrderAsync(orders, student, product);
        await orders.CancelAsync(other.Id, callerStaffId: 9, role: StaffRole.Admin);
        Assert.Equal(OrderStatus.Cancelled,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == other.Id)).Status);
    }

    [Fact]
    public async Task Void_pending_order_tells_caller_to_cancel()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330009");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);

        // 待确认订单没有账务后果：作废入口引导走取消，而非 409。
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.VoidAsync(order.Id, "误下单", null, 1));
        Assert.Contains("取消", ex.Message);
        Assert.Equal(OrderStatus.Pending,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task Void_on_cancelled_or_voided_order_throws_Conflict()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330010");
        var product = await SeedProductAsync(db);
        var cancelled = await CreatePendingOrderAsync(orders, student, product);
        await orders.CancelAsync(cancelled.Id, callerStaffId: 1, role: StaffRole.Admin);
        var voided = await CreatePendingOrderAsync(orders, student, product);
        await orders.ConfirmAsync(voided.Id, staffId: 1);
        await orders.VoidAsync(voided.Id, "重复作废", null, 1);

        await Assert.ThrowsAsync<ConflictException>(
            () => orders.VoidAsync(cancelled.Id, "再次作废", null, 1));
        await Assert.ThrowsAsync<ConflictException>(
            () => orders.VoidAsync(voided.Id, "再次作废", null, 1));
    }

    [Fact]
    public async Task Void_negative_refund_amount_throws_BusinessRule()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330011");
        var product = await SeedProductAsync(db);
        var order = await CreatePendingOrderAsync(orders, student, product);
        await orders.ConfirmAsync(order.Id, staffId: 1);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => orders.VoidAsync(order.Id, "退款", -0.01m, 1));

        // 合法作废仍可完成（前面的失败没有污染状态）。
        await orders.VoidAsync(order.Id, "退款", 0m, 1);
        Assert.Equal(0m,
            (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).RefundAmount);
    }

    [Fact]
    public async Task Unknown_order_ids_throw_NotFound()
    {
        var (db, orders, conn) = CreateOrders();

        await Assert.ThrowsAsync<NotFoundException>(() => orders.ConfirmAsync(999999, 1));
        await Assert.ThrowsAsync<NotFoundException>(
            () => orders.CancelAsync(999999, 1, StaffRole.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => orders.VoidAsync(999999, "原因", null, 1));
        await Assert.ThrowsAsync<NotFoundException>(() => orders.GetDetailAsync(999999));

        // 列表空态正常返回而非抛错。
        var page = await orders.ListAsync(null, null, 1, 20);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetDetail_aggregates_student_product_and_pack_born_at_confirm()
    {
        var (db, orders, conn) = CreateOrders();
        var student = await SeedStudentAsync(db, "13833330012", "张小学员");
        var product = await SeedProductAsync(db, sessionCount: 16);
        var order = await CreatePendingOrderAsync(orders, student, product);

        // 未确认：pack 为 null，无凭证。
        var pending = await orders.GetDetailAsync(order.Id);
        Assert.Null(pending.Pack);
        Assert.Empty(pending.Vouchers);
        Assert.Equal("张小学员", pending.Student.Name);
        Assert.Equal("课程包", pending.Product.Name);
        Assert.Equal("Flexible", pending.Product.Type);
        Assert.Equal("Pending", pending.Status);

        await orders.ConfirmAsync(order.Id, staffId: 1);
        var active = await orders.GetDetailAsync(order.Id);
        Assert.NotNull(active.Pack);
        Assert.Equal(16, active.Pack!.TotalSessions);
        Assert.Equal(16, active.Pack.RemainingSessions);
        Assert.Equal("Active", active.Pack.Status);
    }

    [Fact]
    public async Task List_filters_by_status_and_studentId_and_pages()
    {
        var (db, orders, conn) = CreateOrders();
        var studentA = await SeedStudentAsync(db, "13833330013", "学员甲");
        var studentB = await SeedStudentAsync(db, "13833330014", "学员乙");
        var product = await SeedProductAsync(db);
        var sales = await SeedStaffAsync(db, "13833330015", StaffRole.Sales);
        var order1 = await orders.CreateAsync(
            new CreateOrderRequest(studentA.Id, product.Id, 1000m, sales.Id, null));
        var order2 = await orders.CreateAsync(
            new CreateOrderRequest(studentA.Id, product.Id, 2000m, sales.Id, null));
        var order3 = await orders.CreateAsync(
            new CreateOrderRequest(studentB.Id, product.Id, 3000m, null, null));
        await orders.ConfirmAsync(order1.Id, staffId: 1);
        await orders.CancelAsync(order3.Id, callerStaffId: 1, role: StaffRole.Admin);

        var all = await orders.ListAsync(null, null, 1, 20);
        Assert.Equal(3, all.Total);

        var active = await orders.ListAsync(OrderStatus.Active, null, 1, 20);
        var item = Assert.Single(active.Items);
        Assert.Equal(order1.Id, item.Id);
        Assert.Equal("学员甲", item.StudentName);
        Assert.Equal("课程包", item.ProductName);
        Assert.Equal(sales.Id, item.SalesId);
        Assert.Equal($"员工{sales.Phone}", item.SalesName);
        Assert.Equal("Active", item.Status);

        Assert.Single((await orders.ListAsync(OrderStatus.Pending, null, 1, 20)).Items);
        Assert.Single((await orders.ListAsync(OrderStatus.Cancelled, null, 1, 20)).Items);
        Assert.Equal(2, (await orders.ListAsync(null, studentA.Id, 1, 20)).Total);
        Assert.Single((await orders.ListAsync(OrderStatus.Pending, studentA.Id, 1, 20)).Items);

        // 分页：pageSize=2 的第 2 页只剩最后一条。
        var page2 = await orders.ListAsync(null, null, 2, 2);
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
    }
}
