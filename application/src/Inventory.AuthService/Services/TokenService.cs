using Inventory.AuthService.Data;
using Inventory.Shared.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.AuthService.Services;

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class TokenService(JwtSettings settings, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(settings.CreateKey(), SecurityAlgorithms.HmacSha256);

    public IssuedToken Issue(User user)
    {
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [InventoryClaims.UserId] = user.Id.ToString(),
                [InventoryClaims.Login] = user.Login,
                [InventoryClaims.FullName] = user.FullName,
                [InventoryClaims.Role] = user.Role,
                [InventoryClaims.TokenId] = Guid.NewGuid().ToString()
            }
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
