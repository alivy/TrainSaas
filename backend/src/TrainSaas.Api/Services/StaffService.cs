using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 机构员工管理服务，在 Admin 员工令牌下运行。业务规则：
/// 手机号全局唯一（任意机构的占用都拒绝）、不能停用自己（403）、
/// 机构必须始终保留至少一名活跃 Admin（降级/停用最后一个活跃 Admin → 409）、
/// 新建员工初始密码 123456 且默认激活。
///
/// 租户过滤器注意：Admin 令牌的过滤器会把其它机构的 Staff 行隐藏掉，
/// 因此全局手机号冲突检查必须 IgnoreQueryFilters（任何机构的命中——含本
/// 机构——都是 409）；而按 id 的读写保持过滤，跨机构的 id 自然 404。
/// </summary>
public class StaffService(AppDbContext db, IPasswordHasherService hasher)
{
    /// <summary>
    /// GET /api/org/staff?role&amp;page&amp;pageSize 的数据源：本机构（租户过滤器）
    /// 内按 Id 升序分页；role 可选，非法值 400。
    /// </summary>
    public async Task<Paged<StaffListItem>> ListAsync(string? role, int? page, int? pageSize)
    {
        var query = db.Staff.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(role))
        {
            var parsed = ParseRole(role);
            query = query.Where(s => s.Role == parsed);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var staff = await query
            .OrderBy(s => s.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        return new Paged<StaffListItem>(
            total,
            staff.Select(ToListItem).ToList());
    }

    /// <summary>
    /// 创建员工 {name,phone,role}：初始密码 123456、默认激活，归属机构由
    /// SaveChanges 按当前 Admin 令牌自动填充。手机号经
    /// <see cref="PhoneValidator"/>（trim + <c>^1\d{10}$</c>，M2：非法 400，
    /// 落库/唯一性检查一律用 trim 后的规范值）；与任意机构（含本机构）的
    /// 员工冲突 → <see cref="ConflictException"/> 409。
    /// </summary>
    public async Task<Staff> CreateAsync(CreateStaffRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BusinessRuleException("员工姓名不能为空");
        var phone = PhoneValidator.Validate(request.Phone, "员工手机号");
        var role = ParseRole(request.Role);

        // 全局唯一的登录手机号：Admin 令牌的租户过滤器会隐藏其它机构的员工，
        // 冲突检查必须忽略过滤器——排除任何机构的命中，包括本机构。
        if (await db.Staff.IgnoreQueryFilters().AnyAsync(s => s.Phone == phone))
            throw new ConflictException("手机号已被占用");

        var staff = new Staff
        {
            Name = request.Name,
            Phone = phone,
            Role = role,
            // 实体默认是 false——新员工必须显式激活，否则创建后无法登录。
            IsActive = true,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
            // InstitutionId 留 0：SaveChanges 按当前 Admin 令牌的机构自动填充。
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    /// <summary>
    /// PUT {name?,role?}——仅更新非 null 字段。把 Admin 降级（无论其自身是否
    /// 活跃）前必须仍有另一名活跃 Admin，否则机构将无人能进入 Admin 端点 →
    /// 409。未知 id（含跨机构不可见）→ 404。
    ///
    /// I1（最终审查）：角色改写另设**调用者数据库状态守卫**——令牌里的
    /// role=Admin 只在签发那一刻可信，过期窗口内被降级/停用的管理员若仍能
    /// 凭旧令牌改写角色，就可以把自己晋升回 Admin（永久改写数据库权限）。
    /// 因此凡请求携带 Role，就先加载调用者本人的库内行并要求其仍为活跃
    /// Admin，否则 403（镜像 <see cref="SetActiveAsync"/> 的"防御令牌过期
    /// 窗口"模式）。名称改写不在守卫范围内（每请求全量状态校验超出本次
    /// 修复波范围，见最终审查 out-of-scope 裁定）。
    /// </summary>
    public async Task UpdateAsync(int id, UpdateStaffRequest request, int currentStaffId)
    {
        if (request.Role is not null)
        {
            await EnsureCallerIsActiveAdminAsync(currentStaffId);
        }

        var staff = await FindStaffOrThrowAsync(id);

        if (request.Name is not null)
            staff.Name = request.Name;

        if (request.Role is not null)
        {
            var role = ParseRole(request.Role);
            if (role != staff.Role)
            {
                if (staff.Role == StaffRole.Admin && role != StaffRole.Admin)
                {
                    await EnsureNotLastActiveAdminAsync(id, "降级");
                }

                staff.Role = role;
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// I1 调用者守卫：从数据库加载调用者本人的员工行（租户过滤器 = 调用者
    /// 令牌机构，本人行必然可见），要求 IsActive 且 Role==Admin——行缺失、
    /// 已停用或已降级一律 <see cref="ForbiddenActionException"/> 403。
    /// </summary>
    private async Task EnsureCallerIsActiveAdminAsync(int currentStaffId)
    {
        var caller = await db.Staff.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == currentStaffId);
        if (caller is null || !caller.IsActive || caller.Role != StaffRole.Admin)
            throw new ForbiddenActionException("当前账号已不是活跃管理员，无权修改角色");
    }

    /// <summary>
    /// 停用/激活员工。停用自己 → <see cref="ForbiddenActionException"/> 403；
    /// 停用本机构最后一个活跃 Admin → 409（防御令牌过期窗口：调用者令牌仍带
    /// role=Admin，但其行已不是活跃 Admin，例如刚被降级/停用）。
    /// </summary>
    public async Task SetActiveAsync(int id, bool active, int currentStaffId)
    {
        var staff = await FindStaffOrThrowAsync(id);

        if (!active && staff.Id == currentStaffId)
            throw new ForbiddenActionException("不能停用自己的账号");

        if (!active && staff.Role == StaffRole.Admin && staff.IsActive)
        {
            await EnsureNotLastActiveAdminAsync(id, "停用");
        }

        staff.IsActive = active;
        await db.SaveChangesAsync();
    }

    /// <summary>重置密码为初始密码 123456。未知 id（含跨机构不可见）→ 404。</summary>
    public async Task ResetPasswordAsync(int id)
    {
        var staff = await FindStaffOrThrowAsync(id);
        staff.PasswordHash = hasher.Hash(DefaultPassword.Initial);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 安全解析角色：枚举名忽略大小写（"Admin"）；数字串（"0"…"3"，此前被
    /// IsDefined 误收）与未定义名一律 400——统一走
    /// <see cref="QueryParsers.ParseEnum{T}"/>（M1 收敛，口径全站一致）。
    /// 非法值抛 <see cref="BusinessRuleException"/>（400），绝不 500。
    /// </summary>
    public static StaffRole ParseRole(string? role) =>
        QueryParsers.ParseEnum<StaffRole>(role, "角色参数无效");

    /// <summary>按 id 查员工；租户过滤器下不可见（跨机构/不存在）→ 404。</summary>
    private async Task<Staff> FindStaffOrThrowAsync(int id) =>
        await db.Staff.SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException("员工不存在");

    /// <summary>
    /// 断言 id 之外本机构仍有其它活跃 Admin（计数走租户过滤器 = Admin 令牌的
    /// 本机构）。id 是最后一个活跃 Admin 时抛 <see cref="ConflictException"/>。
    /// </summary>
    private async Task EnsureNotLastActiveAdminAsync(int id, string action)
    {
        var otherActiveAdmins = await db.Staff
            .CountAsync(s => s.Role == StaffRole.Admin && s.IsActive && s.Id != id);
        if (otherActiveAdmins == 0)
            throw new ConflictException($"机构最后一个活跃管理员不能{action}");
    }

    private static StaffListItem ToListItem(Staff staff) =>
        new(staff.Id, staff.Name, staff.Phone, staff.Role.ToString(), staff.IsActive, staff.CreatedAt);
}
