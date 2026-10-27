namespace TrainSaas.Api.Domain;

/// <summary>培训机构（租户）。平台超管管理，不受租户过滤器约束。</summary>
public class Institution
{
    public int Id { get; set; }

    /// <summary>机构唯一编码，例如 "demo"。</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? LogoPath { get; set; }

    public string? IntroText { get; set; }

    public string? ContactPhone { get; set; }

    public string? Address { get; set; }

    public InstitutionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
