using System.Text.Json;
using VortexApi.Client.Sessions;

namespace VortexApi.Client.Sync;

public sealed class VortexSyncOptions
{
    public int PageSize { get; init; } = 200;

    /// <summary>Recibir también los eventos que publicó este mismo sistema (por defecto no).</summary>
    public bool IncludeOwnEvents { get; init; }
}

/// <summary>Resultado de una pasada de sincronización.</summary>
public sealed record VortexSyncResult(
    int PushedEvents,
    int PushedMasterWrites,
    int Conflicts,
    int Failed,
    int PulledChanges,
    int PulledEvents,
    int PendingOutbox,
    bool Completed,
    string? InterruptedBy);

public enum VortexConflictResolution
{
    /// <summary>Descartar el cambio local; la siguiente lectura del feed trae la versión del hub (por defecto).</summary>
    DiscardLocal,

    /// <summary>Reenviar el cambio local sin control de versión: pisa lo que haya en el hub.</summary>
    OverwriteServer,
}

/// <summary>Un cambio local que llegó tarde: otro sistema modificó el registro antes.</summary>
public sealed record VortexConflict(VortexOutboxEntry Local, JsonElement Server);

/// <summary>Un handler de la app falló al aplicar un cambio; el cursor queda en el último cambio aplicado.</summary>
public sealed class VortexSyncException(string stream, long sequence, Exception inner)
    : Exception($"Error al aplicar el cambio {sequence} del feed '{stream}': {inner.Message}", inner)
{
    public string Stream { get; } = stream;
    public long Sequence { get; } = sequence;
}

