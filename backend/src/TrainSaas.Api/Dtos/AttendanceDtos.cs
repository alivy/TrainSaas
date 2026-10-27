using System.Text.Json;
using System.Text.Json.Serialization;
using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/sessions/{id}/attendance 请求体：{marks:[{studentId,mark}]}。
/// marks 缺失/null/空数组一律 400（点名名单不能为空）。
/// </summary>
public record AttendanceRequest(List<AttendanceMarkItem>? Marks);

/// <summary>
/// 单项点名请求：{studentId, mark}。mark 是区分大小写的字符串枚举
/// "Present"/"Leave"/"Absent"（严格匹配，见 <see cref="AttendanceMarkJsonConverter"/>）；
/// studentId 缺失按逐项失败处理（学员 0 不在名单），mark 缺失直接 400。
/// </summary>
public record AttendanceMarkItem(
    int StudentId,
    [property: JsonRequired]
    [property: JsonConverter(typeof(AttendanceMarkJsonConverter))]
    AttendanceMark Mark);

/// <summary>
/// AttendanceMark 的严格字符串 JSON 绑定：仅接受 "Present"/"Leave"/"Absent"
/// （区分大小写——"present" 等变体一律拒绝，防止大小写宽松绑定悄悄改变
/// 账务语义）；非字符串、null 或未知值抛 <see cref="JsonException"/>，由
/// [ApiController] 模型绑定转为 400，绝不 500。
/// </summary>
public sealed class AttendanceMarkJsonConverter : JsonConverter<AttendanceMark>
{
    public override AttendanceMark Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "Present" => AttendanceMark.Present,
                "Leave" => AttendanceMark.Leave,
                "Absent" => AttendanceMark.Absent,
                _ => throw new JsonException("mark 必须是 Present/Leave/Absent 之一"),
            }
            : throw new JsonException("mark 必须是 Present/Leave/Absent 之一");

    public override void Write(
        Utf8JsonWriter writer, AttendanceMark value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// 服务层单项点名输入（Task 14 复用，签名一致）：{studentId, mark}。
/// </summary>
public record MarkItem(int StudentId, AttendanceMark Mark);

/// <summary>
/// 单项点名结果：{studentId, ok, error?}。ok=false 时 error 为用户可见文案
/// （"不在名单且无可用课时包"/"无可用课时包"）。
/// </summary>
public record MarkResult(int StudentId, bool Ok, string? Error);

/// <summary>
/// POST /api/org/sessions/{id}/attendance 的 200 响应：{results:[...]}——
/// 逐项处理、部分成功：单项失败只影响该学员，绝不中断同批其他学员。
/// </summary>
public record AttendanceResult(List<MarkResult> Results);
