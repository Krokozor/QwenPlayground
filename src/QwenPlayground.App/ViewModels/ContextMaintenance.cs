using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Сжатие контекста и обслуживание бюджета (домен, вытащенный из MainViewModel):
/// бэкап сессии → резюме ветки (обычные сессии) ИЛИ слоистый конвейер L1/L2/L3 (main) →
/// извлечение долговременных фактов в memories/.
///
/// FSM-контракт: вызов из бюджет-гварда цикла приходит УЖЕ в состоянии Compacting;
/// ручной вызов сам переводит Idle → Compacting и вернёт Idle, авто — вернёт Generating.
/// Ошибки сжатия наружу не бросаются — уходят в статус (бэкап уже снят, история цела).
/// Ручная компакция во время Generating не блокирует цикл, а ставится в очередь флагом.
/// </summary>
public sealed class ContextMaintenance
{
    /// <summary>Канал обратной связи с чатом: статус-строка, флаг «занят», сохранение разговора (вид перестраивается событием ChatLog.Changed).</summary>
    public sealed record Ui(Action<string> SetStatus, Action<bool> SetGenerating, Action Save);

    private readonly ChatLog _conversation;
    private readonly ChatStateMachine _chat;
    private readonly CompactionPreview _preview;
    private readonly Func<string, string?, Action<string>?, CancellationToken, Task<string?>> _completeStructured;
    private readonly MemoryLayerStore _layers;
    private readonly MemorySurfacer _surfacer;
    private readonly Func<CancellationToken, Task<int>> _countNextTokens;
    private readonly Func<Task<int>> _effectiveSize;
    private readonly Func<string> _currentSessionId;
    private readonly ContextBackupStore _backups;
    private readonly Func<MemoryStore> _storeFactory;
    private readonly Ui _ui;

    private bool _requested; // ручная компакция, запрошенная во время Generating

    public ContextMaintenance(
        ChatLog conversation,
        ChatStateMachine chat,
        CompactionPreview preview,
        Func<string, string?, Action<string>?, CancellationToken, Task<string?>> completeStructured,
        MemoryLayerStore layers,
        MemorySurfacer surfacer,
        Func<CancellationToken, Task<int>> countNextTokens,
        Func<Task<int>> effectiveSize,
        Func<string> currentSessionId,
        ContextBackupStore backups,
        Ui ui,
        Func<MemoryStore>? storeFactory = null)
    {
        _conversation = conversation;
        _chat = chat;
        _preview = preview;
        _completeStructured = completeStructured;
        _layers = layers;
        _surfacer = surfacer;
        _countNextTokens = countNextTokens;
        _effectiveSize = effectiveSize;
        _currentSessionId = currentSessionId;
        _backups = backups;
        _ui = ui;
        // Шов для тестов: в приложении — реальное хранилище memories/.
        _storeFactory = storeFactory ?? (() => new MemoryStore());
    }

    /// <summary>
    /// Бюджет-проверка перед следующим ходом: точный размер промпта у сервера против окна
    /// (с резервом на ответ). Не влезает ИЛИ пользователь просил сжатие во время генерации — сжимаем.
    /// Ошибка подсчёта бросается наверх: ход не должен начинаться вслепую.
    /// </summary>
    public async Task EnsureBudgetAsync(CancellationToken cancellationToken)
    {
        var effective = await _effectiveSize();
        var estimated = await _countNextTokens(cancellationToken);
        var exceeded = effective - estimated <
                       AppSettings.Get().MaxTokens + ContextCompactor.CompactionReserveTokens;
        var compact = exceeded || _requested;
        _requested = false;
        if (compact)
        {
            await RunAsync(fromAgentLoop: true);
        }
    }

    /// <summary>Ручная компакция из UI.</summary>
    public async Task CompactFromUiAsync() => await RunAsync(fromAgentLoop: false);

