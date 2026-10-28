using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TrainSaas.Api.Errors;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// Directly invokes <see cref="ExceptionHandlingMiddleware"/> with a throwing
/// <see cref="RequestDelegate"/> (no host). Pins the status-code mapping, that
/// domain exceptions expose their message as ProblemDetails Detail, and that
/// framework exceptions (e.g. bare <see cref="InvalidOperationException"/>) map
/// to 500 with a generic detail that does not leak the original message.
/// </summary>
public class ExceptionHandlingMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task<(int StatusCode, ProblemDetails Body)> InvokeAsync(Exception exception)
    {
        RequestDelegate next = _ => throw exception;
        var middleware = new ExceptionHandlingMiddleware(next, NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = JsonSerializer.Deserialize<ProblemDetails>(await reader.ReadToEndAsync(), JsonOptions)!;
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task NotFoundException_maps_to_404_with_message_as_detail()
    {
        var (statusCode, body) = await InvokeAsync(new NotFoundException("找不到学员"));
        Assert.Equal(404, statusCode);
        Assert.Equal("找不到学员", body.Detail);
    }

    [Fact]
    public async Task ConflictException_maps_to_409_with_message_as_detail()
    {
        var (statusCode, body) = await InvokeAsync(new ConflictException("手机号已被占用"));
        Assert.Equal(409, statusCode);
        Assert.Equal("手机号已被占用", body.Detail);
    }

    [Fact]
    public async Task ForbiddenActionException_maps_to_403_with_message_as_detail()
    {
        var (statusCode, body) = await InvokeAsync(new ForbiddenActionException("无权停用该员工"));
        Assert.Equal(403, statusCode);
        Assert.Equal("无权停用该员工", body.Detail);
    }

    [Fact]
    public async Task NoAvailablePackException_maps_to_400_with_message_as_detail()
    {
        var (statusCode, body) = await InvokeAsync(new NoAvailablePackException("没有可用的课时包"));
        Assert.Equal(400, statusCode);
        Assert.Equal("没有可用的课时包", body.Detail);
    }

    [Fact]
    public async Task BusinessRuleException_maps_to_400_with_message_as_detail()
    {
        var (statusCode, body) = await InvokeAsync(new BusinessRuleException("课时数必须大于 0"));
        Assert.Equal(400, statusCode);
        Assert.Equal("课时数必须大于 0", body.Detail);
    }

    [Fact]
    public async Task Bare_InvalidOperationException_maps_to_500_without_leaking_the_message()
    {
        var (statusCode, body) = await InvokeAsync(
            new InvalidOperationException("EF model internal state is broken"));
        Assert.Equal(500, statusCode);
        Assert.NotNull(body.Detail);
        Assert.DoesNotContain("EF model internal state is broken", body.Detail);
    }

    /// <summary>
    /// M6（最终审查）：客户端已断开（RequestAborted 已触发）时的
    /// OperationCanceledException 不是服务器错误——异常原样上抛，绝不记 500
    /// 也绝不写 500 响应（响应体保持未写）。
    /// </summary>
    [Fact]
    public async Task OperationCanceledException_on_aborted_request_is_rethrown_without_a_response()
    {
        RequestDelegate next = _ => throw new OperationCanceledException();
        var middleware = new ExceptionHandlingMiddleware(next, NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    /// <summary>M6 反向对照：无客户端断开的取消（如服务端超时令牌）仍是 500。</summary>
    [Fact]
    public async Task OperationCanceledException_without_client_abort_maps_to_500()
    {
        var (statusCode, body) = await InvokeAsync(new OperationCanceledException("server-side timeout"));
        Assert.Equal(500, statusCode);
        Assert.Equal("服务器内部错误", body.Detail);
    }
}
