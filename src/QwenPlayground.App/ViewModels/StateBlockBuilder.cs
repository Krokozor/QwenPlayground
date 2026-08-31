using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Сборка state-блока — снапшота «что агент знает о себе прямо сейчас»: msg_id, время,
/// фактический контекст (cur/max), последняя сборка, всплывшие воспоминания, наг'ы.
/// Блок свежий на каждом рендере; парсер привязывает его к ответу, так что в истории
/// у каждого хода свой снапшот и модель видит эволюцию своего статуса.
///
/// msg_id в блоке = СТАБИЛЬНЫЙ ID генерируемого assistant-сообщения: перед сборкой
/// владелец обязан прогнать AssignPendingIds (делегат <see cref="Action"/>) — тогда
/// счётчик указывает ровно на тот ID, который сообщение получит при добавлении.
/// </summary>
public sealed class StateBlockBuilder
{
    /// <summary>Кэш последней записи journal.json развёрнутого run/ (читается на каждом рендере).</summary>
    private static FileDependentCache<BuildJournalEntry?>? _lastBuildCache;

    private readonly Action _assignPendingIds;
    private readonly Func<int> _nextMessageId;
    private readonly Func<int> _effectiveContextSize;
    private readonly ServerProps _serverProps;
    private readonly Func<IReadOnlyList<ChatMessage>> _conversation;
    private readonly Func<IReadOnlyList<SurfacedMemory>> _surfaced;
    private readonly Func<string?> _memoryNag;
    private readonly Func<IReadOnlyList<PendingPair>> _pendingPairs;

    public StateBlockBuilder(
        Action assignPendingIds,
        Func<int> nextMessageId,
        Func<int> effectiveContextSize,
        ServerProps serverProps,
        Func<IReadOnlyList<ChatMessage>> conversation,
        Func<IReadOnlyList<SurfacedMemory>> surfaced,
        Func<string?> memoryNag,
        Func<IReadOnlyList<PendingPair>>? pendingPairs = null)
    {
        _assignPendingIds = assignPendingIds;
        _nextMessageId = nextMessageId;
        _effectiveContextSize = effectiveContextSize;
        _serverProps = serverProps;
        _conversation = conversation;
        _surfaced = surfaced;
        _memoryNag = memoryNag;
        _pendingPairs = pendingPairs ?? (() => []);
    }

    /// <summary>
    /// Чистая сборка без побочных эффектов менеджмента памяти (счётчик наг'а не двигается).
    /// Реальный рендер вызывает её же, но с OnRendered() у surfacer'а на стороне владельца —
    /// так разделение «сборка» / «показ» остаётся явным.
    /// Используется пред-отправочным подсчётом токенов: образ блока должен совпадать
    /// с реальным рендером, но «показывать» воспоминания повторно он не вправе.
    /// </summary>
    public StateBlock Build()
    {
        _assignPendingIds();
        // Контекст в блоке — ТОЛЬКО фактический серверный счёт (/tokenize бюджета или
        // Generation последнего хода). Оценок chars/4 нет: не ответили — «неизвестно» (0).
        var context = _serverProps.LastActualPromptTokens(_conversation());

        var state = new StateBlock
        {
            MsgId = _nextMessageId(),
            Time = DateTime.Now,
            ContextUsed = Math.Min(context, _effectiveContextSize()),
            ContextMax = _effectiveContextSize(),
            BuildId = LastBuild()?.Id,
            BuildStatus = LastBuild()?.Status
        };

        // Всплывшие воспоминания: живут до компакции (там пул чистится), дубликаты не повторяются.
        foreach (var memory in _surfaced())
        {
            state.Memories.Add(new StateBlock.MemoryRef
            {
                Id = memory.Id,
                Relevance = memory.Score,
                Content = ToSingleLine(memory.Content, 200)
            });
        }

        // Пары-кандидаты на слияние: бюджет за рендер, чтобы очередь не съедала контекст.
        foreach (var pair in _pendingPairs().Take(3))
        {
            state.SimilarPairs.Add(new StateBlock.MemoryPair(pair.A, pair.B));
        }

        // Наг менеджмента памяти: модель в обсессии не займётся дедупом сама — периодически дёргаем.
        if (_memoryNag() is { } nag)
        {
            state.MemoryNag = nag;
        }

        return state;
    }

    /// <summary>Однострочное содержимое поля блока: парсер режет блок по строкам.</summary>
    private static string ToSingleLine(string text, int maxLength)
    {
        var oneLine = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
    }

    /// <summary>
    /// Последняя запись журнала сборок развёрнутого run/ (или null). Один источник для
    /// state-блока, стартового статуса и заголовка окна; кэш по mtime — journal.json растёт
    /// с каждой сборкой, а читается при каждом рендере.
    /// </summary>
    public static BuildJournalEntry? LastBuild()
    {
        if (!SelfBuildPaths.TryGetDeployedRunRoot(out var runRoot))
        {
            return null;
        }
        _lastBuildCache ??= new FileDependentCache<BuildJournalEntry?>(
            new[] { Path.Combine(runRoot, "journal.json") },
            () => BuildJournal.Load(runRoot).LastOrDefault(),
            initial: null);
        return _lastBuildCache.Get();
    }
}
