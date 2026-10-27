using System.Text.RegularExpressions;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 手机号校验（M2/I2 收敛）：登录/账号类手机号统一要求 <c>^1\d{10}$</c>
/// （与学员、意向人的既有规则同口径）。trim 后校验并返回规范值——落库与
/// 唯一性检查一律用 trim 后的手机号，避免" 138… "这类输入绕过唯一索引或
/// 制造登录不了的账号。非法值抛 <see cref="BusinessRuleException"/>（400）。
///
/// 使用方：员工创建（StaffService）、机构开通管理员
/// （InstitutionProvisioningService）、家长密码重置（OrgParentsController）。
/// 学员/意向人的既有校验保持各自服务内实现（已审代码不动，文案不同）。
/// </summary>
public static class PhoneValidator
{
    /// <summary>1 开头的 11 位数字。</summary>
    public static readonly Regex Pattern = new(@"^1\d{10}$", RegexOptions.Compiled);

    /// <summary>
    /// trim + 格式校验。<paramref name="label"/> 用于拼装用户可见文案
    /// （如 "员工手机号" → "员工手机号不能为空" / "员工手机号格式无效"）。
    /// </summary>
    public static string Validate(string? phone, string label)
    {
        var trimmed = phone?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new BusinessRuleException($"{label}不能为空");
        if (!Pattern.IsMatch(trimmed))
            throw new BusinessRuleException($"{label}格式无效");

        return trimmed;
    }
}
