namespace TrainSaas.Api.Domain;

/// <summary>家长账号（跨机构共用手机号，不受租户过滤器约束）。</summary>
public class ParentAccount
{
    public int Id { get; set; }

    /// <summary>登录手机号，全局唯一索引。</summary>
    public string Phone { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
