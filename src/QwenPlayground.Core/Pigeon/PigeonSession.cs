using System.Collections.Concurrent;
using System.Text.Json;
using Pigeon.Client;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Pigeon;

/// <summary>
/// Пигвин-клей харнесса (docs/MESSANGER.md §12–13): резидентный клиент pigeon-сервера
/// на сессию. Идентичность = чат: id агента — имя каталога сессии (GUID; main → "main"),
/// модель — self-report из /props, состояние подключения — &lt;sessionDir&gt;/pigeon.json
/// (endpoint, token, alias, channel, status).
///
/// Цикл long-poll живёт на UI-потоке (async, не блокирует): mention этой сессии будит чат
/// через существующий wake-путь (только когда чат свободен), новые сообщения копятся в
/// счётчике «new» и видны в state-блоке (IStateAnnouncer, метка "pigeon"). pigeon_read
/// сбрасывает счётчик — агент «увидел».
/// </summary>
public sealed class PigeonSession : IStateAnnouncer
{
    /// <summary>Реестр живых подключений: каталог сессии → сессия.</summary>
    private static readonly ConcurrentDictionary<string, PigeonSession> BySessionDir =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Процессные зависимости (Main.cs): self-report модели (активный /props, чтобы
    /// карточка не была "unknown"). null — pigeon не проведён (тесты): connect вернёт ошибку.
    /// </summary>
    public static Func<CancellationToken, Task<string?>>? ModelNameProvider { get; private set; }

    public static void WireDeps(Func<CancellationToken, Task<string?>> modelName)
    {
        ModelNameProvider = modelName;
    }

    /// <summary>
    /// PER-SESSION зависимости (решение владельца, 2026-10-16: «нахуя ты пытаешься
    /// пробуждать main»): mention будит heartbeat СОБСТВЕННОГО окна сессии.
    /// Каталог сессии → (занятость ЕЁ чата, wake-сигнал в ЕЁ WakeSignalStore).
    /// </summary>
    private sealed record SessionDeps(Func<bool> IsBusy, Action<string> Wake);
    private static readonly Dictionary<string, SessionDeps> SessionDepsByDir =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string sessionDir, Func<bool> isBusy, Action<string> wake) =>
        SessionDepsByDir[Path.GetFullPath(sessionDir)] = new SessionDeps(isBusy, wake);

    /// <summary>
    /// Снять регистрацию (main-окно переключилось с сессии): mention без окна ждёт в
    /// state-строке, а не будит чужой чат по устаревшей записи.
    /// </summary>
    public static void Unregister(string sessionDir) =>
        SessionDepsByDir.Remove(Path.GetFullPath(sessionDir));

    private static SessionDeps? DepsFor(string dir) =>
        SessionDepsByDir.TryGetValue(Path.GetFullPath(dir), out var d) ? d : null;

    /// <summary>Живое подключение каталога сессии или null (не подключено).</summary>
    public static PigeonSession? For(string? sessionDir) =>
        sessionDir is null ? null : BySessionDir.TryGetValue(Path.GetFullPath(sessionDir), out var s) ? s : null;

    /// <summary>
    /// Разлогинить ВСЕ живые подключения (выход приложения, решение владельца 2026-10-16):
    /// сервер не ждёт 45 с heartbeat-таймаута — идентичности уходят в оффлайн сразу.
    /// pigeon.json сохраняется (reconnect после рестарта мгновенный).
    /// </summary>
    public static void DisconnectAll()
    {
        foreach (var session in BySessionDir.Values)
        {
            try
            {
                session.Disconnect();
            }
            catch
            {
                // Одна мёртвая сессия не должна ломать разлогин остальных при shutdown.
            }
        }
    }

