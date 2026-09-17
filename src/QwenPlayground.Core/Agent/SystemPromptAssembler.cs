using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Сборка системного промпта сессии и множества инструментов запроса: ядро (идентичность
/// main / фрагмент профиля) + внешние инструменты + индекс полок + слои памяти.
/// Единый источник и для реального запроса, и для превью (PromptPipeline) — превью и ход
/// совпадают по построению.
///
/// Владеет кэшированием промпта: детект пересборки KV-кеша и батчинг staged-деактиваций
/// полок — деактивация батчится с ЕСТЕСТВЕННОЙ сменой промпта (компакция/смена сессии/
/// слои) и не создаёт собственный rebuild. Внутренности непрозрачны для внешнего
/// наблюдателя: наружу смотрят только четыре метода.
/// </summary>
public sealed class SystemPromptAssembler
{
    private readonly Func<string> _currentSessionId;
    private readonly Func<string?> _promptKey;
    private readonly Func<string> _sessionDir;
    private readonly InjectedIdentity _identity;
    private readonly ExternalToolsNote _externalTools;
    private readonly ToolRegistry _toolRegistry;

    private string? _cachedSystemPrompt;
    // Base-промпт (без индекса полок) — для детекта ЕСТЕСТВЕННОЙ смены промпта: именно она
    // меняется при компакции/смене сессии/слоях. С её сменой батчим staged-деактивации.
    private string? _cachedBasePrompt;

    public SystemPromptAssembler(
        Func<string> currentSessionId,
        Func<string?> promptKey,
        Func<string> sessionDir,
        InjectedIdentity identity,
        ExternalToolsNote externalTools,
        ToolRegistry toolRegistry)
    {
        _currentSessionId = currentSessionId;
        _promptKey = promptKey;
        _sessionDir = sessionDir;
        _identity = identity;
        _externalTools = externalTools;
        _toolRegistry = toolRegistry;
    }

    /// <summary>Финальный системный промпт (null — ядра нет: пустой профиль и нет идентичности).</summary>
    public string? ResolveSystemPrompt()
    {
        var isMain = _currentSessionId() == MainAgent.SessionId;
        // Ядро: main — динамическая идентичность (main-agent.md + траектория),
        // не-main — профиль. Слои L1/L2/L3 инжектятся ниже ОТДЕЛЬНО и независимо от ядра:
        // пустой (default) профиль их не глотает — баг 2026-09-04: после компакции слои
        // писались в sessions/<id>/layers.json, но в промпт (и превью) не попадали, т.к.
        // RenderSystemPrompt() для пустого профиля возвращает null.
        string? core = isMain
            ? _identity.GetFor(true)
            : ChatProfiles.Get().ResolvePrompt(_promptKey()).RenderSystemPrompt();

        // Секция «внешние инструменты» (external/README.md) — всем интерактивным сессиям.
        var note = _externalTools.Get();
        // Слои памяти L1/L2/L3 (per-session, sessions/<id>/layers.json) — дистиллированная
        // история; у main и не-main один и тот же путь (CurrentId = "main" / id сессии).
        var layers = new MemoryLayerStore(
            Path.Combine(SelfBuildPaths.WorkspaceRoot, "sessions", _currentSessionId())).Load();
        var layersBlock = layers.IsEmpty ? string.Empty : layers.ToPromptBlock();

        // Base-промпт (без индекса полок) — для детекта ЕСТЕСТВЕННОЙ смены промпта: именно
        // он меняется при компакции/смене сессии/слоях. С его сменой батчим staged-деактивации.
        var basePrompt = Combine(Combine(core, note), layersBlock.Length > 0 ? layersBlock : null);

        // Staged-деактивации: снимаем помеченные группы ТОЛЬКО когда base-промпт и так меняется
        // (компакция/смена сессии/слои) — деактивация батчится с неизбежным rebuild'ом, а не
        // создаёт собственный. Пока base не менялась — pending-группы остаются в промпте.
        var shelf = new ShelfState(_sessionDir());
        var pending = shelf.LoadPending();
        if (pending.Count > 0 && !string.Equals(basePrompt, _cachedBasePrompt, StringComparison.Ordinal))
        {
            var removed = shelf.FlushPending().ToList();
            if (removed.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[shelf-cache] staged-деактивация при смене промпта: {string.Join(", ", removed)}");
            }
        }
        _cachedBasePrompt = basePrompt;

        // Индекс полок: pending-группы рисуются как активные (их тулзы реально ещё в промпте).
        // Вставляется ПЕРЕД слоями: системный промпт течёт в чат как
        // «кто я → какие инструменты → старая история → чуть новейшая → последняя → чат».
        var mcpRows = BuildMcpServerRows();
        var index = ToolGroupIndex.Render(EffectiveShelves(), _toolRegistry, mcpRows);
        var beforeLayers = Combine(Combine(core, note), index.Length > 0 ? index : null);
        var final = Combine(beforeLayers, layersBlock.Length > 0 ? layersBlock : null);
        // Трекер кешированного промпта: изменился → KV-кеш пересоберётся (диагностика).
        if (!string.Equals(_cachedSystemPrompt, final, StringComparison.Ordinal))
        {
            _cachedSystemPrompt = final;
            System.Diagnostics.Debug.WriteLine(
                $"[shelf-cache] системный промпт изменился (KV-кеш rebuild), длина={final?.Length ?? 0}");
        }
        return final;
    }

    /// <summary>
    /// Тулзы для запроса: базовый набор (Core + активные полки) ∩ whitelist профиля (если задан).
    /// То же множество даёт превью (PromptPipeline.AdvertisedTools) — превью и запрос совпадают.
    /// </summary>
    public IReadOnlyList<ToolDefinition> ToolsFor(IReadOnlyList<string> allowed)
    {
        var tools = new List<ToolDefinition>(_toolRegistry.DefinitionsByGroup(ToolGroup.Core));
        foreach (var group in EffectiveShelves())
        {
            tools.AddRange(_toolRegistry.DefinitionsByGroup(group));
        }
        // Память выключена — memory_*-тулы не рекламируем (модель не может их вызвать).
        tools = tools.Where(d => MemoryToolGate.ShouldAdvertise(d.Name)).ToList();
        if (allowed.Count == 0)
        {
            return tools;
        }
        var allow = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tools.Where(d => allow.Contains(d.Name)).ToList();
    }

    /// <summary>
    /// Полки, тулзы которых реально в промпте: активные + pending-деактивации. Pending-группы
    /// ещё не сняты (ждут естественной смены промпта), их тулзы по-прежнему доступны — индекс
    /// и реклама тулов должны совпадать, иначе модель увидит «active» без инструментов.
    /// </summary>
    public IReadOnlyList<ToolGroup> EffectiveShelves()
    {
        var shelf = new ShelfState(_sessionDir());
        var active = shelf.Load();
        foreach (var g in shelf.LoadPending())
        {
            active.Add(g);
        }
        return active.OrderBy(g => g).ToList();
    }

    /// <summary>
    /// Авто-выключение неиспользуемых полок после компакции: в оставшемся контексте ни одного
    /// ToolCall из инструментов группы → снимаем. Бесплатно: компакция и так пересобирает
    /// системный промпт, деактивация батчится с неизбежным rebuild'ом (не создаёт собственный).
    /// </summary>
    public void DeactivateUnusedShelves(IReadOnlyList<ChatMessage> conversation)
    {
        var shelf = new ShelfState(_sessionDir());
        var active = shelf.Load();
        if (active.Count == 0)
        {
            return;
        }
        var unused = ShelfState.FindUnused(active, conversation, _toolRegistry).ToList();
        if (unused.Count == 0)
        {
            return;
        }
        foreach (var g in unused)
        {
            active.Remove(g);
            shelf.UnmarkPending(g); // если была помечена — решение уже исполнено, пометка не нужна
        }
        shelf.Save(active);
        System.Diagnostics.Debug.WriteLine(
            $"[shelf-cache] авто-выключение после компакции: {string.Join(", ", unused)}");
    }

    /// <summary>Склейка двух фрагментов промпта пустой строкой; null/пусто — второй фрагмент как есть.</summary>
    private static string? Combine(string? a, string? b) =>
        a is null ? b : b is null ? a : a + "\n\n" + b;

    /// <summary>Строки для таблицы MCP Servers в системном промпте.</summary>
    private IReadOnlyList<ToolGroupIndex.McpServerRow> BuildMcpServerRows()
    {
        var settings = AppSettings.Get();
        if (settings.McpServers.Count == 0)
        {
            return Array.Empty<ToolGroupIndex.McpServerRow>();
        }

        var manager = McpService.Instance;
        var clients = manager?.Clients ?? new Dictionary<string, McpClient>();
        var mcpShelfActive = EffectiveShelves().Contains(ToolGroup.Mcp);

        return settings.McpServers.Select(s =>
        {
            clients.TryGetValue(s.Name, out var client);
            var connected = client?.IsConnected ?? false;

            // Status: active (shelf on + connected), inactive (shelf off), disconnected (no conn)
            string status;
            if (!connected) status = "disconnected";
            else if (mcpShelfActive) status = "active";
            else status = "inactive";

            // Address: http → URL; stdio → command + args (or env port if present)
            string address;
            if (s.Transport == "http")
            {
                address = s.Url;
            }
            else
            {
                // stdio: show command + args, or env port for bridge servers
                var cmd = string.IsNullOrEmpty(s.Command) ? "" :
                    System.IO.Path.GetFileNameWithoutExtension(s.Command) + " " +
                    string.Join(" ", s.Args);
                // Check for port in env (e.g. BLENDER_MCP_PORT). FirstOrDefault на Dictionary
                // возвращает struct KeyValuePair (никогда не null): без совпадения Value null —
                // NRE на .Value (баг 2026-09-14: превью падало на stdio-сервере с пустым Env).
                string? port = null;
                if (s.Env is not null)
                {
                    foreach (var kv in s.Env)
                    {
                        if (kv.Key is not null && kv.Key.Contains("PORT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value))
                        {
                            port = kv.Value;
                            break;
                        }
                    }
                }
                if (port is not null)
                {
                    cmd += $" → localhost:{port}";
                }
                address = cmd;
            }

            return new ToolGroupIndex.McpServerRow(
                s.Name,
                status,
                s.Transport,
                address,
                client?.Tools.Count ?? 0,
                string.IsNullOrEmpty(s.Description) ? "—" : s.Description);
        }).ToList();
    }
}
