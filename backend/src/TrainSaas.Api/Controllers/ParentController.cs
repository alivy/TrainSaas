using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 家长端 API（Task 15），类级 [ParentOnly]：孩子列表、课时包、课时流水、
/// 未来课表。家长令牌跨机构（只有 phone claim），数据面的租户过滤豁免纪律
/// （每条查询 IgnoreQueryFilters + 显式 phone/孩子归属谓词、他人孩子一律 404
/// 不泄露存在性）全部在 <see cref="ParentService"/> 内，见该类注释。四个路由
/// 对家长都是只读的；唯一的写入是课表请求顺带触发的按机构惰性生成（R15）。
///
/// studentId 必填（缺失 400，不静默当成 0）；records 的 from/to 可选、严格
/// yyyy-MM-dd（非法 400）；schedule 的 days 安全解析（非法 400）并钳制 1..60，
/// 缺省 14 天。
/// </summary>
[ApiController]
[Route("api/parent")]
[Authorize(Policy = AuthPolicies.ParentOnly)]
public class ParentController(ParentService parentService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>课表默认窗口：14 天。</summary>
    private const int DefaultScheduleDays = 14;

    /// <summary>课表窗口上限：60 天（与员工端课表查询上限一致）。</summary>
    private const int MaxScheduleDays = 60;

    /// <summary>
    /// GET /api/parent/children →
    /// [{studentId,name,institutionId,institutionName,institutionCode}]：
    /// 令牌手机号名下所有在读孩子，跨机构（设计内豁免），按机构、学员升序。
    /// </summary>
    [HttpGet("children")]
    public async Task<ActionResult<List<ParentChildItem>>> Children() =>
        Ok(await parentService.ListChildrenAsync(currentUser));

    /// <summary>
    /// GET /api/parent/packs?studentId →
    /// [{packId,productName,totalSessions,remainingSessions,status,orderNo,
    /// purchasedAt}]：含已作废的包（家长看得见历史），packId 降序。
    /// </summary>
    [HttpGet("packs")]
    public async Task<ActionResult<List<ParentPackItem>>> Packs(int? studentId) =>
        Ok(await parentService.ListPacksAsync(RequireStudentId(studentId), currentUser));

    /// <summary>
    /// GET /api/parent/records?studentId&amp;from&amp;to&amp;page&amp;pageSize →
    /// Paged&lt;{id,kind,direction,productName,createdAt,reason,
    /// classSessionStartAt?}&gt;（CreatedAt desc, Id desc）。冲正行如实出现
    /// （kind=Reversal、direction=-1、reason=撤销原因）。
    /// </summary>
    [HttpGet("records")]
    public async Task<ActionResult<Paged<ParentRecordItem>>> Records(
        int? studentId, string? from, string? to, int? page, int? pageSize)
    {
        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        return Ok(await parentService.ListRecordsAsync(
            RequireStudentId(studentId),
            QueryParsers.ParseOptionalDate(from, "from"),
            QueryParsers.ParseOptionalDate(to, "to"),
            pageNumber,
            pageSizeValue,
            currentUser));
    }

    /// <summary>
    /// GET /api/parent/schedule?studentId&amp;days=14 →
    /// [{startAt,endAt,productName,teacherName,room,institutionName}]：在班且
    /// 启用模板的未来 Scheduled 实例，startAt 升序；days 钳制 1..60。查询前按
    /// 孩子所属机构触发一次惰性生成（R15），机构侧没人查过课表时家长也看得到。
    /// </summary>
    [HttpGet("schedule")]
    public async Task<ActionResult<List<ParentScheduleItem>>> Schedule(int? studentId, string? days) =>
        Ok(await parentService.ListScheduleAsync(
            RequireStudentId(studentId), ParseDays(days), currentUser));

    /// <summary>studentId 必填：缺失 → 400（绝不静默当成 0 去查别人的/不存在的孩子）。</summary>
    private static int RequireStudentId(int? studentId) =>
        studentId ?? throw new BusinessRuleException("studentId 不能为空");

    /// <summary>
    /// 安全解析 days：缺失/空白 → 14；非整数 → 400；其余钳制到 1..60。
    /// </summary>
    private static int ParseDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days))
        {
            return DefaultScheduleDays;
        }

        if (!int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new BusinessRuleException("days 必须是整数");
        }

        return Math.Clamp(parsed, 1, MaxScheduleDays);
    }
}
