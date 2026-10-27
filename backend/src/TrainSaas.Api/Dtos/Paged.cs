namespace TrainSaas.Api.Dtos;

/// <summary>
/// 统一分页响应（Global Constraints）：JSON 形如 <c>{ "total": …, "items": [...] }</c>。
/// 查询参数 page 默认 1、pageSize 默认 20、上限 100，由 <see cref="Paging.Normalize"/>
/// 统一钳制。
/// </summary>
public record Paged<T>(int Total, List<T> Items);

/// <summary>分页查询参数的统一钳制规则（Global Constraints）。</summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>page 缺省/小于 1 → 1；pageSize 缺省/小于 1 → 20；大于 100 → 100。</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var normalizedPage = page is >= 1 ? page.Value : 1;
        var normalizedPageSize = pageSize ?? DefaultPageSize;
        if (normalizedPageSize < 1)
        {
            normalizedPageSize = DefaultPageSize;
        }
        else if (normalizedPageSize > MaxPageSize)
        {
            normalizedPageSize = MaxPageSize;
        }

        return (normalizedPage, normalizedPageSize);
    }
}
