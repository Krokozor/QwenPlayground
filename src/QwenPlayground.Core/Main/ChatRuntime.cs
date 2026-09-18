using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Main;

/// <summary>
/// Рантайм чата — бандл пер-разговорных сервисов одного окна чата: история (Log),
/// FSM хода (ChatState), живое превью сжатия (Compaction), реколл памяти (MemorySurfacer),
/// сборка промпта (PromptAssembler/StateBlocks/Pipeline), драфт ввода (Draft),
/// сжатие контекста (Maintenance), ход (Turns). Общие сервисы (Tools, ServerProps,
/// Background, память) не входят — они живут в Main и передаются при сборке.
///
/// Не путать с <see cref="QwenPlayground.Core.Runtime.AgentRuntime"/> — скоупом агента
/// (профиль настроек + маршрут интерактива): ChatRuntime — «что обслуживает разговор»,
/// скоуп — «в каком профиле и с каким интерактивом исполняется ход».
///
/// Рантайм главного окна динамичен по сессии: селектор переключает сессии, и
/// SessionId() возвращает текущую. Окна субагентов (стадия C) закреплены за своей
/// сессией: SessionId() возвращает её id.
///
/// Сервисы собирает композиционный корень (Main — он знает порядок); здесь только
/// бандл и вычисленные свойства.
/// </summary>
public sealed class ChatRuntime {
    // Заполняет композиционный корень (Main) — он знает порядок сборки; internal,
    // потому что порядок проходит через SessionController (создаётся между сервисами).
    public ChatLog Log { get; internal set; } = new();
    public ChatStateMachine ChatState { get; internal set; } = new();
    public CompactionPreview Compaction { get; internal set; } = new();
    public MemorySurfacer MemorySurfacer { get; internal set; } = new();
    public SystemPromptAssembler PromptAssembler { get; internal set; } = null!;
    public StateBlockBuilder StateBlocks { get; internal set; } = null!;
    public PromptPipeline Pipeline { get; internal set; } = null!;
    public ContextMaintenance Maintenance { get; internal set; } = null!;
    public DraftKeeper Draft { get; internal set; } = null!;
    public TurnPipeline Turns { get; internal set; } = null!;

    /// <summary>
    /// Сессия рантайма: главное окно — текущая (селектор), субагент — закреплённая.
    /// </summary>
    public Func<string> SessionId { get; }

    // Ключи профилей закреплённого рантайма (фабрика). Main-рантайм: null — ключи
    // знает SessionController (текущая сессия).
    public string? SamplerKey { get; internal set; }
    public string? PromptKey { get; internal set; }
    public string? StateBlockKey { get; internal set; }

    private readonly ServerProps _serverProps;

    public ChatRuntime(Func<string> sessionId, ServerProps serverProps) {
        SessionId = sessionId;
        _serverProps = serverProps;
    }

    /// <summary>
    /// Эффективный размер окна: реальный n_ctx сервера (если известен), иначе настроенный
    /// ContextSize. Для проверки «влезет ли» сравниваем именно с ним — это то, что реально
    /// спрашивает сервер.
    /// </summary>
    public int EffectiveContextSize =>
        Math.Min(AppSettings.Get().ContextSize, _serverProps.NContext ?? AppSettings.Get().ContextSize);
}
