namespace TrainSaas.Api.Errors;

/// <summary>资源不存在（HTTP 404）。</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>状态冲突，例如重复手机号、重复确认（HTTP 409）。</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>已认证但无权执行该操作（HTTP 403）。</summary>
public class ForbiddenActionException(string message) : Exception(message);

/// <summary>学员没有可扣减的课时包（HTTP 400）。</summary>
public class NoAvailablePackException(string message) : Exception(message);

/// <summary>
/// 业务规则违反（HTTP 400）。消息即用户可见文案；服务层校验失败（如课时数必须为正）
/// 一律抛此异常，绝不抛裸 <see cref="InvalidOperationException"/> —— 后者属于框架
/// 内部错误，会被映射为 500。
/// </summary>
public class BusinessRuleException(string message) : Exception(message);
