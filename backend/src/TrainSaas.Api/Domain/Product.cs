namespace TrainSaas.Api.Domain;

/// <summary>课程包产品（固定班/灵活班）。</summary>
public class Product : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public string Name { get; set; } = null!;

    public ProductType Type { get; set; }

    /// <summary>售价（金额聚合必须在内存中计算，SQLite 的 decimal SQL 运算不可靠）。</summary>
    public decimal Price { get; set; }

    /// <summary>课时数。</summary>
    public int SessionCount { get; set; }

    public string? Description { get; set; }

    public string? CoverImagePath { get; set; }

    public bool IsPublished { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
