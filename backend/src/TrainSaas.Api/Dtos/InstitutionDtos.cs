namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/platform/institutions 请求。code 规则
/// <c>^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$</c>（3–32 位，小写字母/数字/中划线，
/// 首尾须为字母或数字）；adminPhone 不得与任何现有 Staff.Phone 冲突。
/// </summary>
public record CreateInstitutionRequest(
    string Code,
    string Name,
    string AdminName,
    string AdminPhone,
    string? ContactPhone,
    string? Address,
    string? IntroText);

/// <summary>POST /api/platform/institutions → 201 {id,code,name}。</summary>
public record InstitutionCreatedResponse(int Id, string Code, string Name);

/// <summary>GET /api/platform/institutions 列表项（status 为 "Active"/"Disabled"）。</summary>
public record InstitutionListItem(
    int Id,
    string Code,
    string Name,
    string Status,
    int StaffCount,
    int StudentCount,
    DateTime CreatedAt);

/// <summary>PUT /api/platform/institutions/{id}——仅更新非 null 字段。</summary>
public record UpdateInstitutionRequest(string? Name, string? IntroText, string? ContactPhone, string? Address);
