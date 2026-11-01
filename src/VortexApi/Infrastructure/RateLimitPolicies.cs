using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VortexApi.Security;

namespace VortexApi;

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    /// <param name="authPermitsPerMinute">Límite por IP para endpoints de autenticación (fuerza bruta, relleno de credenciales).</param>
    /// <param name="integrationPermitsPerMinute">Límite global por aplicación de integración, para que una integración mal hecha no sature el hub.</param>
    public static void Configure(RateLimiterOptions options, int authPermitsPerMinute, int integrationPermitsPerMinute)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            return ValueTask.CompletedTask;
        };

        options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Requiere que la autenticación corra antes (UseAuthentication antes de UseRateLimiter).
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.User.IntegrationClientId() is { } clientId
                ? RateLimitPartition.GetFixedWindowLimiter("integration:" + clientId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = integrationPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter("default"));
    }
}
