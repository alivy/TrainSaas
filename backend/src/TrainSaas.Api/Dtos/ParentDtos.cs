namespace TrainSaas.Api.Dtos;

/// <summary>
/// GET /api/parent/children 行：{studentId,name,institutionId,institutionName,
/// institutionCode}。同手机号可跨机构有多个孩子（设计内的租户过滤豁免），
/// 按 institutionId、studentId 升序；仅 IsActive 的学员。
/// </summary>
public record ParentChildItem(
    int StudentId,
    string Name,
    int InstitutionId,
    string? InstitutionName,
    string? InstitutionCode);

/// <summary>
/// GET /api/parent/packs 行：{packId,productName,totalSessions,
/// remainingSessions,status,orderNo,purchasedAt}。含已作废（Voided）的包——
/// 家长看得见历史；status 为枚举名；purchasedAt = 课时包创建时刻（即订单确认
/// 时刻）；按 packId 降序（新买的在前）。
/// </summary>
public record ParentPackItem(
    int PackId,
    string? ProductName,
    int TotalSessions,
    int RemainingSessions,
    string Status,
    string? OrderNo,
    DateTime PurchasedAt);

/// <summary>
/// GET /api/parent/records 行：{id,kind,direction,productName,createdAt,reason,
/// classSessionStartAt?}。账本 append-only：冲正行如实出现（kind=Reversal、
/// direction=-1、reason=撤销原因），原扣减行不被改写。kind 为枚举名
/// （RollCall/WalkIn/Reversal）；随到随上等无课次的行 classSessionStartAt 为 null。
/// </summary>
public record ParentRecordItem(
    int Id,
    string Kind,
    int Direction,
    string? ProductName,
    DateTime CreatedAt,
    string? Reason,
    DateTime? ClassSessionStartAt);

/// <summary>
/// GET /api/parent/schedule 行：{startAt,endAt,productName,teacherName,room,
/// institutionName}。仅"在班（Enrolled）报名 + 启用（IsActive）模板"的未来
/// Scheduled 实例，按 startAt 升序；room 读时取自模板（ClassSession 不存教室，
/// 既有约定）。
/// </summary>
public record ParentScheduleItem(
    DateTime StartAt,
    DateTime EndAt,
    string? ProductName,
    string? TeacherName,
    string? Room,
    string? InstitutionName);
