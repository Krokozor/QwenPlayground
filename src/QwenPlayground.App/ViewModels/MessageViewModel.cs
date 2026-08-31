using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.App.ViewModels;

/// <summary>Вложение сообщения (артефакт из artifacts/msg_&lt;id&gt;/): имя + превью для картинок.</summary>
public sealed record MessageAttachment(string Name, string FullPath)
{
    private ImageSource? _preview;

    public bool IsImage => ChatPreview.IsImageFile(FullPath);
    public bool IsNotImage => !IsImage;

    /// <summary>Декодируется один раз: WPF-биндинг дёргает геттер на каждом layout-проходе (скролл виртуализированного списка).</summary>
    public ImageSource? Preview => IsImage ? (_preview ??= ChatPreview.Load(FullPath)) : null;

    /// <summary>
    /// Ширина превью под фиксированную высоту 140: картинка растекается по собственному
    /// соотношению сторон, пустых полей нет (в отличие от жёсткого MaxWidth+MaxHeight).
    /// </summary>
    public double PreviewWidth
    {
        get
        {
            if (Preview is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelHeight > 0)
            {
                return Math.Round(MessageAttachmentPreviewHeight * bitmap.PixelWidth / (double)bitmap.PixelHeight);
            }
            return MessageAttachmentPreviewHeight;
        }
    }

    public const double MessageAttachmentPreviewHeight = 140;
}

/// <summary>Облегчённый загрузчик превью: только растровые форматы, без блокировки файла (OnLoad + Freeze).</summary>
internal static class ChatPreview
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".ico"
    };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static ImageSource? Load(string path)
    {
        if (!IsImageFile(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

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

    [ObservableProperty]
    private bool _thinkingClosed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Timestamp))]
    [NotifyPropertyChangedFor(nameof(HasTimestamp))]
    private ChatMessage? _source;

    public bool HasReasoning => Reasoning.Length > 0;
    public bool HasToolCalls => ToolCallCount > 0;
    public bool HasTokenInfo => TokenInfo.Length > 0;

    /// <summary>assistant — единственная роль, для которой осмысленны реролл/продолжить.</summary>
    public bool IsAssistant => Role == "assistant";
    /// <summary>system — служебная роль: рендерится в чате, но без кнопок действий.</summary>
    public bool IsSystem => Role == "system";

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
        foreach (var path in store.GetArtifacts(Source.Id))
        {
            Attachments.Add(new MessageAttachment(Path.GetFileName(path), path));
        }
        HasAttachments = Attachments.Count > 0;
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

        if (message.Generation is { } generation)
        {
            var prompt = generation.PromptTokens?.ToString() ?? "?";
            var completion = generation.CompletionTokens?.ToString() ?? "?";
            TokenInfo = $"tokens: {prompt} + {completion}";
        }
        HasGeneration = message.Generation is not null;
    }

    public static MessageViewModel FromMessage(string role, ChatMessage message)
    {
        var viewModel = new MessageViewModel { Role = role };
        viewModel.ApplyParsed(message);
        return viewModel;
    }
}
