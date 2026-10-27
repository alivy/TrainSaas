namespace TrainSaas.Api.Dtos;

/// <summary>登录请求/响应契约（平台超管、机构员工、家长）。JSON 序列化为 camelCase。</summary>

public record PlatformLoginRequest(string Username, string Password);

/// <summary>POST /api/platform/auth/login → {token,name}。</summary>
public record PlatformLoginResponse(string Token, string Name);

public record StaffLoginRequest(string Phone, string Password);

/// <summary>POST /api/org/auth/login → {token,name,role,institutionId,institutionName,institutionCode}。</summary>
public record StaffLoginResponse(
    string Token,
    string Name,
    string Role,
    int InstitutionId,
    string InstitutionName,
    string InstitutionCode);

/// <summary>GET /api/org/auth/me → {staffId,name,role,institutionId,institutionName,institutionCode}。</summary>
public record StaffMeResponse(
    int StaffId,
    string Name,
    string Role,
    int InstitutionId,
    string InstitutionName,
    string InstitutionCode);

public record ParentLoginRequest(string Phone, string Password);

/// <summary>POST /api/parent/auth/login → {token,phone}。</summary>
public record ParentLoginResponse(string Token, string Phone);

/// <summary>POST /api/parent/change-password。新密码 ≥6 位。</summary>
public record ParentChangePasswordRequest(string OldPassword, string NewPassword);
