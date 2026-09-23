using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Ручное управление KV-кешем llama.cpp-сервера (b10353): GET /slots +
/// POST /slots/{id}?action=save|restore|erase (нужен флаг сервера --slot-save-path).
///
/// Зачем: при пиннинге слотов (SlotAllocation) сервер НЕ ходит в RAM prompt cache
/// (update_cache=false для явного id_slot) — пиннутый слот живёт в GPU
/// (--no-cache-idle-slots) и защищается только файловым якорем:
///   spawn субагента → save слота main → субагент работает → restore слота main.
/// Без якоря субагент вытесняет KV main'а из пула (пул ≈ одна полная сессия).
///
/// Файлы: плоские имена (fs_validate_filename отклоняет разделители) —
/// sessions/&lt;session-id&gt;-kv.bin, рядом с папкой сессии (CRUD — одна строка в
/// ChatSessions.Delete). Размер: ~320 КБ/токен (f16 в файле) — 80k токенов ≈ 26 ГБ.
///
/// Все методы — best-effort: сетевые ошибки и 4xx/5xx дают false (вызывающий
/// деградирует без якоря), не бросают. Исключение — только отмена (OperationCanceledException).
/// </summary>
public sealed class KvCacheController
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(600) };

    private readonly Func<string> _endpoint;

    public KvCacheController(Func<string>? endpoint = null)
    {
        _endpoint = endpoint ?? (() => Settings.AppSettings.Get().Endpoint);
    }

    private string Base => _endpoint().TrimEnd('/');

    /// <summary>Состояние слота из GET /slots (детальные поля — при LLAMA_SERVER_SLOTS_DEBUG=1).</summary>
    public sealed record SlotInfo(int Id, bool IsProcessing, int? NPromptTokens);

    /// <summary>Список слотов. Пусто — сервер недоступен/не отвечает.</summary>
    public async Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SharedHttp.GetAsync($"{Base}/slots", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var result = new List<SlotInfo>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : -1;
                var processing = element.TryGetProperty("is_processing", out var procProp) && procProp.ValueKind == JsonValueKind.True;
                int? tokens = element.TryGetProperty("n_prompt_tokens", out var tokProp) && tokProp.ValueKind == JsonValueKind.Number
                    ? tokProp.GetInt32()
                    : null;
                result.Add(new SlotInfo(id, processing, tokens));
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Сохранить KV слота в файл (filename — плоское имя внутри --slot-save-path).
    /// false — ошибка (501 без --slot-save-path, слот с медиа, сетевая).
    /// </summary>
    public Task<bool> SaveSlotAsync(int slotId, string filename, CancellationToken cancellationToken = default) =>
        SlotActionAsync(slotId, "save", new JsonObject { ["filename"] = filename }, cancellationToken);

    /// <summary>Восстановить KV слота из файла (заменяет промпт слота). false — ошибка/нет файла.</summary>
    public Task<bool> RestoreSlotAsync(int slotId, string filename, CancellationToken cancellationToken = default) =>
        SlotActionAsync(slotId, "restore", new JsonObject { ["filename"] = filename }, cancellationToken);

    /// <summary>Выкинуть KV слота. false — ошибка (в т.ч. слот с медиа).</summary>
    public Task<bool> EraseSlotAsync(int slotId, CancellationToken cancellationToken = default) =>
        SlotActionAsync(slotId, "erase", new JsonObject(), cancellationToken);

    private async Task<bool> SlotActionAsync(int slotId, string action, JsonObject body, CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        try
        {
            using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var response = await SharedHttp.PostAsync(
                $"{Base}/slots/{slotId}?action={action}", content, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                DiagnosticsLog.Log($"KV: /slots/{slotId}?action={action} → {(int)response.StatusCode} {Truncate(payload)}");
                return false;
            }
            // Успех: {id_slot, filename?, n_saved/n_restored/n_erased, timings} — логируем размер для отладки.
            var summary = payload.Length < 300 ? payload : Truncate(payload);
            DiagnosticsLog.Log($"KV: /slots/{slotId}?action={action} → ok ({(DateTime.Now - started).TotalSeconds:F1}s) {summary}");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            DiagnosticsLog.Log($"KV: /slots/{slotId}?action={action} → сетевая ошибка: {exception.Message}");
            return false;
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
