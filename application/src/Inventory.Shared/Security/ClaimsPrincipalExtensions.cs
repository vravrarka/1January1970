using System.Security.Claims;

namespace Inventory.Shared.Security;

/// <summary>
/// Личность и роль текущего пользователя определяются только из проверенного токена,
/// а не из параметров запроса (threat-model.md, раздел 8; D-03).
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(InventoryClaims.UserId);
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("В токене отсутствует корректный идентификатор пользователя.");
    }

    public static Guid? TryGetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(InventoryClaims.UserId), out var id) ? id : null;

    public static string GetLogin(this ClaimsPrincipal user) =>
        user.FindFirstValue(InventoryClaims.Login) ?? string.Empty;

    public static string GetFullName(this ClaimsPrincipal user) =>
        user.FindFirstValue(InventoryClaims.FullName) ?? user.GetLogin();

    public static string? GetRole(this ClaimsPrincipal user) =>
        user.FindFirstValue(InventoryClaims.Role);
}
