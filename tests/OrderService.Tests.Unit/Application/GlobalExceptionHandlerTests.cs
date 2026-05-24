using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NextAurora.ServiceDefaults;
using NSubstitute;

namespace OrderService.Tests.Unit.Application;

/// <summary>
/// Tests for <see cref="GlobalExceptionHandler"/> — specifically the recent change
/// that switched the response <c>traceId</c> from <c>Activity.Current?.Id</c> (full
/// W3C traceparent with span ID) to <c>Activity.Current?.TraceId.ToString()</c>
/// (trace component only). The span-ID leak is a CWE-200 information disclosure
/// of server-side handler call structure; tests pin the correct shape.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private readonly ILogger<GlobalExceptionHandler> _logger =
        Substitute.For<ILogger<GlobalExceptionHandler>>();

    [Fact]
    public async Task TryHandleAsync_WhenActivityIsActive_ReturnsTraceIdOnly_NotFullActivityId()
    {
        using var activity = new Activity("test");
        activity.Start();
        var (sut, httpContext, responseStream) = BuildSut();

        var handled = await sut.TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeTrue();
        var traceIdInResponse = await ExtractTraceIdAsync(responseStream);

        // The response traceId must be the trace component only (32 hex chars), NOT
        // Activity.Id which is the full W3C traceparent "00-<trace>-<span>-<flags>"
        // and leaks span information.
        traceIdInResponse.Should().Be(activity.TraceId.ToString());
        traceIdInResponse.Should().NotBe(activity.Id);
        traceIdInResponse.Should().NotContain("-",
            "the W3C traceparent format uses hyphens to separate version/trace/span/flags; the bare trace component has none");
    }

    [Fact]
    public async Task TryHandleAsync_WhenNoActivity_FallsBackToHttpContextTraceIdentifier()
    {
        // Belt-and-suspenders: ensure no Activity is leaking from a previous test.
        // Activity.Current is an AsyncLocal; in xunit a class's tests run sequentially
        // so the `using` in the prior test should have stopped its activity, but
        // assert here to be explicit.
        Activity.Current.Should().BeNull();

        var (sut, httpContext, responseStream) = BuildSut();
        httpContext.TraceIdentifier = "fallback-trace-from-aspnetcore";

        await sut.TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        var traceIdInResponse = await ExtractTraceIdAsync(responseStream);
        traceIdInResponse.Should().Be("fallback-trace-from-aspnetcore");
    }

    [Fact]
    public async Task TryHandleAsync_AlwaysReturnsProblemDetailsWithGenericDetail_NeverExceptionMessage()
    {
        // Defense against information disclosure: the response must never include the
        // raw exception message (which can leak SQL fragments, file paths, stack
        // traces, etc.). Per CLAUDE.md "Security Requirements — Error Handling".
        var (sut, httpContext, responseStream) = BuildSut();
        var sensitiveException = new InvalidOperationException(
            "SELECT * FROM Users WHERE Password = 'super-secret-leaked-via-error'");

        await sut.TryHandleAsync(httpContext, sensitiveException, CancellationToken.None);

        responseStream.Position = 0;
        using var reader = new StreamReader(responseStream);
        var body = await reader.ReadToEndAsync();
        body.Should().NotContain("super-secret-leaked-via-error");
        body.Should().NotContain("SELECT * FROM");
    }

    private (GlobalExceptionHandler sut, DefaultHttpContext context, MemoryStream responseStream) BuildSut()
    {
        var sut = new GlobalExceptionHandler(_logger);
        var httpContext = new DefaultHttpContext();
        var responseStream = new MemoryStream();
        httpContext.Response.Body = responseStream;
        return (sut, httpContext, responseStream);
    }

    private static async Task<string?> ExtractTraceIdAsync(MemoryStream responseStream)
    {
        responseStream.Position = 0;
        using var reader = new StreamReader(responseStream);
        var body = await reader.ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        return problem.GetProperty("traceId").GetString();
    }
}
