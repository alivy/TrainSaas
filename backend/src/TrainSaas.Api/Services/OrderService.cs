using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 订单服务（账务入口）。状态机：Pending →（确认）Active →（作废）Voided；
/// Pending →（取消）Cancelled。三个状态翻转都是"前置校验 + 显式事务内的
/// CAS 翻转 + 同事务落库"：ExecuteUpdate 带 WHERE Status=前态 直接对数据库
/// 求值（不经过变更跟踪器），并发对手抢先处理时 affected==0 → 回滚 →
/// 409 "订单已处理"；SessionPacks.OrderId 唯一索引再把 CAS 之外的插入
/// 竞态兜成 DbUpdateException → 409。确认/作废的后续写入（课时包、家长
/// 账号、包作废）与翻转在同一显式事务内一次 SaveChanges 提交。
///
/// 业务规则：
/// <list type="bullet">
/// <item>创建：amount ≥0（议价金额可与产品售价不同）；studentId/productId
/// 须是本机构可见行（租户过滤器，跨机构/未知一律 404）；产品不校验发布
/// 状态（未发布产品仍可售给已有学员）；salesId 缺省=当前员工的 staffId，
/// 显式传入须是本机构在职销售，否则 400（跨机构 id 被租户过滤器隐藏，
/// 同样 400）；订单号 SO+yyyyMMdd+6 位随机数，全局唯一索引兜底。</item>
/// <item>确认（AdminOnly 入口）：仅 Pending，重复/并发确认 409 "订单已处理"
/// （Review Focus 2，双击与竞态同文案）；生成 SessionPack{Total=Remaining=
/// 产品课时数}；若学员手机号尚无家长账号则以初始密码创建（Review Focus 4：
/// 家长账号按手机号全局唯一，第二个同号订单确认不得重复建号）。</item>
/// <item>取消：仅 Pending（否则 400）；调用者须是 Admin 或订单创建人
/// 本人（否则 403）；课时包尚未诞生，无级联。</item>
/// <item>作废（AdminOnly 入口）：仅 Active（Pending 引导走取消 400；
/// Cancelled/Voided 409 "订单已处理"）；原因必填 ≤200（Review Focus 3）；
/// 退款金额可空但 ≥0；关联课时包在同一事务内一并作废。</item>
/// </list>
///
/// 租户过滤的例外（本任务仅两处，见裁定）：ParentAccount 非租户实体，
/// 其查询天然全局（按手机号跨机构共用账号）；订单号唯一性检查须避开
/// 跨机构碰撞（全局唯一索引），故显式 <c>IgnoreQueryFilters</c>。
/// 其余所有读写一律走租户过滤器：未知/跨机构订单 id 与不存在同态 404
/// （Review Focus 1），不泄露存在性。ExecuteUpdate 与普通查询同样经过
/// 租户过滤器，批量翻转不会越过机构边界。
/// </summary>
public class OrderService(AppDbContext db, ICurrentUser currentUser, IPasswordHasherService passwordHasher)
{
    private const int MaxVoidReasonLength = 200;

    /// <summary>订单号唯一性冲突的最大尝试次数：每次换一个随机数重试，超过后 409。</summary>
    private const int OrderNoMaxAttempts = 3;

