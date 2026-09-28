using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.App.ViewModels;

public partial class MessageViewModel : ObservableObject
{
    // UnsafeRelaxedJsonEscaping: кириллица и прочий не-ASCII выводятся как есть,
    // а не как \u041F\u0440\u0438... — иначе в чате сырая JSON-каша вместо читаемого текста.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FormatToolCall(string name, System.Text.Json.Nodes.JsonNode? arguments) =>
        $"{name}\n{UnescapeJsonForDisplay(arguments?.ToJsonString(IndentedJson) ?? string.Empty)}";

    /// <summary>
    /// Single-pass unescape of JSON string values for display: \n → newline, \t → tab, etc.
    /// Tracks in/out-of-string state so \\n (literal backslash + n) is NOT turned into a newline.
    /// </summary>
    private static string UnescapeJsonForDisplay(string json)
    {
        if (json.Length == 0) return json;
        var sb = new StringBuilder(json.Length);
        bool inString = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length &&
                                ushort.TryParse(json.AsSpan(i + 1, 4), out ushort code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else
                            {
                                sb.Append('\\').Append('u');
                            }
                            break;
                        default: sb.Append('\\').Append(next); break;
                    }
                }
                else if (c == '"')
                {
                    inString = false;
                    sb.Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                    inString = true;
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssistant))]
    [NotifyPropertyChangedFor(nameof(IsSystem))]
    private string _role = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolCalls))]
    private int _toolCallCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokenInfo))]
    private string _tokenInfo = string.Empty;

    [ObservableProperty]
    private bool _hasGeneration;

    /// <summary>Есть ли сохранённый промпт (Generation.Prompt непустой). Кнопка «промпт» видна только тогда.</summary>
    [ObservableProperty]
    private bool _hasPrompt;

    [ObservableProperty]
    private bool _thinkingClosed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Timestamp))]
    [NotifyPropertyChangedFor(nameof(HasTimestamp))]
    private ChatMessage? _source;

    public bool HasReasoning => Reasoning.Length > 0;
    public bool HasToolCalls => ToolCallCount > 0;
    public bool HasTokenInfo => TokenInfo.Length > 0;

    /// <summary>
    /// Вызов spawn_subagent в этом сообщении: встроенный чат субагента в пузыре
    /// (SubagentChat — VM разговора субагента, ставит MainViewModel при создании
    /// субагента) + кнопка «⤢» (popout в отдельное окно на той же VM).
    /// </summary>
    public bool HasSpawnSubagentCall { get; private set; }

    /// <summary>
    /// Чат субагента (VM), встроенный в пузырь: null — не spawn-вызов или субагент ещё
    /// не создан. Один инстанс на всех: пузырь и popout-окно рендерят один разговор.
    /// </summary>
    [ObservableProperty]
    private ChatViewModel? _subagentChat;

    /// <summary>
    /// Встроенный чат развёрнут. Авто: true на спавне (видна живая работа), false на
    /// завершении (показываем отчёт); вручную — шеврон в хедере пузыря.
    /// </summary>
    [ObservableProperty]
    private bool _isSubagentExpanded;

    /// <summary>Встроенный блок субагента виден (у spawn-вызова есть чат).</summary>
    public bool HasEmbeddedSubagent => SubagentChat is not null;

    partial void OnSubagentChatChanged(ChatViewModel? value) =>
        OnPropertyChanged(nameof(HasEmbeddedSubagent));

    /// <summary>Шеврон в хедере пузыря: ▸ свёрнут / ▾ развёрнут.</summary>
    public string SubagentChevron => IsSubagentExpanded ? "▾" : "▸";

    partial void OnIsSubagentExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(SubagentChevron));

    /// <summary>Шеврон в хедере пузыря: показать/скрыть чат субагента.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleSubagentExpanded() => IsSubagentExpanded = !IsSubagentExpanded;

    /// <summary>
    /// Ввод субагента (встроенный режим) раскрыт: по дефолту скрыт (компактность),
    /// «✎» в хедере раскрывает — чтобы ткнуть субагента, ушедшего в луп.
    /// </summary>
    [ObservableProperty]
    private bool _showSubagentInput;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleSubagentInput() => ShowSubagentInput = !ShowSubagentInput;

    /// <summary>assistant — единственная роль, для которой осмысленны реролл/продолжить.</summary>
    public bool IsAssistant => Role == "assistant";
    /// <summary>system — служебная роль: рендерится в чате, но без кнопок действий.</summary>
    public bool IsSystem => Role is "system" or "confirm";

    /// <summary>
    /// Время из state-блока сообщения (момент генерации) — ровно то, что видела модель
    /// на этом ходе. Для сообщений без блока (user/tool/старые) — null, метка скрывается.
    /// </summary>
    public string? Timestamp =>
        Source?.StateBlock?.Time is { } time ? time.ToString("yyyy-MM-dd HH:mm:ss") : null;

    public bool HasTimestamp => Timestamp is not null;

    public string? GetInspectionText()
    {
        if (Source?.Generation is not { } generation)
        {
            return null;
        }
        return generation.Prompt + "\n\n========== RAW OUTPUT ==========\n\n" + generation.RawOutput;
    }

    public ObservableCollection<string> ToolCalls { get; } = new();

    /// <summary>Прикреплённые к этому сообщению файлы (копии в artifacts/msg_&lt;id&gt;/).</summary>
    public ObservableCollection<MessageAttachment> Attachments { get; } = new();

    [ObservableProperty]
    private bool _hasAttachments;

    /// <summary>Загружает вложения сообщения из artifacts/msg_&lt;id&gt;/ каталога сессии.</summary>
    public void LoadArtifacts(string sessionDir)
    {
        Attachments.Clear();
        if (Source is null)
        {
            HasAttachments = false;
            return;
        }
        var store = new MessageMetaStore(sessionDir);
        // Мультимодальные вложения (прямо в msg_<id>/) — превью в UI.
        foreach (var path in store.GetArtifacts(Source.Id))
        {
            AddAttachment(path);
        }
        // Анонсируемые (не мультимодальные) вложения — в подпапке attachments/ (чип, не превью).
        var attachmentsDir = Path.Combine(store.ArtifactsDir(Source.Id), "attachments");
        if (Directory.Exists(attachmentsDir))
        {
            foreach (var path in Directory.GetFiles(attachmentsDir))
            {
                AddAttachment(path);
            }
        }
        HasAttachments = Attachments.Count > 0;
    }

    /// <summary>
    /// Добавить вложение и запустить загрузку превью в фоне. Раньше превью декодировалось
    /// синхронно в геттере биндинга — то есть в том же проходе разметки, что и этот метод,
    /// на UI-потоке и полноразмерно. Теперь декод асинхронный, а состояние честное:
    /// не показалось — значит покажется заглушка, а не чёрный прямоугольник.
    /// </summary>
    private void AddAttachment(string path)
    {
        var attachment = new MessageAttachment(Path.GetFileName(path), path);
        Attachments.Add(attachment);
        attachment.BeginLoadPreview();
    }

    // ── Живой стрим ──────────────────────────────────────────────────────────────────
    //
    // Хот-путь генерации: чанки приходят десятки раз в секунду. Прежний вариант
    // (UpdateFromRaw(raw.ToString()) на каждый чанк) делал полную копию накопленной
    // строки + IndexOf по ней + два Substring на КАЖДЫЙ токен — O(n²) по ходу и полный
    // ре-рендер TextBlock дважды за токен. Теперь разбор чанка инкрементальный
    // (ThinkStreamSplitter, тестируется в Core), а публикация в UI троттлится ~50 мс
    // (паттерн CompactionPreview).

    private readonly ThinkStreamSplitter _stream = new();
    private readonly System.Diagnostics.Stopwatch _streamThrottle = new();
    /// <summary>Чанки осмысленны только между BeginStreaming и ApplyParsed: после парса стрим мёртв.</summary>
    private bool _streamActive;

    /// <summary>Начало стрима: сброс состояния; prefill — уже накопленный вывод (continue-ход).</summary>
    public void BeginStreaming(string prefill)
    {
        _stream.Reset();
        _stream.AppendPrefill(prefill);
        _streamThrottle.Restart();
        _streamActive = true;
    }

    /// <summary>Очередной чанк: инкрементальный разбор + троттлинг-публикация в свойства биндинга.</summary>
    public void AppendStreamChunk(string chunk)
    {
        if (!_streamActive)
        {
            return;
        }
        _stream.Append(chunk);
        if (!_streamThrottle.IsRunning || _streamThrottle.ElapsedMilliseconds >= 50)
        {
            Reasoning = _stream.Reasoning;
            Content = _stream.Content;
            _streamThrottle.Restart();
        }
    }

    /// <summary>
    /// Финальная публикация: разрешает отложенный хвост (последние ThinkClose.Length-1 символов,
    /// возможное начало маркера) как текст. Вызывается в конце потока — до ApplyParsed.
    /// </summary>
    public void FlushStreaming()
    {
        if (!_streamActive)
        {
            return;
        }
        _stream.Flush();
        Reasoning = _stream.Reasoning;
        Content = _stream.Content;
    }

    public void ApplyParsed(ChatMessage message)
    {
        _streamActive = false; // парс финален: стрим этого view завершён
        Source = message;
        Reasoning = message.Reasoning ?? string.Empty;
        Content = message.Content;
        ThinkingClosed = message.ThinkingClosed;
        ToolCalls.Clear();
        if (message.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (var call in toolCalls)
            {
                ToolCalls.Add(FormatToolCall(call.Name, call.Arguments));
            }
        }
        ToolCallCount = ToolCalls.Count;
        HasSpawnSubagentCall = message.ToolCalls is { Count: > 0 } toolCalls2 &&
            toolCalls2.Any(call => call.Name == "spawn_subagent");

        if (message.Generation is { } generation)
        {
            var prompt = generation.PromptTokens?.ToString() ?? "?";
            var completion = generation.CompletionTokens?.ToString() ?? "?";
            TokenInfo = $"tokens: {prompt} + {completion}";
        }
        HasGeneration = message.Generation is not null;
        HasPrompt = message.Generation?.Prompt.Length > 0;
    }

    public static MessageViewModel FromMessage(string role, ChatMessage message)
    {
        var viewModel = new MessageViewModel { Role = role };
        viewModel.ApplyParsed(message);
        return viewModel;
    }

    // ── Карточка подтверждения (роль "confirm") ─────────────────────────────────────
    // Не сообщение чата (не персистится в chat.json — там записан результат тула),
    // а UI-элемент: команда + решение. Живёт в окне рантайма, который попросил
    // подтверждения (main / субагент / откреплённое окно).

    private bool _confirmResolved;
    private string _confirmState = "⏳ ожидает решения";
    private Brush _confirmStateBrush = Brushes.Goldenrod;
    private bool _yoloEnabled;

    /// <summary>Сообщение — карточка подтверждения опасной команды.</summary>
    public bool IsConfirm => Role == "confirm";

    /// <summary>Решение принято (кнопки скрываются, остаётся запись в истории).</summary>
    public bool ConfirmResolved { get => _confirmResolved; private set => SetProperty(ref _confirmResolved, value); }

    /// <summary>Строка состояния карточки («⏳ ожидает решения» / «✅ разрешено» / …).</summary>
    public string ConfirmState { get => _confirmState; private set => SetProperty(ref _confirmState, value); }

    /// <summary>Цвет строки состояния (золотой — ожидание, зелёный — разрешено, красный — запрещено).</summary>
    public Brush ConfirmStateBrush { get => _confirmStateBrush; private set => SetProperty(ref _confirmStateBrush, value); }

    /// <summary>Чекбокс YOLO на карточке (визуальное состояние; включение — через команду).</summary>
    public bool YoloEnabled { get => _yoloEnabled; set => SetProperty(ref _yoloEnabled, value); }

    /// <summary>
    /// Разрешение решения: ставит состояние и оповещает владельца (TCS в ChatViewModel).
    /// Повторное разрешение — no-op (карточка уже решена).
    /// </summary>
    public void ResolveConfirmation(bool allowed, bool yolo = false)
    {
        if (ConfirmResolved)
        {
            return;
        }
        ConfirmResolved = true;
        ConfirmState = allowed ? (yolo ? "✅ разрешено (YOLO)" : "✅ разрешено") : "❌ запрещено";
        ConfirmStateBrush = allowed ? Brushes.LimeGreen : Brushes.OrangeRed;
        ConfirmResolve?.Invoke(allowed);
    }

    /// <summary>Ход отменён/завершён, пока карточка ждёт — честный отказ.</summary>
    public void ResolveCancelled()
    {
        if (ConfirmResolved)
        {
            return;
        }
        ConfirmResolved = true;
        ConfirmState = "⛔ отменено (ход завершён)";
        ConfirmStateBrush = Brushes.Gray;
        ConfirmResolve?.Invoke(false);
    }

    /// <summary>Оповещение владельца о решении (ставит ChatViewModel; null — карточка без ожидания).</summary>
    internal Action<bool>? ConfirmResolve;

    /// <summary>Карточка подтверждения: вопрос (команда) в Content, решение — через кнопки.</summary>
    public static MessageViewModel Confirmation(string question) =>
        new() { Role = "confirm", Content = question };
}
