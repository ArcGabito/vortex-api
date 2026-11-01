namespace VortexApi.Client.Sessions;

/// <summary>Sesión guardada en el equipo: tokens y llaves públicas para validar sin conexión.</summary>
public sealed record VortexStoredSession(VortexSession Session, string? SigningKeysJson);

/// <summary>
/// Dónde guarda la app la sesión. En MAUI, implementarlo con <c>SecureStorage</c> (serializando con
/// <see cref="VortexJson.Options"/>); nunca en archivos planos ni en preferencias.
/// </summary>
public interface ISessionStore
{
    Task<VortexStoredSession?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(VortexStoredSession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private VortexStoredSession? _session;

    public Task<VortexStoredSession?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_session);

    public Task SaveAsync(VortexStoredSession session, CancellationToken ct = default)
    {
        _session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _session = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Mantiene la sesión del usuario: renueva el access token antes de que venza y la persiste.
/// <para>
/// La renovación es de <b>un solo vuelo</b>: aunque muchas llamadas pidan token a la vez, se hace un único refresh.
/// Es importante porque el hub rota el refresh token y, si detecta que uno viejo se reutiliza, cierra la sesión.
/// </para>
/// </summary>
public sealed class VortexSessionManager(
    VortexApiClient client, ISessionStore store, string deviceId, TimeProvider? clock = null)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private VortexStoredSession? _current;
    private bool _loaded;

    public string DeviceId => deviceId;

    /// <summary>Se dispara cuando la sesión deja de ser válida (usuario desactivado, acceso retirado, sesión revocada…).</summary>
    public event EventHandler<VortexApiException>? SessionEnded;

    public async Task<VortexSession?> GetSessionAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _current?.Session;
    }

    public async Task<VortexSession> LoginAsync(string email, string password, string? deviceName = null, string? platform = null,
        CancellationToken ct = default)
    {
        var session = await client.LoginAsync(email, password, deviceId, deviceName, platform, ct);
        string? keys = null;
        try
        {
            keys = await client.GetSigningKeysAsync(ct);
        }
        catch (HttpRequestException)
        {
        }

        await _gate.WaitAsync(ct);
        try
        {
            _current = new VortexStoredSession(session, keys);
            _loaded = true;
            await store.SaveAsync(_current, ct);
        }
        finally
        {
            _gate.Release();
        }
        return session;
    }

    /// <summary>Access token vigente, renovándolo si está por vencer. Lanza <see cref="VortexApiException"/> si la sesión terminó.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var current = _current ?? throw NotSignedIn();
        if (!NeedsRefresh(current.Session))
            return current.Session.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            // Otra llamada pudo haber renovado mientras esperábamos.
            current = _current ?? throw NotSignedIn();
            if (!NeedsRefresh(current.Session))
                return current.Session.AccessToken;

            VortexSession refreshed;
            try
            {
                refreshed = await client.RefreshAsync(current.Session.RefreshToken, deviceId, ct);
            }
            catch (VortexApiException ex) when (ex.StatusCode is 401 or 403)
            {
                await EndSessionAsync(ex, ct);
                throw;
            }

            _current = current with { Session = refreshed };
            await store.SaveAsync(_current, ct);
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ejecuta una llamada con el token vigente. Si el hub responde 401 (p. ej. el token fue revocado), fuerza una
    /// renovación y reintenta una vez.
    /// </summary>
    public async Task<T> WithAccessTokenAsync<T>(Func<string, Task<T>> call, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        try
        {
            return await call(token);
        }
        catch (VortexApiException ex) when (ex.StatusCode == 401)
        {
            await ExpireAccessTokenAsync(token, ct);
            return await call(await GetAccessTokenAsync(ct));
        }
    }

    /// <summary>Heartbeat: confirma el acceso y guarda el offline token renovado.</summary>
    public async Task<VortexHeartbeat> HeartbeatAsync(CancellationToken ct = default)
    {
        var heartbeat = await WithAccessTokenAsync(token => client.HeartbeatAsync(token, ct), ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_current is not null)
            {
                _current = _current with
                {
                    Session = _current.Session with
                    {
                        OfflineToken = heartbeat.OfflineToken,
                        OfflineTokenExpiresAt = heartbeat.OfflineTokenExpiresAt,
                        Access = heartbeat.Access,
                    },
                };
                await store.SaveAsync(_current, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
        return heartbeat;
    }

    /// <summary>Sin conexión: valida el offline token guardado con las llaves públicas guardadas.</summary>
    public async Task<OfflineValidationResult> ValidateOfflineAsync(string issuer, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (_current is not { SigningKeysJson: { } keys } stored)
            return new OfflineValidationResult(false, null, "No hay sesión ni llaves guardadas.");
        return await new OfflineAccessValidator(keys, issuer)
            .ValidateAsync(stored.Session.OfflineToken, client.ProductCode, deviceId, _clock);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var refresh = _current?.Session.RefreshToken;
        await _gate.WaitAsync(ct);
        try
        {
            _current = null;
            await store.ClearAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        if (refresh is not null)
        {
            try
            {
                await client.LogoutAsync(refresh, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or VortexApiException)
            {
                // Sin red, la sesión expira sola en el hub; localmente ya se borró.
            }
        }
    }

    private bool NeedsRefresh(VortexSession session) => _clock.GetUtcNow() + RefreshMargin >= session.AccessTokenExpiresAt;

    private async Task ExpireAccessTokenAsync(string rejectedToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_current?.Session.AccessToken == rejectedToken)
                _current = _current with { Session = _current.Session with { AccessTokenExpiresAt = DateTimeOffset.MinValue } };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndSessionAsync(VortexApiException reason, CancellationToken ct)
    {
        _current = null;
        await store.ClearAsync(ct);
        SessionEnded?.Invoke(this, reason);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                _current = await store.LoadAsync(ct);
                _loaded = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static VortexApiException NotSignedIn() => new(401, "not_signed_in", "No hay una sesión iniciada.");
}