    /// <summary>
    /// POST /api/org/orders：创建 Pending 订单。salesId 缺省=当前员工；
    /// 显式传入时必须是本机构在职销售（跨机构 id 被租户过滤器隐藏 → 400）。
    /// 归属机构由 SaveChanges 按当前员工令牌自动填充。
    /// </summary>
    public async Task<Order> CreateAsync(CreateOrderRequest request)
    {
        if (request.Amount < 0)
        {
            throw new BusinessRuleException("订单金额不能为负数");
        }

        // 租户过滤的解析：跨机构/未知一律 404（与不存在同态）。
        _ = await db.Students.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == request.StudentId)
            ?? throw new NotFoundException("学员不存在");
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == request.ProductId)
            ?? throw new NotFoundException("产品不存在");

        int? salesId;
        if (request.SalesId is int explicitSalesId)
        {
            // 显式成交销售：本机构（租户过滤器）在职销售，否则 400。
            var assignable = await db.Staff.AsNoTracking().AnyAsync(s =>
                s.Id == explicitSalesId && s.Role == StaffRole.Sales && s.IsActive);
            if (!assignable)
            {
                throw new BusinessRuleException("成交销售不存在、已停用或不是销售角色");
            }

            salesId = explicitSalesId;
        }
        else
        {
            // 缺省成交销售 = 下单员工本人。
            salesId = RequireStaffId();
        }

        var order = new Order
        {
            OrderNo = await GenerateOrderNoAsync(),
            StudentId = request.StudentId,
            ProductId = product.Id,
            Amount = request.Amount,
            SalesId = salesId,
            Status = OrderStatus.Pending,
            CreatedById = RequireStaffId(),
            Remark = request.Remark,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌的机构自动填充。
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>
    /// POST /api/org/orders/{id}/confirm（AdminOnly）。仅 Pending，否则
    /// 409 "订单已处理"（双击幂等，Review Focus 2）。状态翻转用 CAS
    /// （ExecuteUpdate 带 WHERE Status=Pending）：并发双确认只有一个赢家，
    /// 落败方 affected==0 → 回滚 → 409，不会静默生成第二个包。随后在同一
    /// 显式事务内：创建 SessionPack{Total=Remaining=product.SessionCount,
    /// Status=Active}；学员手机号若无家长账号则创建（初始密码 123456）——
    /// SessionPacks.OrderId 唯一索引是 CAS 之外的数据库兜底，冲突的
    /// SaveChanges 转为 409（Review Focus 4：家长账号按手机号全局唯一）。
    /// </summary>
    public async Task ConfirmAsync(int orderId, int staffId)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId)
            ?? throw new NotFoundException("订单不存在");

        if (order.Status != OrderStatus.Pending)
        {
            throw new ConflictException("订单已处理");
        }

        // 课时包的课时数取自产品当前定义；学员手机号决定家长账号。
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == order.ProductId)
            ?? throw new NotFoundException("产品不存在");
        var student = await db.Students.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == order.StudentId)
            ?? throw new NotFoundException("学员不存在");

        var confirmedAt = DateTime.Now;
        await using var tx = await db.Database.BeginTransactionAsync();

        // CAS：仅当订单仍为 Pending 才翻转（ExecuteUpdate 不经过变更跟踪器，
        // 直接对数据库求值——两个并发作用域只有一个能改到这一行）。
        var affected = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Active)
                .SetProperty(o => o.ConfirmedById, staffId)
                .SetProperty(o => o.ConfirmedAt, confirmedAt));

        if (affected == 0)
        {
            // 并发对手已抢先处理：回滚并 409（与顺序双击同文案）。
            await tx.RollbackAsync();
            throw new ConflictException("订单已处理");
        }

        // 把 CAS 结果同步回变更跟踪器（ExecuteUpdate 不经过跟踪器）：
        // 重载后的实体既让同上下文的后续读/校验看到翻转后的状态，又保持
        // Unchanged，使 SaveChanges 不再重复发出无守卫的订单 UPDATE。
        await db.Entry(order).ReloadAsync();

        db.SessionPacks.Add(new SessionPack
        {
            OrderId = order.Id,
            StudentId = order.StudentId,
            ProductId = order.ProductId,
            TotalSessions = product.SessionCount,
            RemainingSessions = product.SessionCount,
            Status = PackStatus.Active,
        });

        // ParentAccount 按手机号全局共用（非租户实体，查询天然不过滤）。
        // 先查后插：同号第二单确认时此处已能看到已保存的行，不再创建。
        var hasParentAccount = await db.ParentAccounts.AsNoTracking()
            .AnyAsync(p => p.Phone == student.Phone);
        if (!hasParentAccount)
        {
            db.ParentAccounts.Add(new ParentAccount
            {
                Phone = student.Phone,
                PasswordHash = passwordHasher.Hash(DefaultPassword.Initial),
            });
        }

        try
        {
            // 状态翻转、课时包、家长账号在同一事务内落库并提交。
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateException)
        {
            // 唯一索引兜底（SessionPacks.OrderId / ParentAccounts.Phone）：
            // CAS 之外的窗口把竞态变成 409，而不是静默双包或 500。
            await tx.RollbackAsync();
            throw new ConflictException("订单已处理");
        }
    }

    /// <summary>
    /// POST /api/org/orders/{id}/cancel。仅 Pending（已确认订单走作废），
    /// 否则 400；调用者须是 Admin 或订单创建人本人，否则 403。状态翻转
    /// 同样用 CAS（WHERE Status=Pending）：并发对手先一步处理（确认/取消）
    /// 时 affected==0 → 回滚 → 409 "订单已处理"。课时包在确认时才诞生，
    /// 取消无需级联。
    /// </summary>
    public async Task CancelAsync(int orderId, int callerStaffId, StaffRole role)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId)
            ?? throw new NotFoundException("订单不存在");

        if (order.Status != OrderStatus.Pending)
        {
            throw new BusinessRuleException("仅待确认订单可取消，已确认订单请作废");
        }

        if (role != StaffRole.Admin && order.CreatedById != callerStaffId)
        {
            throw new ForbiddenActionException("仅管理员或订单创建人可以取消订单");
        }

        await using var tx = await db.Database.BeginTransactionAsync();

        var affected = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Cancelled));

        if (affected == 0)
        {
            // 并发对手已抢先处理（确认/取消）：回滚并 409。
            await tx.RollbackAsync();
            throw new ConflictException("订单已处理");
        }

        // 同步变更跟踪器（同上下文后续读的一致性；无后续 SaveChanges）。
        await db.Entry(order).ReloadAsync();

        await tx.CommitAsync();
    }

    /// <summary>
    /// POST /api/org/orders/{id}/void（AdminOnly）。仅 Active：Pending 引导
    /// 走取消（400），Cancelled/Voided 409 "订单已处理"。原因必填 ≤200
    /// （Review Focus 3）；退款金额可空但 ≥0。状态翻转用 CAS
    /// （WHERE Status=Active + 作废三字段 + RefundAmount 一次写入），
    /// 并发对手先一步处理时 affected==0 → 回滚 → 409；关联课时包在同一
    /// 显式事务、同一次 SaveChanges 内一并作废后提交。
    /// </summary>
    public async Task VoidAsync(int orderId, string? reason, decimal? refundAmount, int staffId)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId)
            ?? throw new NotFoundException("订单不存在");

        switch (order.Status)
        {
            case OrderStatus.Pending:
                // 待确认订单没有账务后果：作废入口引导走取消。
                throw new BusinessRuleException("待确认订单请取消，无需作废");
            case OrderStatus.Cancelled or OrderStatus.Voided:
                throw new ConflictException("订单已处理");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrEmpty(trimmedReason))
        {
            throw new BusinessRuleException("作废原因不能为空");
        }

        if (trimmedReason.Length > MaxVoidReasonLength)
        {
            throw new BusinessRuleException($"作废原因不能超过 {MaxVoidReasonLength} 个字符");
        }

        if (refundAmount is < 0)
        {
            throw new BusinessRuleException("退款金额不能为负数");
        }

        var voidedAt = DateTime.Now;
        await using var tx = await db.Database.BeginTransactionAsync();

        // CAS：仅当订单仍为 Active 才作废（作废字段与状态一次写入）。
        var affected = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Voided)
                .SetProperty(o => o.VoidedById, staffId)
                .SetProperty(o => o.VoidedAt, voidedAt)
                .SetProperty(o => o.VoidReason, trimmedReason)
                .SetProperty(o => o.RefundAmount, refundAmount));

        if (affected == 0)
        {
            // 并发对手已抢先处理（确认后作废/取消）：回滚并 409。
            await tx.RollbackAsync();
            throw new ConflictException("订单已处理");
        }

        // 同步变更跟踪器：后续课时包查询与 SaveChanges 只处理包，不再
        // 重复发出无守卫的订单 UPDATE。
        await db.Entry(order).ReloadAsync();

        // 关联课时包同批作废（一个订单一个包，遍历防御多包形态）。
        var packs = await db.SessionPacks.Where(sp => sp.OrderId == order.Id).ToListAsync();
        foreach (var pack in packs)
        {
            pack.Status = PackStatus.Voided;
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// GET /api/org/orders/{id}：订单全字段 + vouchers + student/product/pack
    /// 摘要。未知/跨机构 id 一律 404（Review Focus 1）。pack 在确认时才
    /// 诞生，未确认为 null。
    /// </summary>
    public async Task<OrderDetail> GetDetailAsync(int orderId)
    {
        var order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == orderId)
            ?? throw new NotFoundException("订单不存在");

        var student = await db.Students.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == order.StudentId)
            ?? throw new NotFoundException("学员不存在");
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == order.ProductId)
            ?? throw new NotFoundException("产品不存在");
        var pack = await db.SessionPacks.AsNoTracking()
            .Where(sp => sp.OrderId == order.Id)
            .OrderBy(sp => sp.Id)
            .FirstOrDefaultAsync();
        var vouchers = await db.Vouchers.AsNoTracking()
            .Where(v => v.OrderId == order.Id)
            .OrderBy(v => v.Id)
            .ToListAsync();

        return new OrderDetail(
            order.Id,
            order.OrderNo,
            order.StudentId,
            order.ProductId,
            order.Amount,
            order.SalesId,
            order.Status.ToString(),
            order.CreatedById,
            order.ConfirmedById,
            order.ConfirmedAt,
            order.VoidedById,
            order.VoidedAt,
            order.VoidReason,
            order.RefundAmount,
            order.Remark,
            order.CreatedAt,
            vouchers.Select(v => new OrderVoucherItem(v.Id, $"/api/files/vouchers/{v.Id}")).ToList(),
            new OrderStudentSummary(student.Id, student.Name, student.Phone),
            new OrderProductSummary(product.Id, product.Name, product.Type.ToString()),
            pack is null
                ? null
                : new OrderPackSummary(pack.Id, pack.TotalSessions, pack.RemainingSessions, pack.Status.ToString()));
    }

    /// <summary>
    /// GET /api/org/orders?status&amp;studentId&amp;page&amp;pageSize：本机构
    /// （租户过滤器）内按 Id 升序分页；status/studentId 可选、可组合。
    /// status 的安全解析在控制器（非法值 400，绝不 500）。
    /// </summary>
    public async Task<Paged<OrderListItem>> ListAsync(
        OrderStatus? status, int? studentId, int page, int pageSize)
    {
        var query = db.Orders.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        if (studentId is not null)
        {
            query = query.Where(o => o.StudentId == studentId);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var orders = await query
            .OrderBy(o => o.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        return new Paged<OrderListItem>(total, await ToListItemsAsync(orders));
    }

    /// <summary>
    /// 生成订单号：SO + yyyyMMdd + [100000, 999999) 随机数。唯一索引是
    /// 全局的，跨机构碰撞同样会撞索引，因此查重显式绕过租户过滤器
    /// （本任务除 ParentAccount 外唯一一处 IgnoreQueryFilters，裁定）；
    /// 冲突换随机数重试，3 次后放弃并 409。
    /// </summary>
    private async Task<string> GenerateOrderNoAsync()
    {
        for (var attempt = 0; attempt < OrderNoMaxAttempts; attempt++)
        {
            var orderNo = $"SO{DateTime.Now:yyyyMMdd}{Random.Shared.Next(100000, 999999)}";
            var taken = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(o => o.OrderNo == orderNo);
            if (!taken)
            {
                return orderNo;
            }
        }

        throw new ConflictException("订单号生成冲突，请重试");
    }

    /// <summary>员工令牌必须携带 staff_id，否则视为损坏的令牌（403）。</summary>
    private int RequireStaffId() =>
        currentUser.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");

    /// <summary>列表项的 studentName/productName/salesName 批量解析（本机构，经租户过滤器）。</summary>
    private async Task<List<OrderListItem>> ToListItemsAsync(List<Order> orders)
    {
        var studentIds = orders.Select(o => o.StudentId).Distinct().ToList();
        var studentNames = studentIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Students.AsNoTracking()
                .Where(s => studentIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        var productIds = orders.Select(o => o.ProductId).Distinct().ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

        var salesIds = orders.Select(o => o.SalesId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var salesNames = salesIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => salesIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        // 学员/产品没有删除入口：页内订单的 id 必能解析出名称（字典与订单
        // 查询同一机构空间），sales 则可能为空或指向已不存在的员工。
        return orders.Select(o => new OrderListItem(
            o.Id,
            o.OrderNo,
            o.StudentId,
            studentNames.GetValueOrDefault(o.StudentId)!,
            o.ProductId,
            productNames.GetValueOrDefault(o.ProductId)!,
            o.Amount,
            o.SalesId,
            o.SalesId is int salesId ? salesNames.GetValueOrDefault(salesId) : null,
            o.Status.ToString(),
            o.CreatedAt)).ToList();
    }
}
