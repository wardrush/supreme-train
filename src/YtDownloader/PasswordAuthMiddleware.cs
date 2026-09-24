using System.Security.Cryptography;
using System.Text;

namespace YtDownloader;

/// <summary>
/// Guards /api/* with the shared password. Accepts the X-App-Password header or the app_password cookie.
/// Comparison is constant-time over SHA-256 digests so length differences leak nothing either.
/// The password value is never logged.
/// </summary>
public sealed class PasswordAuthMiddleware(RequestDelegate next, AppOptions options)
{
    public const string HeaderName = "X-App-Password";
    public const string CookieName = "app_password";

    private readonly byte[] _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Password));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresAuth(context.Request.Path))
        {
            await next(context);
            return;
        }

        string? supplied = context.Request.Headers[HeaderName];
        if (string.IsNullOrEmpty(supplied))
            supplied = context.Request.Cookies[CookieName];

        if (!IsMatch(supplied))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Wrong or missing password." });
            return;
        }

        await next(context);
    }

    internal static bool RequiresAuth(PathString path) => path.StartsWithSegments("/api");

    internal bool IsMatch(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied))
            return false;

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, _expectedHash);
    }
}
