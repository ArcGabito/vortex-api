using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using VortexApi;
using VortexApi.Data;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<LockoutOptions>(config.GetSection(LockoutOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));

builder.Services.AddDbContext<VortexDbContext>(o =>
    o.UseSqlServer(config.GetConnectionString("Vortex"), sql => sql.EnableRetryOnFailure()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((o, tokens) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = tokens.ValidationParameters();
        o.Events = new JwtBearerEvents { OnTokenValidated = TokenRevalidator.OnTokenValidated };
    });
builder.Services.AddAuthorization(Policies.Configure);

builder.Services.AddRateLimiter(o =>
    RateLimitPolicies.Configure(o, config.GetValue("RateLimiting:AuthPermitsPerMinute", 20)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecurityTransformer>());
builder.Services.AddHealthChecks();

if (config.GetValue<bool>("ForwardedHeaders:Enabled"))
{
    // Detrás de Azure App Service / Front Door, para registrar la IP real del cliente.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
    await DbSeeder.SeedAsync(scope.ServiceProvider, seed, app.Logger);
}

if (config.GetValue<bool>("ForwardedHeaders:Enabled"))
    app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
