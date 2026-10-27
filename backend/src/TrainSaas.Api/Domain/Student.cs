namespace TrainSaas.Api.Domain;

/// <summary>学员。</summary>
public class Student : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public string Name { get; set; } = null!;

    public string Phone { get; set; } = null!;

    /// <summary>性别，自由文本（如 "男"/"女"），可空。</summary>
    public string? Gender { get; set; }

    public DateOnly? Birthday { get; set; }

    /// <summary>归属销售的员工 Id。</summary>
    public int? AssignedSalesId { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
