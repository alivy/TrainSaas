namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/consumptions/walk-in {studentId, sessionPackId}：到店核销。
/// 包须属于该学员、Active、有余量；实际扣减按包的 ProductId 走
/// IDeductionService（同产品最早先扣），流水行不带课次 id。
/// </summary>
public record WalkInRequest(int StudentId, int SessionPackId);

/// <summary>
/// 到店核销 200 响应：{id, remainingSessions}——新流水 id 与核销后该包余量。
/// </summary>
public record WalkInResult(int Id, int RemainingSessions);

/// <summary>
/// POST /api/org/consumptions/{id}/reverse {reason}：撤销冲正。reason 必填、
/// trim 后非空且 ≤200 字（服务层校验，400）。
/// </summary>
public record ReverseRequest(string? Reason);

/// <summary>撤销冲正 201 响应：{id}——新写入的镜像行（Direction=-1）id。</summary>
public record ReverseResult(int Id);

/// <summary>
/// GET /api/org/consumptions 列表项：
/// {id,studentId,studentName,productName,kind,direction,reason,operatorName,
/// classSessionStartAt?,createdAt}。kind 为枚举名（RollCall/WalkIn/Reversal）；
/// 名称批量解析（无 N+1）；WalkIn 等无课次行 classSessionStartAt 为 null。
/// </summary>
public record ConsumptionItem(
    int Id,
    int StudentId,
    string? StudentName,
    string? ProductName,
    string Kind,
    int Direction,
    string? Reason,
    string? OperatorName,
    DateTime? ClassSessionStartAt,
    DateTime CreatedAt);
