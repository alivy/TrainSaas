using System.Globalization;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Dtos;

/// <summary>
/// 查询参数/请求字段的共享解析器（最终审查 T15③+M1 收敛：此前严格
/// yyyy-MM-dd 解析在 4 个控制器各有一份 <c>ParseDate/ParseOptionalDate</c>，
/// 严格枚举解析散落在 7 处且口径漂移——数字串 "0"/"1" 在多数站点被
/// Enum.TryParse+IsDefined 误收为合法值）。所有失败路径一律抛
/// <see cref="BusinessRuleException"/>（HTTP 400，消息即用户可见文案），
/// 绝不抛格式异常把坏请求变成 500。与 <see cref="Paging"/> 同类：请求形状
/// 助手，控制器与服务层共用。
///
/// 枚举解析口径（全站统一）：接受枚举名、忽略大小写（如 "Admin"）；
/// **纯数字串一律 400**（即使 TryParse 能映射到已定义值——"1" 是调用方
/// bug 的信号，不该被当成合法过滤值），未定义名（"Bogus"）与越界值
/// （"999"）同样 400。
/// </summary>
public static class QueryParsers
{
    /// <summary>全站日期参数的唯一格式：严格 yyyy-MM-dd（本地日期）。</summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// 严格解析**必填**日期：null/空白或非 yyyy-MM-dd → 400；成功返回当日
    /// 零点（本地日期）。
    /// </summary>
    public static DateTime ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParseExact(
                value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new BusinessRuleException($"{field} 必须是 yyyy-MM-dd 格式的日期");
        }

        return parsed.Date;
    }

    /// <summary>严格解析**可选**日期：null/空白 → null（不过滤）；非法 → 400。</summary>
    public static DateTime? ParseOptionalDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseDate(value, field);
    }

    /// <summary>严格解析**必填**枚举（口径见类注释）：非法 → 400。</summary>
    public static T ParseEnum<T>(string? value, string error) where T : struct, Enum
    {
        if (!TryParseEnum<T>(value, out var parsed))
        {
            throw new BusinessRuleException(error);
        }

        return parsed;
    }

    /// <summary>严格解析**可选**枚举：null/空白 → null（不过滤）；非法 → 400。</summary>
    public static T? ParseOptionalEnum<T>(string? value, string error) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseEnum<T>(value, error);
    }

    private static bool TryParseEnum<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.All(char.IsAsciiDigit)
            || !Enum.TryParse(trimmed, ignoreCase: true, out parsed)
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        return true;
    }
}
