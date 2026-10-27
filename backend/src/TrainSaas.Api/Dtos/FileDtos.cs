namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/files 成功响应：path = 相对存储路径（正斜杠分隔，即数据库
/// 存储原值，前端拼接静态/授权下发地址时直接使用）。
/// </summary>
public record UploadFileResponse(string Path);

/// <summary>POST /api/org/files/vouchers/{orderId} 成功响应：url = 凭据下发地址 /api/files/vouchers/{id}。</summary>
public record VoucherCreatedResponse(int Id, string Url);
