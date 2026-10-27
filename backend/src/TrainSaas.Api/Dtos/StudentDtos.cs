namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/students {name,phone,gender?,birthday?,assignedSalesId?,notes?}。
/// phone 须匹配 ^1\d{10}$；assignedSalesId（若提供）必须是本机构在职 Sales；
/// Sales 角色创建时归属强制为自己（提供的值被忽略）。学员手机号不做唯一性
/// 检查（同机构同号多孩合法，Review Focus 4）。
/// </summary>
public record CreateStudentRequest(
    string Name,
    string Phone,
    string? Gender,
    DateOnly? Birthday,
    int? AssignedSalesId,
    string? Notes);

/// <summary>PUT /api/org/students/{id}——仅更新非 null 字段；字段级权限按角色在服务层裁决。</summary>
public record UpdateStudentRequest(
    string? Name,
    string? Phone,
    string? Gender,
    DateOnly? Birthday,
    int? AssignedSalesId,
    string? Notes);

/// <summary>
/// POST 201 响应与详情聚合中的学员主体：
/// {id,name,phone,gender,birthday,assignedSalesId,notes,isActive,createdAt}。
/// </summary>
public record StudentResponse(
    int Id,
    string Name,
    string Phone,
    string? Gender,
    DateOnly? Birthday,
    int? AssignedSalesId,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt);

/// <summary>GET /api/org/students 列表项（assignedSalesName 为归属销售姓名，未归属为 null）。</summary>
public record StudentListItem(
    int Id,
    string Name,
    string Phone,
    string? Gender,
    DateOnly? Birthday,
    int? AssignedSalesId,
    string? AssignedSalesName,
    bool IsActive,
    DateTime CreatedAt);

/// <summary>
/// GET /api/org/students/{id} 详情聚合：学员 + 课时包 + 订单 + 最近 20 条课时
/// 流水 + 报班记录（后期任务会往这些表里写数据，这里直接读表）。
/// </summary>
public record StudentDetail(
    StudentResponse Student,
    List<StudentPackItem> Packs,
    List<StudentOrderItem> Orders,
    List<StudentConsumptionItem> RecentConsumptions,
    List<StudentEnrollmentItem> Enrollments);

/// <summary>学员课时包：{productId,productName,total,remaining,status}。</summary>
public record StudentPackItem(
    int ProductId,
    string? ProductName,
    int Total,
    int Remaining,
    string Status);

/// <summary>订单摘要：{id,orderNo,productId,productName,amount,status,createdAt}。</summary>
public record StudentOrderItem(
    int Id,
    string OrderNo,
    int ProductId,
    string? ProductName,
    decimal Amount,
    string Status,
    DateTime CreatedAt);

/// <summary>课时流水（最近 20 条）：{id,kind,direction,productName?,createdAt}。</summary>
public record StudentConsumptionItem(
    int Id,
    string Kind,
    int Direction,
    string? ProductName,
    DateTime CreatedAt);

/// <summary>报班记录：{templateId,dayOfWeek,startTime,endTime,teacherName,status}。</summary>
public record StudentEnrollmentItem(
    int TemplateId,
    string DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? TeacherName,
    string Status);
