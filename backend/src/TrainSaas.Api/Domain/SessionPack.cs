namespace TrainSaas.Api.Domain;

/// <summary>学员课时包（订单确认时生成，按产品匹配扣减）。</summary>
public class SessionPack : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int OrderId { get; set; }

    public int StudentId { get; set; }

    public int ProductId { get; set; }

    public int TotalSessions { get; set; }

    public int RemainingSessions { get; set; }

    public PackStatus Status { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
