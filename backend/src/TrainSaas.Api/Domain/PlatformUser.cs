namespace TrainSaas.Api.Domain;

/// <summary>平台超管账号（跨租户，不受租户过滤器约束）。</summary>
public class PlatformUser
{
    public int Id { get; set; }

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;
}
