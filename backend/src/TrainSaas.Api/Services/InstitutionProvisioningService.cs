using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 平台超管的机构开通服务。
///
/// 本服务运行在 platform 令牌下（<c>ICurrentUser.InstitutionId == null</c>）：
/// Staff 的租户过滤器会把所有行都隐藏掉，因此管理员手机号的占用检查必须
/// <c>IgnoreQueryFilters()</c> + 显式 <c>Phone ==</c> 条件；新建 Staff 的
/// InstitutionId 也必须显式赋值——DbContext 的自动补全只对 staff 令牌生效，
/// 无租户上下文时反而会抛 <see cref="ForbiddenActionException"/>。
/// </summary>
public class InstitutionProvisioningService(AppDbContext db, IPasswordHasherService hasher)
{
    /// <summary>机构编码：3–32 位小写字母/数字/中划线，首尾不得为中划线。</summary>
    public static readonly Regex CodePattern =
        new("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.Compiled);

    /// <summary>
    /// 开通机构：先校验（编码格式 → 编码唯一 → 管理员手机号全局唯一），再在
    /// 同一数据库事务中创建 Institution(Status=Active) 与初始密码 123456 的
    /// Admin 员工——两者要么同时落库要么同时回滚。校验失败抛
    /// <see cref="BusinessRuleException"/>（400）或 <see cref="ConflictException"/>（409）。
    /// </summary>
    public async Task<(Institution Institution, Staff Admin)> CreateAsync(CreateInstitutionRequest request)
    {
        Validate(request);

        if (await db.Institutions.AnyAsync(i => i.Code == request.Code))
            throw new ConflictException("机构编码已存在");

        // 全局唯一的登录手机号：platform 令牌无租户上下文，必须忽略租户过滤器。
        if (await db.Staff.IgnoreQueryFilters().AnyAsync(s => s.Phone == request.AdminPhone))
            throw new ConflictException("手机号已被占用");

        var institution = new Institution
        {
            Code = request.Code,
            Name = request.Name,
            ContactPhone = request.ContactPhone,
            Address = request.Address,
            IntroText = request.IntroText,
            Status = InstitutionStatus.Active,
        };
        var admin = new Staff
        {
            Name = request.AdminName,
            Phone = request.AdminPhone,
            Role = StaffRole.Admin,
            IsActive = true,
            PasswordHash = hasher.Hash(DefaultPassword.Initial),
        };

        // 一个事务内的两段 SaveChanges：机构的 Id 由数据库生成，而 Staff 与
        // Institution 之间没有模型导航关系，单次 SaveChanges 无法把生成的 Id
        // 传播给外键；显式事务保证原子性（第二段任何失败都整体回滚）。
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        admin.InstitutionId = institution.Id;
        db.Staff.Add(admin);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return (institution, admin);
    }

    /// <summary>格式校验先于唯一性校验：编码既非法又重复时应报 400 而非 409。</summary>
    private static void Validate(CreateInstitutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BusinessRuleException("机构名称不能为空");
        if (string.IsNullOrWhiteSpace(request.AdminName))
            throw new BusinessRuleException("管理员姓名不能为空");
        if (string.IsNullOrWhiteSpace(request.AdminPhone))
            throw new BusinessRuleException("管理员手机号不能为空");
        if (request.Code is null || !CodePattern.IsMatch(request.Code))
            throw new BusinessRuleException("机构编码格式不正确（3-32位小写字母、数字或中划线，首尾须为字母或数字）");
    }
}
