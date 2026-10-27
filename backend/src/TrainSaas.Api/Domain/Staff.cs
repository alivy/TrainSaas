namespace TrainSaas.Api.Domain;

/// <summary>机构员工（管理员/销售/教师/前台）。手机号全局唯一（登录账号）。</summary>
public class Staff : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>登录手机号，全局唯一索引。</summary>
    public string Phone { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public StaffRole Role { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
