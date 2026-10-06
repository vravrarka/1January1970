using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Inventory.Shared.Http;

/// <summary>
/// Передаёт токен текущего пользователя при вызове соседнего сервиса.
/// Благодаря этому вызываемый сервис сам проверяет роль инициатора операции
/// (D-01), а не доверяет вызывающему сервису «на слово».
/// </summary>
public sealed class ForwardBearerTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header) && AuthenticationHeaderValue.TryParse(header, out var value))
        {
            request.Headers.Authorization = value;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
