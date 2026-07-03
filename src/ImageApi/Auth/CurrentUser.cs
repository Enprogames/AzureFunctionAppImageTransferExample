using System.Security.Claims;
using System.Security.Cryptography;

namespace ImageApi.Auth;

public sealed record CurrentUser(
    string TenantId,
    string UserId,
    string DisplayName)
{
    public const string HttpContextItemKey = "ImageApi.CurrentUser";

    public string StableKey => $"{TenantId}:{UserId}";

    public string OwnerHash
    {
        get
        {
            var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StableKey));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public static CurrentUser FromHttpContext(HttpContext context)
    {
        if (context.Items[HttpContextItemKey] is CurrentUser developmentUser)
        {
            return developmentUser;
        }

        var principal = context.User;

        var tenantId =
            principal.FindFirstValue("tid") ??
            principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");

        var userId =
            principal.FindFirstValue("oid") ??
            principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        var displayName =
            principal.FindFirstValue("name") ??
            principal.FindFirstValue("preferred_username") ??
            "unknown";

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("Authenticated user is missing tenant or object ID claims.");
        }

        return new CurrentUser(tenantId, userId, displayName);
    }

    public static CurrentUser FromDevelopmentUser(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Development user name is required.", nameof(name));
        }

        var trimmed = name.Trim();
        return new CurrentUser("development", trimmed, trimmed);
    }
}
