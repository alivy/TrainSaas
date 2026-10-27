namespace TrainSaas.Api.Domain;

/// <summary>固定班排班模板（每周几 + 起止时间 + 教师教室）。</summary>
public class ClassTemplate : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int ProductId { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    public int TeacherId { get; set; }

    public string? Room { get; set; }

    public int? Capacity { get; set; }

    public bool IsActive { get; set; }
}