    private async Task RunAsync(bool fromAgentLoop)
    {
        var effective = await _effectiveSize();
        var keepRatio = ParseKeepRatio(AppSettings.Get().CompactKeepRatio);
        var boundary = ContextCompactor.FindCompactionBoundary(_conversation, keepRatio, effective);
        if (boundary == 0)
        {
            _ui.SetStatus("нечего сжимать");
            return;
        }
        _surfacer.Clear(); // всплывшие воспоминания выпадают из контекста на суммаризации

        if (fromAgentLoop)
        {
            // Вызов из contextBudgetGuard: FSM уже в Compacting (переведён guard'ом).
            _ui.SetStatus("авто-компакция между итерациями...");
        }
        else
        {
            // Ручная компакция: если мы в Generating — ставим флаг, а не блокируем цикл.
            if (_chat.Current == ChatState.Generating)
            {
                _requested = true;
                _ui.SetStatus("компакция запрошена — выполнится между итерациями");
                return;
            }
            _chat.Transition(ChatState.Compacting);
            _ui.SetGenerating(true);
            _ui.SetStatus("сжатие контекста...");
        }
        try
        {
            // Точка отката: без бэкапа не сжимаем (восстановление = Restore папки/файла сессии).
            var backup = _backups.Save(_currentSessionId());
            _ui.SetStatus($"бэкап: {Path.GetFileName(backup)}; сжатие...");
            _preview.Begin();

            if (_currentSessionId() == MainAgent.SessionId)
            {
                // main-агент: слоистая память L1/L2/L3 (изолированные конвейерные шаги).
                await CompactMainAsync(boundary);
            }
            else
            {
                var (system, user) = ContextCompactor.BuildSummarizationRequest(_conversation, boundary, effective);

                BeginStage("резюме ранней части диалога");
                var summary = await _completeStructured(user, system, chunk => _preview.Append(chunk), CancellationToken.None)
                              ?? throw new InvalidOperationException(
                                  "сводка: модель не вызвала submit_result (улетела в размышления или оборвалась). " +
                                  "Бэкап снят — можно восстановить или повторить сжатие.");
                _preview.Flush();

                // Сжатый сегмент (без ведущего system) — снимаем ДО замены разговора.
                var segmentStart = _conversation.Count > 0 && _conversation[0].Role == ChatRole.System ? 1 : 0;
                var segment = _conversation.CopyRange(segmentStart, Math.Max(0, boundary - segmentStart));

                var compacted = ContextCompactor.ApplyCompaction(_conversation, boundary, summary);
                _conversation.ReplaceAll(compacted);
                _ui.Save();

                // Долговременные факты из сжатого сегмента → memories/ (best-effort: не роняет компакцию).
                var memoriesSaved = await ExtractMemoriesFromSegmentAsync(segment);
                _ui.SetStatus(memoriesSaved > 0
                    ? $"контекст сжат: {boundary} сообщ. → резюме, +{memoriesSaved} в память"
                    : $"контекст сжат: {boundary} сообщ. → резюме");
            }
        }
        catch (Exception exception)
        {
            _ui.SetStatus($"ошибка сжатия: {exception.Message}");
        }
        finally
        {
            _preview.End();
            // FSM: Compacting → Idle (ручная) или → Generating (авто между итерациями).
            // Сначала FSM, потом SetGenerating(false): уведомление CanExecuteChanged должно
            // стрельнуть, когда IsBusy уже false, иначе кнопка отката останется серой.
            if (_chat.Current == ChatState.Compacting)
            {
                _chat.Transition(fromAgentLoop ? ChatState.Generating : ChatState.Idle);
                _ui.SetGenerating(false);
            }
        }
    }

