namespace TrainSaas.Api.Domain;

/// <summary>学员在固定班（模板）上的报名。</summary>
public class ClassEnrollment : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int TemplateId { get; set; }

    public int StudentId { get; set; }

    public int EnrolledById { get; set; }

    public DateTime EnrolledAt { get; set; } = DateTime.Now;

    public EnrollmentStatus Status { get; set; }
}