/// <summary>
/// Sincronización offline-first de una app Vortex con el hub.
/// <list type="number">
/// <item><b>Sube</b> la cola local (outbox) en el orden en que se encoló: datos maestros y eventos creados sin
/// conexión. El orden importa: un cliente creado offline llega antes que la venta que lo referencia.</item>
/// <item><b>Baja</b> los feeds de datos maestros y de eventos desde el último cursor y llama a los handlers.</item>
/// </list>
/// Los handlers deben ser idempotentes (aplicar por Id y versión): si la app se cierra a mitad de una página, al
/// reanudar se repiten los cambios posteriores al último cursor guardado.
/// </summary>
public sealed class VortexSync(
    VortexApiClient client, VortexSessionManager session, IVortexSyncStore store,
    VortexSyncOptions? options = null, TimeProvider? clock = null)
{
    public const string MasterStream = "master";
    public const string EventStream = "events";
    private const int PushBatchSize = 100;

    private readonly VortexSyncOptions _options = options ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Dictionary<string, List<Func<VortexMasterChange, CancellationToken, Task>>> _masterHandlers = [];
    private readonly List<(string Pattern, Func<VortexEvent, CancellationToken, Task> Handler)> _eventHandlers = [];
    private Func<VortexConflict, CancellationToken, Task<VortexConflictResolution>> _onConflict =
        (_, _) => Task.FromResult(VortexConflictResolution.DiscardLocal);
    private Func<VortexFailedEntry, CancellationToken, Task>? _onFailed;

    // ---------- Registro de handlers ----------

    public VortexSync OnMasterChange(string entityType, Func<VortexMasterChange, CancellationToken, Task> handler)
    {
        if (!_masterHandlers.TryGetValue(entityType, out var list))
            _masterHandlers[entityType] = list = [];
        list.Add(handler);
        return this;
    }

    public VortexSync OnParties(Func<VortexMasterChange<VortexParty>, CancellationToken, Task> handler) => Typed("party", handler);
    public VortexSync OnItems(Func<VortexMasterChange<VortexItem>, CancellationToken, Task> handler) => Typed("item", handler);
    public VortexSync OnSites(Func<VortexMasterChange<VortexSite>, CancellationToken, Task> handler) => Typed("site", handler);
    public VortexSync OnCompany(Func<VortexMasterChange<VortexCompany>, CancellationToken, Task> handler) => Typed("company", handler);

    /// <param name="pattern">Tipo exacto (<c>sale.completed</c>), prefijo (<c>sale.*</c>) o <c>*</c>.</param>
    public VortexSync OnEvent(string pattern, Func<VortexEvent, CancellationToken, Task> handler)
    {
        _eventHandlers.Add((pattern, handler));
        return this;
    }

    /// <summary>Decide qué hacer cuando un cambio local choca con uno más reciente del hub.</summary>
    public VortexSync OnConflict(Func<VortexConflict, CancellationToken, Task<VortexConflictResolution>> handler)
    {
        _onConflict = handler;
        return this;
    }

    /// <summary>Aviso cuando el hub rechaza una operación de forma definitiva (queda en <see cref="IVortexSyncStore.GetFailedAsync"/>).</summary>
    public VortexSync OnPushFailed(Func<VortexFailedEntry, CancellationToken, Task> handler)
    {
        _onFailed = handler;
        return this;
    }

    // ---------- Cola local ----------

    /// <summary>Encola un evento para publicarlo en la próxima sincronización. Devuelve su Id.</summary>
    public async Task<Guid> EnqueueEventAsync(string type, object data, string? subjectType = null, string? subjectId = null,
        Guid? id = null, CancellationToken ct = default)
    {
        var entry = new VortexOutboxEntry(id ?? Guid.NewGuid(), VortexOutboxKind.Event, _clock.GetUtcNow(), EventType: type,
            Payload: JsonSerializer.SerializeToElement(data, VortexJson.Options), SubjectType: subjectType, SubjectId: subjectId);
        await store.EnqueueAsync(entry, ct);
        return entry.Id;
    }

    /// <summary>
    /// Encola el alta o actualización de un dato maestro con un Id generado por la app (p. ej. un cliente creado sin
    /// conexión). Indique <paramref name="expectedVersion"/> al editar un registro existente para detectar conflictos.
    /// </summary>
    public Task EnqueueUpsertAsync(string resource, Guid id, object request, int? expectedVersion = null, CancellationToken ct = default)
    {
        VortexMasterResource.EntityTypeOf(resource);
        return store.EnqueueAsync(new VortexOutboxEntry(Guid.NewGuid(), VortexOutboxKind.MasterUpsert, _clock.GetUtcNow(),
            Resource: resource, EntityId: id, ExpectedVersion: expectedVersion,
            Payload: JsonSerializer.SerializeToElement(request, VortexJson.Options)), ct);
    }

    public Task EnqueueDeleteAsync(string resource, Guid id, int? expectedVersion = null, CancellationToken ct = default)
    {
        VortexMasterResource.EntityTypeOf(resource);
        return store.EnqueueAsync(new VortexOutboxEntry(Guid.NewGuid(), VortexOutboxKind.MasterDelete, _clock.GetUtcNow(),
            Resource: resource, EntityId: id, ExpectedVersion: expectedVersion), ct);
    }

    // ---------- Sincronización ----------

    /// <summary>
    /// Sube la cola y baja los feeds. Sin conexión (o con el hub caído) devuelve <c>Completed = false</c> sin perder
    /// nada; si la sesión terminó lanza <see cref="VortexApiException"/>; si un handler falla, <see cref="VortexSyncException"/>.
    /// </summary>
    public async Task<VortexSyncResult> SyncAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        var r = new Counters();
        try
        {
            await PushAsync(r, ct);
            await PullMasterAsync(r, ct);
            await PullEventsAsync(r, ct);
            return await ResultAsync(r, completed: true, null, ct);
        }
        catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
        {
            return await ResultAsync(r, completed: false, ex.Message, ct);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Sincroniza cada <paramref name="interval"/> hasta que se cancele. Los cortes de red no detienen el ciclo;
    /// termina si la sesión deja de ser válida.
    /// </summary>
    public async Task RunPeriodicAsync(TimeSpan interval, Action<VortexSyncResult>? onSynced = null,
        Action<Exception>? onError = null, CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                onSynced?.Invoke(await SyncAsync(ct));
            }
            catch (VortexSyncException ex)
            {
                onError?.Invoke(ex);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PushAsync(Counters r, CancellationToken ct)
    {
        while (true)
        {
            var batch = await store.PeekAsync(PushBatchSize, ct);
            if (batch.Count == 0)
                return;

            for (var i = 0; i < batch.Count;)
            {
                if (batch[i].Kind == VortexOutboxKind.Event)
                {
                    var events = batch.Skip(i).TakeWhile(e => e.Kind == VortexOutboxKind.Event).ToList();
                    await PushEventsAsync(events, r, ct);
                    i += events.Count;
                }
                else
                {
                    await PushMasterAsync(batch[i], r, ct);
                    i++;
                }
            }
        }
    }

    private async Task PushEventsAsync(List<VortexOutboxEntry> entries, Counters r, CancellationToken ct)
    {
        var toPublish = entries.Select(e => new VortexEventToPublish(e.Id, e.EventType!, e.Payload!.Value, e.CreatedAt,
            e.SubjectType, e.SubjectId)).ToList();
        var results = await session.WithAccessTokenAsync(token => client.PublishEventsAsync(token, toPublish, ct), ct);

        foreach (var (entry, result) in entries.Zip(results))
        {
            if (result.ErrorCode is null)
            {
                await store.RemoveAsync(entry.Id, ct);
                r.PushedEvents++;
            }
            else
            {
                await FailAsync(entry, result.ErrorCode, result.Error ?? result.ErrorCode, r, ct);
            }
        }
    }

    private async Task PushMasterAsync(VortexOutboxEntry entry, Counters r, CancellationToken ct)
    {
        try
        {
            await SendMasterAsync(entry, entry.ExpectedVersion, ct);
            await store.RemoveAsync(entry.Id, ct);
            r.PushedMasterWrites++;
        }
        catch (VortexApiException ex) when (ex.StatusCode == 412)
        {
            r.Conflicts++;
            var server = await session.WithAccessTokenAsync(token => client.GetMasterAsync(token, entry.Resource!, entry.EntityId!.Value, ct), ct);
            if (await _onConflict(new VortexConflict(entry, server), ct) == VortexConflictResolution.OverwriteServer)
            {
                await SendMasterAsync(entry, expectedVersion: null, ct);
                r.PushedMasterWrites++;
            }
            await store.RemoveAsync(entry.Id, ct);
        }
        catch (VortexApiException ex) when (ex.StatusCode is 400 or 404 or 409 or 422)
        {
            // Borrar algo que ya no existe no es un error.
            if (entry.Kind == VortexOutboxKind.MasterDelete && ex.StatusCode == 404)
                await store.RemoveAsync(entry.Id, ct);
            else
                await FailAsync(entry, ex.Code, ex.Message, r, ct);
        }
    }

    private Task SendMasterAsync(VortexOutboxEntry entry, int? expectedVersion, CancellationToken ct) =>
        session.WithAccessTokenAsync<object?>(async token =>
        {
            if (entry.Kind == VortexOutboxKind.MasterDelete)
                await client.DeleteMasterAsync(token, entry.Resource!, entry.EntityId!.Value, expectedVersion, ct);
            else
                await client.PutMasterAsync(token, entry.Resource!, entry.EntityId!.Value, entry.Payload!.Value, expectedVersion, ct);
            return null;
        }, ct);

    private async Task FailAsync(VortexOutboxEntry entry, string code, string error, Counters r, CancellationToken ct)
    {
        var at = _clock.GetUtcNow();
        await store.FailAsync(entry.Id, code, error, at, ct);
        r.Failed++;
        if (_onFailed is not null)
            await _onFailed(new VortexFailedEntry(entry, code, error, at), ct);
    }

    private async Task PullMasterAsync(Counters r, CancellationToken ct)
    {
        if (_masterHandlers.Count == 0)
            return;

        var cursor = await store.GetCursorAsync(MasterStream, ct);
        var types = _masterHandlers.Keys.ToList();
        while (true)
        {
            var page = await session.WithAccessTokenAsync(
                token => client.GetMasterChangesAsync(token, cursor, types, _options.PageSize, ct), ct);
            foreach (var change in page.Changes)
            {
                await ApplyAsync(MasterStream, change.Sequence, cursor, async () =>
                {
                    foreach (var handler in _masterHandlers.GetValueOrDefault(change.EntityType) ?? [])
                        await handler(change, ct);
                }, ct);
                cursor = change.Sequence;
                r.PulledChanges++;
            }

            cursor = page.NextCursor;
            await store.SaveCursorAsync(MasterStream, cursor, ct);
            if (!page.HasMore)
                return;
        }
    }

    private async Task PullEventsAsync(Counters r, CancellationToken ct)
    {
        if (_eventHandlers.Count == 0)
            return;

        var cursor = await store.GetCursorAsync(EventStream, ct);
        var patterns = _eventHandlers.Select(h => h.Pattern).Distinct().ToList();
        var exclude = _options.IncludeOwnEvents ? null : client.ProductCode;
        while (true)
        {
            var page = await session.WithAccessTokenAsync(
                token => client.GetEventsAsync(token, cursor, patterns, exclude, _options.PageSize, ct), ct);
            foreach (var e in page.Events)
            {
                await ApplyAsync(EventStream, e.Sequence, cursor, async () =>
                {
                    foreach (var (pattern, handler) in _eventHandlers.Where(h => Matches(h.Pattern, e.Type)))
                        await handler(e, ct);
                }, ct);
                cursor = e.Sequence;
                r.PulledEvents++;
            }

            cursor = page.NextCursor;
            await store.SaveCursorAsync(EventStream, cursor, ct);
            if (!page.HasMore)
                return;
        }
    }

    /// <summary>Si el handler falla, guarda el cursor en el último cambio aplicado para reanudar desde el que falló.</summary>
    private async Task ApplyAsync(string stream, long sequence, long lastApplied, Func<Task> apply, CancellationToken ct)
    {
        try
        {
            await apply();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await store.SaveCursorAsync(stream, lastApplied, ct);
            throw new VortexSyncException(stream, sequence, ex);
        }
    }

    private VortexSync Typed<T>(string entityType, Func<VortexMasterChange<T>, CancellationToken, Task> handler) =>
        OnMasterChange(entityType, (c, ct) => handler(
            new VortexMasterChange<T>(c.Sequence, c.EntityId, c.Operation, c.Version, c.ChangedAt, c.Source, c.DataAs<T>()), ct));

    /// <summary>Misma regla que el hub: exacto, prefijo terminado en <c>.*</c> o <c>*</c>.</summary>
    public static bool Matches(string pattern, string type) =>
        pattern == "*" || (pattern.EndsWith(".*") ? type.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == type);

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException ||
        ex is VortexApiException { StatusCode: >= 500 or 429 };

    private async Task<VortexSyncResult> ResultAsync(Counters r, bool completed, string? interruptedBy, CancellationToken ct) =>
        new(r.PushedEvents, r.PushedMasterWrites, r.Conflicts, r.Failed, r.PulledChanges, r.PulledEvents,
            (await store.PeekAsync(int.MaxValue, ct)).Count, completed, interruptedBy);

    private sealed class Counters
    {
        public int PushedEvents, PushedMasterWrites, Conflicts, Failed, PulledChanges, PulledEvents;
    }
}
