namespace TrainSaas.Api.Domain;

/// <summary>匿名购买意向（官网/落地表单提交）。</summary>
public class PurchaseIntent : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public string? Name { get; set; }

    public string Phone { get; set; } = null!;

    public int? ProductId { get; set; }

    public string? Message { get; set; }

    public IntentStatus Status { get; set; }

    /// <summary>跟进销售的员工 Id。</summary>
    public int? FollowedByStaffId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
