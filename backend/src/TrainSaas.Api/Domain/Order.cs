namespace TrainSaas.Api.Domain;

/// <summary>订单。确认后生成课时包；可作废（需原因）。</summary>
public class Order : ITenanted
{
    public int Id { get; set; }

    public int InstitutionId { get; set; }

    /// <summary>订单号，全局唯一。</summary>
    public string OrderNo { get; set; } = null!;

    public int StudentId { get; set; }

    public int ProductId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>成交销售员工 Id。</summary>
    public int? SalesId { get; set; }

    public OrderStatus Status { get; set; }

    /// <summary>下单人（创建订单的员工）。</summary>
    public int CreatedById { get; set; }

    public int? ConfirmedById { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public int? VoidedById { get; set; }

    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因，作废时必填。</summary>
    public string? VoidReason { get; set; }

    public decimal? RefundAmount { get; set; }

    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>订单凭证（收据/转账截图），删除订单时级联删除。</summary>
    public List<Voucher> Vouchers { get; set; } = [];
}
