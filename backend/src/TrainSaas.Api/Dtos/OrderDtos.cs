namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/orders {studentId, productId, amount, salesId?, remark?}。
/// studentId/productId 必须是本机构（租户过滤）可见的行，否则 404；
/// amount 必填且 ≥0——议价金额可与产品售价不同，不做等值校验；
/// salesId 缺省=当前员工的 staffId，显式传入时必须是本机构在职销售
/// （Role=Sales 且 IsActive），否则 400。
/// </summary>
public record CreateOrderRequest(
    int StudentId,
    int ProductId,
    decimal Amount,
    int? SalesId,
    string? Remark);

/// <summary>
/// POST /api/org/orders/{id}/void {reason, refundAmount?}（AdminOnly）。
/// reason 必填 ≤200 字符；refundAmount 可选但须 ≥0。
/// </summary>
public record VoidOrderRequest(string? Reason, decimal? RefundAmount);

/// <summary>POST /api/org/orders 201 响应：{id, orderNo, status}。</summary>
public record OrderCreatedResponse(int Id, string OrderNo, string Status);

/// <summary>
/// GET /api/org/orders?status&amp;studentId&amp;page&amp;pageSize 列表项：
/// studentName/productName/salesName 批量解析（本机构经租户过滤器；
/// salesId 为空或指向已不存在的员工时 salesName 为 null）。
/// </summary>
public record OrderListItem(
    int Id,
    string OrderNo,
    int StudentId,
    string StudentName,
    int ProductId,
    string ProductName,
    decimal Amount,
    int? SalesId,
    string? SalesName,
    string Status,
    DateTime CreatedAt);

/// <summary>
/// GET /api/org/orders/{id} 详情聚合：订单全字段 + vouchers（含授权下发
/// url）+ student/product 摘要 + pack 摘要——课时包确认时才诞生，未确认的
/// 订单 pack 为 null。
/// </summary>
public record OrderDetail(
    int Id,
    string OrderNo,
    int StudentId,
    int ProductId,
    decimal Amount,
    int? SalesId,
    string Status,
    int CreatedById,
    int? ConfirmedById,
    DateTime? ConfirmedAt,
    int? VoidedById,
    DateTime? VoidedAt,
    string? VoidReason,
    decimal? RefundAmount,
    string? Remark,
    DateTime CreatedAt,
    List<OrderVoucherItem> Vouchers,
    OrderStudentSummary Student,
    OrderProductSummary Product,
    OrderPackSummary? Pack);

/// <summary>订单凭证：{id, url}——url 即授权下发路由 GET /api/files/vouchers/{id}。</summary>
public record OrderVoucherItem(int Id, string Url);

/// <summary>订单详情中的学员摘要：{id, name, phone}。</summary>
public record OrderStudentSummary(int Id, string Name, string Phone);

/// <summary>订单详情中的产品摘要：{id, name, type}。</summary>
public record OrderProductSummary(int Id, string Name, string Type);

/// <summary>订单详情中的课时包摘要：{id, totalSessions, remainingSessions, status}。</summary>
public record OrderPackSummary(
    int Id,
    int TotalSessions,
    int RemainingSessions,
    string Status);
