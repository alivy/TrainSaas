namespace TrainSaas.Api.Domain;

/// <summary>由模板生成的具体课次实例。</summary>
public class ClassSession : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int TemplateId { get; set; }

    public int TeacherId { get; set; }

    public DateTime StartAt { get; set; }

    public DateTime EndAt { get; set; }

    public SessionStatus Status { get; set; }
}
