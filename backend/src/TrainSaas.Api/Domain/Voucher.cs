namespace TrainSaas.Api.Domain;

/// <summary>订单凭证文件（收据/转账截图等）。</summary>
public class Voucher : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    public int OrderId { get; set; }

    /// <summary>文件相对路径（Storage:UploadRoot 之下）。</summary>
    public string FilePath { get; set; } = null!;

    public int UploadedById { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
