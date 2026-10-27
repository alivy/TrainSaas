namespace TrainSaas.Api.Dtos;

/// <summary>
/// GET /api/org/reports/sales 行：{staffId,staffName,orderCount,totalAmount}。
/// 口径：Status=Active 且 ConfirmedAt 落在 [from 当日零点, to 次日零点) 的订单，
/// 按 SalesId 分组；金额在 ToListAsync 之后内存 Sum（Global Constraints：
/// SQLite 的 decimal SQL 聚合不可靠）。窗口内零成交的销售不出行（不补零行）。
/// staffName 读时批量解析（缺失为 null）。
/// </summary>
public record SalesReportRow(int StaffId, string? StaffName, int OrderCount, decimal TotalAmount);

/// <summary>
/// GET /api/org/reports/teachers 行：{teacherId,teacherName,completedSessions,
/// presentMarks}。completedSessions = 窗口内（StartAt ∈ [from, to 次日零点)）
/// Status=Completed 的课次实例数；presentMarks = 课次落在同一窗口内的
/// Mark=Present 点名行数（Leave/Absent 不计）。两项皆空的教师不出行。
/// </summary>
public record TeacherReportRow(
    int TeacherId,
    string? TeacherName,
    int CompletedSessions,
    int PresentMarks);
