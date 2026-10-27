namespace TrainSaas.Api.Domain;

/// <summary>
/// 课时流水。Direction = +1 表示扣减课时，-1 表示冲正（回补）。
/// 冲正行 Kind=Reversal 且 ReversesConsumptionId 指向被冲正的原流水。
/// </summary>
public class Consumption : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int StudentId { get; set; }

    public int SessionPackId { get; set; }

    /// <summary>关联课次；随到随上/WalkIn 无课次时为空。</summary>
    public int? ClassSessionId { get; set; }

    public ConsumptionKind Kind { get; set; }

    /// <summary>+1 扣减 / -1 冲正。</summary>
    public int Direction { get; set; }

    public string? Reason { get; set; }

    public int? ReversesConsumptionId { get; set; }

    public int OperatorId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
