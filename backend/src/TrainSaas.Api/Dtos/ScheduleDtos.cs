namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/templates {productId,dayOfWeek,startTime,endTime,teacherId,
/// room?,capacity?}。startTime/endTime 为严格 "HH:mm" 字符串；dayOfWeek 为
/// 0-6（0=周日）；产品必须是本机构 Fixed 型；teacherId 必须是本机构在职的
/// Teacher/Admin。
/// </summary>
public record CreateTemplateRequest(
    int ProductId,
    int DayOfWeek,
    string StartTime,
    string EndTime,
    int TeacherId,
    string? Room,
    int? Capacity);

/// <summary>
/// PUT /api/org/templates/{id}——仅更新非 null 字段，非 null 值走与创建相同的
/// 校验（endTime&gt;startTime 以合并后的值判定）。修改模板只影响之后新生成的
/// 课次，已生成课次的教师/起止时间不变。
/// </summary>
public record UpdateTemplateRequest(
    int? ProductId,
    int? DayOfWeek,
    string? StartTime,
    string? EndTime,
    int? TeacherId,
    string? Room,
    int? Capacity);

/// <summary>
/// 模板 201 响应与列表项：
/// {id,productId,dayOfWeek,startTime,endTime,teacherId,room,capacity,isActive}。
/// </summary>
public record TemplateResponse(
    int Id,
    int ProductId,
    int DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int TeacherId,
    string? Room,
    int? Capacity,
    bool IsActive);

/// <summary>
/// GET /api/org/sessions 列表项：
/// {id,templateId,productId,productName,teacherId,teacherName,startAt,endAt,
/// status,room,enrolledCount}。room 从模板读时解析（ClassSession 不存教室）；
/// enrolledCount 为该模板在班（Enrolled）学员数。
/// </summary>
public record SessionListItem(
    int Id,
    int TemplateId,
    int ProductId,
    string? ProductName,
    int TeacherId,
    string? TeacherName,
    DateTime StartAt,
    DateTime EndAt,
    string Status,
    string? Room,
    int EnrolledCount);

/// <summary>课次详情内的模板快照信息（productId/productName 等模板当前值）。</summary>
public record SessionTemplateInfo(
    int ProductId,
    string? ProductName,
    int DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Room,
    int? Capacity,
    bool IsActive);

/// <summary>在班学员（roster）：{studentId,name,phone}——仅 Enrolled 状态。</summary>
public record SessionRosterItem(
    int StudentId,
    string Name,
    string Phone);

/// <summary>既有点名记录：{studentId,mark,operatorId,createdAt}。</summary>
public record SessionAttendanceItem(
    int StudentId,
    string Mark,
    int OperatorId,
    DateTime CreatedAt);

/// <summary>
/// GET /api/org/sessions/{id} 详情：课次字段 + 内嵌模板信息 + roster + attendance。
/// </summary>
public record SessionDetail(
    int Id,
    int TemplateId,
    int TeacherId,
    string? TeacherName,
    DateTime StartAt,
    DateTime EndAt,
    string Status,
    SessionTemplateInfo? Template,
    List<SessionRosterItem> Roster,
    List<SessionAttendanceItem> Attendance);