    /// <summary>
    /// Содержимое pigeon.json — состояние подключения сессии (персистентно: reconnect
    /// после рестарта мгновенный, токен не переиздаётся).
    /// </summary>
    private sealed class ConnectionFile
    {
        public string Endpoint { get; set; } = "";
        public string Token { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string SessionDir { get; }

    /// <summary>Id агента на сервере: имя каталога сессии (GUID; main → "main").</summary>
    public string AgentId { get; }

    public string? Endpoint { get; private set; }
    public string Alias { get; private set; } = "";
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    public string FilePath => Path.Combine(SessionDir, "pigeon.json");

    // ── Живое состояние (UI-поток) ──────────────────────────────────────────────────

    private PigeonClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    private Task? _heartbeatLoop;
    private long _cursor;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _lastWho = DateTime.MinValue;
    private PigeonIdentity[] _who = [];
    private string? _channel;
    private string? _channelDescription;
    private int _newCount;
    private bool _channelHot;
    private (string Alias, string Text, DateTimeOffset Ts)? _latest;
    private (string Alias, string Channel, string Text, DateTimeOffset Ts)? _pendingMention;

    /// <summary>Сколько новых сообщений с момента последнего pigeon_read (state-строка).</summary>
    public int NewCount => _newCount;

    /// <summary>Последнее сообщение (автор + текст + время) — для state-строки.</summary>
    public (string Alias, string Text, DateTimeOffset Ts)? Latest => _latest;

    /// <summary>Непрочитанный mention (автор, канал, текст, время) — будит чат, пока не обработан.</summary>
    public (string Alias, string Channel, string Text, DateTimeOffset Ts)? PendingMention => _pendingMention;

    /// <summary>Текущий канал (из membership на сервере; null — меня ещё не посадили).</summary>
    public string? Channel => _channel;

    private PigeonSession(string sessionDir)
    {
        SessionDir = sessionDir;
        AgentId = Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar));
    }

    // ── Connect / disconnect ────────────────────────────────────────────────────────

    /// <summary>
    /// Подключить сессию к серверу (или переподключить): pigeon.json, join (idempotent),
    /// опционально канал, запуск long-poll, авто-активация полки pigeon.
    /// Алиас присваивается АВТОМАТИЧЕСКИ = guid сессии (решение владельца, 2026-10-15):
    /// для этого харнесса guid — прямой идентификатор чат-сессии, упоминание — по нему;
    /// модель и прочее — экстра-метаданные карточки, не часть имени.
    /// Возвращает текст ошибки или null (успех). Запускать на UI-потоке.
    /// </summary>
    public static async Task<string?> ConnectAsync(string sessionDir, string endpoint, string? channel = null)
    {
        if (ModelNameProvider is null)
        {
            return "pigeon is not wired in this process (WireDeps) — cannot connect.";
        }
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "address is required: pigeon_connect <http://host:port> [channel]";
        }
        var dir = Path.GetFullPath(sessionDir);
        Directory.CreateDirectory(dir);
        var session = BySessionDir.GetOrAdd(dir, static d => new PigeonSession(d));
        if (session.Connected && session.Endpoint == endpoint.TrimEnd('/'))
        {
            return $"Already connected to {endpoint}.";
        }

