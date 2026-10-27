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
}
