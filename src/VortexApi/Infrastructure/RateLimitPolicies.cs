using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace VortexApi;

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    /// <summary>Límite por IP para endpoints de autenticación (mitiga fuerza bruta y relleno de credenciales).</summary>
    public static void Configure(RateLimiterOptions options, int permitsPerMinute)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }
}
