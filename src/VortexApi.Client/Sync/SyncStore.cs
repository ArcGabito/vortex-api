using System.Text.Json;

namespace VortexApi.Client.Sync;

public enum VortexOutboxKind
{
    /// <summary>Evento de integración a publicar.</summary>
    Event,

    /// <summary>Alta o actualización de un dato maestro (PUT con Id de la app).</summary>
    MasterUpsert,

    /// <summary>Borrado lógico de un dato maestro.</summary>
    MasterDelete,
}

/// <summary>Operación pendiente de subir al hub. Se suben en el orden en que se encolaron.</summary>
public sealed record VortexOutboxEntry(
    Guid Id,
    VortexOutboxKind Kind,
    DateTimeOffset CreatedAt,
    string? EventType = null,
    string? Resource = null,
    Guid? EntityId = null,
    int? ExpectedVersion = null,
    JsonElement? Payload = null,
    string? SubjectType = null,
    string? SubjectId = null);

/// <summary>Operación que el hub rechazó de forma definitiva (validación, duplicado). No bloquea la cola.</summary>
public sealed record VortexFailedEntry(VortexOutboxEntry Entry, string ErrorCode, string Error, DateTimeOffset FailedAt);

/// <summary>
/// Persistencia local de la sincronización: cursores de cada feed y la cola de salida (outbox).
/// Implemente esta interfaz sobre la base local de la app (p. ej. SQLite) o use <see cref="JsonFileSyncStore"/>.
/// </summary>
public interface IVortexSyncStore
{
    Task<long> GetCursorAsync(string stream, CancellationToken ct = default);
    Task SaveCursorAsync(string stream, long cursor, CancellationToken ct = default);

    Task EnqueueAsync(VortexOutboxEntry entry, CancellationToken ct = default);

    /// <summary>Primeras <paramref name="max"/> entradas pendientes, en orden de encolado.</summary>
    Task<IReadOnlyList<VortexOutboxEntry>> PeekAsync(int max, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Saca la entrada de la cola y la guarda como fallida.</summary>
    Task FailAsync(Guid id, string errorCode, string error, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<VortexFailedEntry>> GetFailedAsync(CancellationToken ct = default);
}

/// <summary>Estado serializable de los stores incluidos.</summary>
public sealed class VortexSyncState
{
    public Dictionary<string, long> Cursors { get; set; } = [];
    public List<VortexOutboxEntry> Outbox { get; set; } = [];
    public List<VortexFailedEntry> Failed { get; set; } = [];
}

/// <summary>Store en memoria: útil para pruebas; se pierde al cerrar la app.</summary>
public class InMemorySyncStore : IVortexSyncStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    protected VortexSyncState State { get; set; } = new();

    public Task<long> GetCursorAsync(string stream, CancellationToken ct = default) =>
        Locked(() => State.Cursors.GetValueOrDefault(stream), persist: false, ct);

    public Task SaveCursorAsync(string stream, long cursor, CancellationToken ct = default) =>
        Locked(() => State.Cursors[stream] = cursor, persist: true, ct);

    public Task EnqueueAsync(VortexOutboxEntry entry, CancellationToken ct = default) =>
        Locked(() => { State.Outbox.Add(entry); return 0; }, persist: true, ct);

    public Task<IReadOnlyList<VortexOutboxEntry>> PeekAsync(int max, CancellationToken ct = default) =>
        Locked(() => (IReadOnlyList<VortexOutboxEntry>)State.Outbox.Take(max).ToList(), persist: false, ct);

    public Task RemoveAsync(Guid id, CancellationToken ct = default) =>
        Locked(() => State.Outbox.RemoveAll(e => e.Id == id), persist: true, ct);

    public Task FailAsync(Guid id, string errorCode, string error, DateTimeOffset at, CancellationToken ct = default) =>
        Locked(() =>
        {
            var entry = State.Outbox.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                State.Outbox.Remove(entry);
                State.Failed.Add(new VortexFailedEntry(entry, errorCode, error, at));
            }
            return 0;
        }, persist: true, ct);

    public Task<IReadOnlyList<VortexFailedEntry>> GetFailedAsync(CancellationToken ct = default) =>
        Locked(() => (IReadOnlyList<VortexFailedEntry>)State.Failed.ToList(), persist: false, ct);

    /// <summary>Se llama después de cada cambio de estado (con el lock tomado).</summary>
    protected virtual Task PersistAsync(CancellationToken ct) => Task.CompletedTask;

    protected virtual Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    private bool _loaded;

    private async Task<T> Locked<T>(Func<T> action, bool persist, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                await LoadAsync(ct);
                _loaded = true;
            }
            var result = action();
            if (persist)
                await PersistAsync(ct);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Store en un archivo JSON (escritura atómica: archivo temporal + reemplazo). En MAUI use una ruta dentro de
/// <c>FileSystem.AppDataDirectory</c>. Para colas grandes, conviene una implementación sobre SQLite.
/// </summary>
public sealed class JsonFileSyncStore(string path) : InMemorySyncStore
{
    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return;
        await using var stream = File.OpenRead(path);
        State = await JsonSerializer.DeserializeAsync<VortexSyncState>(stream, VortexJson.Options, ct) ?? new VortexSyncState();
    }

    protected override async Task PersistAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, State, VortexJson.Options, ct);
        File.Move(temp, path, overwrite: true);
    }
}
