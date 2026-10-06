using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Shared.Http;

/// <summary>
/// Единый формат ошибок (RFC 7807 Problem Details) с машиночитаемым полем <c>code</c>.
/// </summary>
public static class ApiErrors
{
    public static IResult BadRequest(string code, string message) => Problem(StatusCodes.Status400BadRequest, code, message);

    public static IResult Forbidden(string code, string message) => Problem(StatusCodes.Status403Forbidden, code, message);

    public static IResult NotFound(string code, string message) => Problem(StatusCodes.Status404NotFound, code, message);

    public static IResult Conflict(string code, string message) => Problem(StatusCodes.Status409Conflict, code, message);

    public static IResult Unavailable(string code, string message) => Problem(StatusCodes.Status503ServiceUnavailable, code, message);

    public static IResult Problem(int status, string code, string message) =>
        Results.Problem(
            statusCode: status,
            title: message,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = message };
        problem.Extensions["code"] = code;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

/// <summary>Тело ошибки, получаемое от другого сервиса.</summary>
public sealed record RemoteProblem(string? Title, string? Code, int? Status);
