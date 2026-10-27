namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/staff {name,phone,role}——初始密码 123456，phone 全局唯一。
/// role 以字符串传输（Admin/Sales/Teacher/FrontDesk），由 StaffService 安全解析，
/// 非法值抛 <see cref="Errors.BusinessRuleException"/>（400）。
/// </summary>
public record CreateStaffRequest(string Name, string Phone, string Role);

/// <summary>POST /api/org/staff → 201 {id,name,phone,role,isActive}。</summary>
public record StaffCreatedResponse(int Id, string Name, string Phone, string Role, bool IsActive);

/// <summary>PUT /api/org/staff/{id}——仅更新非 null 字段（name/role）。</summary>
public record UpdateStaffRequest(string? Name, string? Role);

/// <summary>GET /api/org/staff 列表项（role 为 "Admin"/"Sales"/"Teacher"/"FrontDesk"）。</summary>
public record StaffListItem(
    int Id,
    string Name,
    string Phone,
    string Role,
    bool IsActive,
    DateTime CreatedAt);
