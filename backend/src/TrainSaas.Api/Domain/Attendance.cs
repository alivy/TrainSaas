namespace TrainSaas.Api.Domain;

/// <summary>课次点名记录。同一课次同一学员唯一。</summary>
public class Attendance : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int ClassSessionId { get; set; }

    public int StudentId { get; set; }

    public AttendanceMark Mark { get; set; }

    /// <summary>点名操作人（员工 Id）。</summary>
    public int OperatorId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
