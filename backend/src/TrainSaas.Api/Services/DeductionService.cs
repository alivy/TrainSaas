using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 扣课服务（账务心脏）。Task 14 复用此接口，签名必须保持一致。
/// </summary>
public interface IDeductionService
{
    /// <summary>
    /// 从学员该产品下最早（(CreatedAt, Id) 升序）的可用课时包扣 1 课时并落一条
    /// Direction=+1 的 <see cref="Consumption"/> 流水。无可用包（Active 且
    /// Remaining&gt;0 的同产品包不存在）→ <see cref="NoAvailablePackException"/>。
    /// </summary>
    Task<Consumption> DeductAsync(
        int studentId,
        int productId,
        int? classSessionId,
        ConsumptionKind kind,
        int operatorId,
        string? reason = null);
}

/// <summary>
/// 扣课实现（R10/R13 账务心脏）：
/// <list type="bullet">
/// <item>选包：租户过滤器内 <c>Status==Active &amp;&amp; Remaining&gt;0 &amp;&amp;
/// ProductId==productId &amp;&amp; StudentId==studentId</c>，按 (CreatedAt, Id)
/// 升序取第一个（同产品最早先扣）；无 → <see cref="NoAvailablePackException"/>
/// ("无可用课时包")——Voided/余量为 0/跨机构的包一律不可见。</item>
/// <item>CAS 扣减：显式事务内 ExecuteUpdate 带
/// <c>WHERE Id==pack.Id &amp;&amp; Status==Active &amp;&amp; Remaining&gt;0</c>
/// 直接对数据库求值（不经变更跟踪器）：并发抢扣最后一课时时，数据库守卫
/// 保证恰一个赢家，RemainingSessions 永不为负；输家 affected==0 → 回滚 →
/// 清跟踪器后重选一次（另一包可能已可用）→ 仍无则 400。重选后的第二次
/// CAS 再失败即 400，不做无限重试。</item>
/// <item>落流水：扣减与 Consumption{Direction=+1, Kind, ClassSessionId,
/// SessionPackId, StudentId, OperatorId, Reason} 在同一事务内提交
/// （InstitutionId 由 SaveChanges 按当前用户自动填充），返回该流水。</item>
/// </list>
/// </summary>
public class DeductionService(AppDbContext db) : IDeductionService
{
    public async Task<Consumption> DeductAsync(
        int studentId,
        int productId,
        int? classSessionId,
        ConsumptionKind kind,
        int operatorId,
        string? reason = null)
    {
        var pack = await SelectPackAsync(studentId, productId)
            ?? throw new NoAvailablePackException("无可用课时包");

        // 至多重选一次：首轮 CAS 失败（包被并发对手抢空/作废）后重选，
        // 重选出的包再失败即放弃（400），绝不无限循环。
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // CAS：守卫（Active 且有余量）在数据库上求值，输家 affected==0。
            var affected = await db.SessionPacks
                .Where(p => p.Id == pack.Id
                    && p.Status == PackStatus.Active
                    && p.RemainingSessions > 0)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.RemainingSessions, p => p.RemainingSessions - 1));

            if (affected == 0)
            {
                // 并发对手抢先扣空/作废了选中的包：回滚，按最新库态重选一次。
                await tx.RollbackAsync();
                if (attempt > 0)
                {
                    // 重选出的包也被抢空：放弃（400），不再循环。
                    throw new NoAvailablePackException("无可用课时包");
                }

                // 清变更跟踪器：丢弃可能陈旧的包快照，让重选读到数据库真值。
                db.ChangeTracker.Clear();
                pack = await SelectPackAsync(studentId, productId)
                    ?? throw new NoAvailablePackException("无可用课时包");
                continue;
            }

            // 把 CAS 结果同步回变更跟踪器（ExecuteUpdate 不经过跟踪器，直接
            // 对数据库求值）：与 OrderService 确认/作废同一模式——重载后的包
            // 实体保持 Unchanged，使 SaveChanges 只发流水的 INSERT；同一
            // 上下文后续对该包的跟踪读/写（如点名修正的回补 +1）看到扣减
            // 后的真值，不会用陈旧快照覆盖账务。
            await db.Entry(pack).ReloadAsync();

            // 扣减成功：同一事务内落 +1 流水（机构由 SaveChanges 自动填充）。
            var consumption = new Consumption
            {
                StudentId = studentId,
                SessionPackId = pack.Id,
                ClassSessionId = classSessionId,
                Kind = kind,
                Direction = 1,
                Reason = reason,
                OperatorId = operatorId,
                CreatedAt = DateTime.Now,
            };
            db.Consumptions.Add(consumption);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return consumption;
        }
    }

    /// <summary>
    /// 选包：租户过滤器内同产品、Active 且有余量，(CreatedAt, Id) 升序取最早。
    /// 跟踪查询（与既有服务一致）：单请求内重复调用经 identity map 稳定。
    /// </summary>
    private async Task<SessionPack?> SelectPackAsync(int studentId, int productId) =>
        await db.SessionPacks
            .Where(p => p.StudentId == studentId
                && p.ProductId == productId
                && p.Status == PackStatus.Active
                && p.RemainingSessions > 0)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync();
}
