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

/// <summary>
/// GET /api/org/profile [StaffOnly] → {code,name,logoPath,introText,contactPhone,
/// address}：机构自己读自己的介绍页内容（code 只读，改编码是平台侧的事）。
/// </summary>
public record OrgProfileResponse(
    string Code,
    string Name,
    string? LogoPath,
    string? IntroText,
    string? ContactPhone,
    string? Address);

/// <summary>
/// PUT /api/org/profile [AdminOnly]——仅更新请求中非 null 的字段；name 若提供
/// 则必填非空且 ≤50 字（否则 400）。改动立刻反映在匿名公开页
/// GET /api/public/orgs/{code} 上。
/// </summary>
public record UpdateOrgProfileRequest(
    string? Name,
    string? LogoPath,
    string? IntroText,
    string? ContactPhone,
    string? Address);