    /// <summary>
    /// Компакция main-агента через конвейер слоёв L1/L2/L3: сжатый сегмент становится новым L3,
    /// старый L3 сдвигается в L2, L1+L2 сливаются в новый L1; утерянные факты валидаций — в memories/.
    /// </summary>
    private async Task CompactMainAsync(int boundary)
    {
        _surfacer.Clear(); // всплывшие воспоминания выпадают из контекста на суммаризации
        var segmentStart = _conversation.Count > 0 && _conversation[0].Role == ChatRole.System ? 1 : 0;
        var segment = _conversation.CopyRange(segmentStart, Math.Max(0, boundary - segmentStart));
        var transcript = ContextCompactor.BuildTranscript(segment, segment.Count);

        var result = await MemoryLayerPipeline.RunAsync(_layers.Load(), transcript, CompleteForPipelineAsync,
            onStage: BeginStage);

        // Без критичных шагов ротация потеряла бы содержимое — компакция прерывается (бэкап уже снят).
        if (!result.MergeSucceeded || !result.SegmentSucceeded)
        {
            throw new InvalidOperationException(
                "слоистый конвейер не смог: merge=" + (result.MergeSucceeded ? "ok" : "fail") +
                ", сегмент=" + (result.SegmentSucceeded ? "ok" : "fail"));
        }

        _layers.Save(result.Next);
        foreach (var fact in result.Facts.Take(MemoryExtractor.MaxFacts))
        {
            await SaveMemoryClassifiedAsync(fact, source: "compaction");
        }
        new DiaryStore().Append(result.Next.L3);

        // Оставляем хвост после границы (ID старших сообщений не переиспользуются — счётчик в логе).
        _conversation.TruncateKeep(boundary);
        _ui.Save();

        _ui.SetStatus($"контекст сжат: {boundary} сообщ. → L3, +{result.Facts.Count} в память");
    }

    /// <summary>Один изолированный LLM-вызов конвейера: результат возвращается через submit_result.</summary>
    private async Task<string> CompleteForPipelineAsync(string userContent, CancellationToken cancellationToken)
    {
        var result = await _completeStructured(
            userContent, null, chunk => _preview.Append(chunk), cancellationToken);
        _preview.Flush();

        // Тихий FallbackContent не используем: модель должна вызвать submit_result; если не вызвала —
        // бросаем, TryCompleteAsync поймает и пометит шаг проваленным (без молчаливого мусора).
        return result ?? throw new InvalidOperationException("модель не вызвала submit_result");
    }

    /// <summary>Извлечение долговременных фактов из сжатого сегмента в memories/ (best-effort).</summary>
    private async Task<int> ExtractMemoriesFromSegmentAsync(IReadOnlyList<ChatMessage> segment)
    {
        if (segment.Count == 0)
        {
            return 0;
        }
        try
        {
            var transcript = ContextCompactor.BuildTranscript(segment, segment.Count);
            var promptText = MemoryExtractor.BuildExtractionPrompt(transcript);
            // System-промпт цели «извлечение фактов» — из каталога (правится во вкладке «Суммаризация»).
            var system = PromptCatalog.Load().MemoryExtractionSystem;

            var result = await _completeStructured(promptText, system, null, CancellationToken.None);

            // Best-effort, но не через FallbackContent: без submit_result фактов нет — просто 0.
            if (result is null)
            {
                return 0;
            }
            var facts = MemoryExtractor.ParseFacts(result);
            if (facts.Count == 0)
            {
                return 0;
            }
            foreach (var fact in facts.Take(MemoryExtractor.MaxFacts))
            {
                await SaveMemoryClassifiedAsync(fact, source: "compaction");
            }
            return facts.Count;
        }
        catch (OperationCanceledException)
        {
            // Отмена — не «фактов нет»: пусть внешний catch компакции покажет честный статус.
            throw;
        }
        catch
        {
            return 0; // извлечение — не критичный путь компакции
        }
    }

    /// <summary>
    /// Сохраняет факт в память и классифицирует пробой компаньон-модели (категория + вайб).
    /// Best-effort: упало — факт остаётся без слоёв, реколл подтянет его по тексту.
    /// </summary>
    private async Task SaveMemoryClassifiedAsync(string content, string source)
    {
        var store = _storeFactory();
        var item = store.Add(content, source: source);
        await MemoryClassifier.EnrichAsync(
            item, AppSettings.Get().CompanionEndpoint, cancellationToken: CancellationToken.None);
        if (item.HasSemanticLayers)
        {
            store.Update(item);
        }
    }

    /// <summary>Доля хвоста для компакции: строка из настроек, допускает запятую как разделитель.</summary>
    private static double ParseKeepRatio(string? value)
    {
        if (double.TryParse((value ?? string.Empty).Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            && result > 0 && result < 1)
        {
            return result;
        }
        return ContextCompactor.DefaultKeepRatio;
    }

    /// <summary>Очередной этап конвейера: заголовок-разделитель в превью + подпись в статусе.</summary>
    private void BeginStage(string stage)
    {
        _preview.NewStage(stage);
        _ui.SetStatus("сжатие: " + stage + "...");
    }
}