        var file = LoadFile(dir) ?? new ConnectionFile { Endpoint = endpoint };
        file.Endpoint = endpoint;
        var client = new PigeonClient(endpoint);
        if (file.Token.Length > 0)
        {
            client.SetToken(file.Token);
        }
        try
        {
            // Self-report модели: активный /props (кэшируется в ServerProps) — экстра-поле карточки.
            var model = await ModelNameProvider(CancellationToken.None) ?? "unknown";
            var join = new PigeonJoinRequest
            {
                Id = session.AgentId,
                Alias = session.AgentId, // авто: guid сессии (уникален на сервере по построению)
                Kind = "agent",
                Harness = "qwen",
                Model = model,
                Status = file.Status,
                Wake = "mention",
                Token = file.Token.Length > 0 ? file.Token : null,
            };
            var result = await client.JoinAsync(join);
            client.SetToken(result.Token);
            file.Token = result.Token;
            file.Alias = session.AgentId;
            SaveFile(dir, file); // НЕМЕДЛЕННО: новый токен живёт на диске, даже если канал ниже упадёт

            // Канал: опционален и НЕ фатален — соединение (join) уже состоялось. Ошибка
            // (нет такого канала) — предупреждение, а не срыв подключения (инцидент 2026-10-15:
            // channel-join убивал connect и новый токен не персистился → 401 при reconnect).
            string channelWarning = "";
            if (!string.IsNullOrWhiteSpace(channel))
            {
                try
                {
                    await client.JoinChannelAsync(channel);
                    file.Channel = channel;
                    SaveFile(dir, file);
                }
                catch (Exception chEx)
                {
                    channelWarning = $" WARNING: could not join channel '{channel}' ({chEx.Message}) — connected, but not seated.";
                }
            }

            session._client = client;
            session.Endpoint = endpoint.TrimEnd('/');
            session.Alias = session.AgentId;
            session.Connected = true;
            session.LastError = null;
            session._channel = null; // актуальное membership придёт с первым who
            session._channelDescription = null;
            session._newCount = 0;
            session._latest = null;
            session._pendingMention = null;
            session._cursor = 0;
            session.StartLoop();

            // Авто-активация полки: подключён → тулзы в промпте (docs §12).
            new ShelfState(dir).Activate(ToolGroup.Pigeon);
            var seated = string.IsNullOrWhiteSpace(file.Channel)
                ? " — not seated in a channel yet (the owner seats you in the web UI, or use pigeon_channels join)."
                : $" — channel #{file.Channel}.";
            return (result.Duplicate
                    ? $"Connected to {endpoint} as @{session.AgentId} — NOTE: the server flagged a duplicate id " +
                      "(another live client with the same session id; one of you is a copy)."
                    : $"Connected to {endpoint} as @{session.AgentId}{seated}") + channelWarning;
        }
        catch (Exception ex)
        {
            client.Dispose();
            return $"connect failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Отключить: остановить циклы; pigeon.json сохранить (reconnect мгновенный).
    /// На сервере идентичность уйдёт в оффлайн сама (нет heartbeat'ов).
    /// </summary>
    public string Disconnect()
    {
        if (!Connected && _client is null)
        {
            return "Not connected.";
        }
        StopLoops();
        _client?.Dispose();
        _client = null;
        Connected = false;
        _channel = null;
        _pendingMention = null;
        return $"Disconnected from {Endpoint}. The connection file is kept — pigeon_connect reconnects instantly.";
    }

    // ── Циклы (UI-поток) ────────────────────────────────────────────────────────────

    private void StartLoop()
    {
        StopLoops();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // Без Task.Run: async-машина остаётся на UI-контексте (инвариант: агентный код на UI-потоке).
        _pollLoop = PollLoopAsync(ct);
        // Presence is not agent code. While a turn is running the UI thread is busy, and a
        // heartbeat living in the poll loop would stop exactly when the agent is most alive —
        // the roster would read "offline" for an agent deep in a tool loop. So it ticks on its
        // own thread and only ever touches HTTP.
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(ct));
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = _client;
            if (client is null) break;
            try
            {
                var deps = DepsFor(SessionDir);
                string state = deps is null ? "detached" : deps.IsBusy() ? "generating" : "idle";
                string activity = deps is null
                    ? "no chat window open for this session"
                    : deps.IsBusy()
                        ? "turn in progress"
                        : "listening";
                await client.HeartbeatAsync(state, activity, ct);
            }
            catch
            {
                // Presence is cosmetic; a failed heartbeat must not kill the loop.
            }
            try { await Task.Delay(10_000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void StopLoops()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        _cts = null;
        _pollLoop = null;
        _heartbeatLoop = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = _client;
            if (client is null)
            {
                break;
            }
            try
            {
                // Presence lives in its own loop (HeartbeatLoopAsync) so a busy UI thread
                // does not make a working agent look offline.
                // Карточки участников (для who + своего membership): каждые 30 с.
                if ((DateTime.UtcNow - _lastWho).TotalSeconds >= 30)
                {
                    _who = await client.WhoAsync(ct);
                    _lastWho = DateTime.UtcNow;
                    var me = _who.FirstOrDefault(p => p.Id == AgentId);
                    if (me is not null)
                    {
                        Alias = me.Alias; // сервер мог уникализировать алиас
                        _channel = me.Channels.FirstOrDefault();
                    }
                }

                if (_channel is null)
                {
                    // Меня ещё не посадили в канал — ждём (owner в web UI или pigeon_channels join).
                    await Task.Delay(15_000, ct);
                    continue;
                }

                // Описание канала — в state-строку (контекст комнаты без чтения истории).
                var channels = await client.ChannelsAsync(ct);
                var mine = channels.FirstOrDefault(c => c.Id == _channel);
                _channelDescription = mine?.Description;
                _channelHot = mine?.Hot ?? false;

                // Long-poll: новые сообщения (до 25 с ожидания).
                var batch = await client.ReadAsync(_channel, _cursor, 25, ct);
                foreach (var m in batch)
                {
                    _cursor = Math.Max(_cursor, m.Id);
                    HandleIncoming(m);
                }
                TryWakeIfIdle();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // 401 = токен больше не действителен (сервер пересоздан / другая dataDir).
                // Re-join с пустым токеном: сервер выпускает новый (docs §4). Иначе клиент
                // будет вечно ретраить stale-токен (инцидент 2026-10-18: лаунчер поднял
                // свежий сервер, старая сессия получила 401 и молчала).
                if (ex.Message.Contains("401"))
                {
                    try
                    {
                        client.SetToken(null);
                        var file = LoadFile(SessionDir);
                        var model = ModelNameProvider is not null
                            ? await ModelNameProvider(ct) ?? "unknown"
                            : "unknown";
                        var rejoin = await client.JoinAsync(new PigeonJoinRequest
                        {
                            Id = AgentId,
                            Alias = AgentId,
                            Kind = "agent",
                            Harness = "qwen",
                            Model = model,
                            Status = file?.Status ?? "",
                            Wake = "mention",
                            Token = null,
                        }, ct);
                        client.SetToken(rejoin.Token);
                        if (file is not null)
                        {
                            file.Token = rejoin.Token;
                            SaveFile(SessionDir, file);
                        }
                        LastError = null;
                        continue;
                    }
                    catch
                    {
                        // re-join не удался — падаем в обычный ретрай
                    }
                }
                try
                {
                    await Task.Delay(5_000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void HandleIncoming(PigeonMessage m)
    {
        _newCount++;
        _latest = (m.Author.Alias, m.Text, m.Ts);
        if (m.Mentions.Contains(AgentId, StringComparer.Ordinal))
        {
            _pendingMention = (m.Author.Alias, m.Channel, m.Text, m.Ts);
        }
    }

    /// <summary>
    /// Mention этой сессии + свободный ЕЁ чат → wake-сигнал в heartbeat ЕЁ же окна
    /// (решение владельца, 2026-10-16: mention будит собственную сессию, не main).
    /// Чат занят — mention дождётся (виден в state-строке); повторно будить по одному
    /// и тому же mention не будем (null'ируем после отправки сигнала).
    /// </summary>
    private void TryWakeIfIdle()
    {
        if (_pendingMention is not { } mention)
        {
            return;
        }
        var deps = DepsFor(SessionDir);
        if (deps is null || deps.IsBusy())
        {
            return; // не зарегистрировано или чат этой сессии занят — mention дождётся
        }
        _pendingMention = null; // разбужен — не будим повторно по тому же сообщению
        deps.Wake(
            $"[pigeon] You were mentioned in #{mention.Channel} by @{mention.Alias}: \"{mention.Text}\". " +
            "Read the channel with pigeon_read and reply with pigeon_send if a reply is due — " +
            "ending the turn without replying is also fine (participation is optional).");
    }

    // ── Операции (вызываются из тулов) ──────────────────────────────────────────────

    private PigeonClient ClientOrThrow()
    {
        if (_client is not { } client)
        {
            throw new InvalidOperationException("not connected — use pigeon_connect <address>");
        }
        return client;
    }

    /// <summary>
    /// Прочитать последние N сообщений своего канала (peek — история, не только новое).
    /// Сбрасывает счётчики «new»/mention: агент увидел.
    /// </summary>
    public async Task<PigeonMessage[]> ReadAsync(int count)
    {
        var client = ClientOrThrow();
        if (_channel is null)
        {
            return [];
        }
        var messages = await client.PeekAsync(_channel, count);
        _newCount = 0;
        _latest = null;
        _pendingMention = null;
        return messages;
    }

    /// <summary>
    /// Отправить сообщение. Упоминания — @-токены прямо в тексте ("@alias" или "@guid"):
    /// сервер резолвит их в id участников (отдельного параметра нет, 2026-10-15).
    /// </summary>
    public async Task<PigeonMessage> SendAsync(string text, long? thread, long? replyTo)
    {
        var client = ClientOrThrow();
        if (_channel is null)
        {
            throw new InvalidOperationException("not seated in a channel yet (pigeon_channels join)");
        }
        return await client.SendAsync(new PigeonSendRequest
        {
            Channel = _channel,
            Text = text,
            Thread = thread,
            ReplyTo = replyTo,
        });
    }

    public async Task SetStatusAsync(string status)
    {
        var client = ClientOrThrow();
        await client.SetStatusAsync(status);
        var file = LoadFile(SessionDir);
        if (file is not null)
        {
            file.Status = status;
            SaveFile(SessionDir, file);
        }
    }

    public Task<PigeonIdentity[]> WhoAsync() => ClientOrThrow().WhoAsync();

    public Task<PigeonChannel[]> ChannelsAsync() => ClientOrThrow().ChannelsAsync();

    /// <summary>Group catalogue — what an @mention of a name would reach.</summary>
    public Task<PigeonGroup[]> GroupsAsync() => ClientOrThrow().GroupsAsync();

    /// <summary>Join a group of this session's own accord; owner-assigned groups refuse.</summary>
    public Task JoinGroupAsync(string group) => ClientOrThrow().JoinGroupAsync(group);

    public Task LeaveGroupAsync(string group) => ClientOrThrow().LeaveGroupAsync(group);

    public async Task JoinChannelAsync(string channelId)
    {
        var client = ClientOrThrow();
        await client.JoinChannelAsync(channelId);
        var file = LoadFile(SessionDir);
        if (file is not null)
        {
            file.Channel = channelId;
            SaveFile(SessionDir, file);
        }
        _channel = channelId; // не ждать 30-с who
    }

    // ── State-блок (IStateAnnouncer) ────────────────────────────────────────────────

    /// <summary>
    /// Строка pigeon в state-блоке (метка "pigeon"): канал + описание, новые сообщения,
    /// mention. Не подключён — тишина. Формат (docs §10):
    ///   pigeon: #general — "what is this channel for"; 2 new (latest @builder: "diff is ready")
    ///   pigeon: mention: @owner in #general: "check the build"
    /// </summary>
    public IReadOnlyList<StateAnnounce> GetAnnounces()
    {
        if (!Connected)
        {
            return [];
        }
        var messages = new List<string>(2);
        if (_channel is null)
        {
            messages.Add($"connected to {Endpoint} — not seated in a channel yet " +
                         "(the owner seats you in the web UI, or use pigeon_channels join)");
        }
        else
        {
            var line = $"#{_channel}" + (_channelDescription is { Length: > 0 } d ? $" — \"{d}\"" : "")
                       + (_channelHot ? " [hot]" : "");
            if (_newCount > 0)
            {
                // Время локальное (сервер хранит UTC): владелец и агент смотрят на одни часы.
                line += _latest is { } latest
                    ? $"; {_newCount} new (latest @{latest.Alias} {latest.Ts.ToLocalTime():HH:mm}: \"{Truncate(latest.Text)}\")"
                    : $"; {_newCount} new";
            }
            if (LastError is { Length: > 0 } err && _newCount == 0)
            {
                line += $"; poll error: {Truncate(err)}";
            }
            messages.Add(line);
            if (_pendingMention is { } mention)
            {
                messages.Add($"mention {mention.Ts.ToLocalTime():HH:mm}: @{mention.Alias} in #{mention.Channel}: \"{Truncate(mention.Text)}\"");
            }
        }
        return [new StateAnnounce { Label = "pigeon", Messages = messages, Source = this }];
    }

    private static string Truncate(string text, int max = 120)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    // ── pigeon.json ─────────────────────────────────────────────────────────────────

    private static ConnectionFile? LoadFile(string dir)
    {
        var path = Path.Combine(dir, "pigeon.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConnectionFile>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Содержимое pigeon.json для UI (диалог 🕊): предыдущие значения полей.
    /// null — файла нет (сессия ещё ни разу не подключалась).
    /// </summary>
    public static (string Endpoint, string Alias, string Channel, string Status)? LoadConnection(string sessionDir)
    {
        var file = LoadFile(sessionDir);
        return file is null ? null : (file.Endpoint, file.Alias, file.Channel, file.Status);
    }

    /// <summary>
    /// Статус в pigeon.json (диалог 🕊, когда НЕ подключены): применится при следующем
    /// connect (join уносит file.Status). Файл создаётся, если его нет.
    /// </summary>
    public static void SaveStatusToFile(string sessionDir, string status)
    {
        var dir = Path.GetFullPath(sessionDir);
        Directory.CreateDirectory(dir);
        var file = LoadFile(dir) ?? new ConnectionFile();
        file.Status = status;
        SaveFile(dir, file);
    }

    private static void SaveFile(string dir, ConnectionFile file)
    {
        var path = Path.Combine(dir, "pigeon.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
