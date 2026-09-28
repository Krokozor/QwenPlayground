using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Общая часть моделей вложения: <see cref="PendingAttachment"/> (до отправки) и
/// <see cref="MessageAttachment"/> (артефакт в пузыре). Разведённые на два record'а
/// превью были идентичны, но по-разному ломались, поэтому состояние живёт здесь один раз.
///
/// Ключевое отличие от прежней реализации: превью грузится **асинхронно, вне UI-потока,
/// с явным состоянием** и **не кэширует неудачу намертво**. Раньше это был
/// `_preview ??= ChatPreview.Load(...)` внутри геттера биндинга — синхронный декод
/// полноразмерного файла на UI-потоке, непустой-битый-результат запоминался навсегда,
/// а record не был INPC, то есть WPF не мог переспросить. Отсюда были оба симптома:
/// «залипшее» превью и «чёрный скриншот».
/// </summary>
public abstract class AttachmentPreview : ObservableObject
{
    private readonly Dispatcher? _dispatcher;
    private int _loadGeneration;
    private ImageSource? _preview;
    private PreviewStatus _status = PreviewStatus.Loading;

    protected AttachmentPreview(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
        // Application.Current — глобальный, не привязан к потоку: даже если модель
        // создана из фонового потока (load_image → OnToolFinished), диспетчер здесь
        // правильный.
        _dispatcher = Application.Current?.Dispatcher;
        ReloadPreviewCommand = new RelayCommand(() => _ = ReloadPreviewAsync());
    }

    public string Name { get; }

    /// <summary>Путь к файлу: у PendingAttachment — оригинал, у MessageAttachment — копия в артефактах.</summary>
    public string FullPath { get; }

    /// <summary>По расширению: уходит ли файл в мультимодальность. Не значит «превью получится».</summary>
    public bool IsImage => ChatPreview.IsImageFile(FullPath);

    /// <summary>Bitmap превью (null, если его нет или декод не удался).</summary>
    public ImageSource? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(PreviewWidth));
            }
        }
    }

    public PreviewStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(IsPreviewBroken));
                OnPropertyChanged(nameof(ShowFallback));
                OnPropertyChanged(nameof(FallbackGlyph));
                OnPropertyChanged(nameof(FallbackText));
            }
        }
    }

    /// <summary>Есть что показать: Image видна только по этому флагу (не по IsImage).</summary>
    public bool HasPreview => _status == PreviewStatus.Ready && _preview is not null;

    /// <summary>Картинка по расширению есть, а превью получить не удалось — рисуем заглушку, а не чёрный бокс.</summary>
    public bool IsPreviewBroken => IsImage && _status is PreviewStatus.Missing or PreviewStatus.Unavailable;

    /// <summary>Показать вместо картинки чип-заглушку: не-картинка или сломанное превью.</summary>
    public bool ShowFallback => !IsImage || IsPreviewBroken;

    /// <summary>Значок заглушки (у текстовых файлов — 📄, у сломанного превью — 🖼).</summary>
    public string FallbackGlyph => !IsImage ? "\U0001F4C4" : "\U0001F5BC";

    /// <summary>Подпись заглушки. Для сломанного превью объясняет, что делать дальше.</summary>
    public string FallbackText => _status switch
    {
        PreviewStatus.Missing => "файл не найден — открыть",
        PreviewStatus.Unavailable => "превью недоступно — открыть",
        _ => "открыть"
    };

    /// <summary>
    /// Ширина превью под фиксированную высоту: картинка растекается по собственному
    /// соотношению сторон, пустых полей нет (в отличие от жёсткого MaxWidth+MaxHeight).
    /// </summary>
    public double PreviewWidth
    {
        get
        {
            if (Preview is BitmapSource bitmap && bitmap.PixelHeight > 0)
            {
                return Math.Round(PreviewHeight * bitmap.PixelWidth / (double)bitmap.PixelHeight);
            }
            return PreviewHeight;
        }
    }

    private double PreviewHeight => MessageAttachment.MessageAttachmentPreviewHeight;

    /// <summary>
    /// Перезагрузить превью. Явный выход из «залипшего» состояния — например, если
    /// владелец дописал файл уже после того, как превью признали недоступным.
    /// </summary>
    public ICommand ReloadPreviewCommand { get; }

    /// <summary>Запустить загрузку превью, не блокируя вызывающего (fire-and-forget).</summary>
    public void BeginLoadPreview() => _ = ReloadPreviewAsync();

    /// <summary>
    /// Дождаться готовности превью (тесты, places, где результат нужен сразу).
    /// </summary>
    public Task<PreviewLoadResult> ReloadPreviewAsync() => LoadAsync();

    private async Task<PreviewLoadResult> LoadAsync()
    {
        if (!IsImage)
        {
            SetStatusOnUi(PreviewStatus.NotAnImage);
            return PreviewLoadResult.Failed(PreviewStatus.NotAnImage);
        }

        // Поколение отсекает устаревший результат: если владелец нажал «перезагрузить»,
        // пока первый декод ещё идёт, побеждает только последний — иначе старый ответ
        // снова мог бы «залить» свежий результат.
        var generation = Interlocked.Increment(ref _loadGeneration);
        SetStatusOnUi(PreviewStatus.Loading);

        PreviewLoadResult result;
        try
        {
            result = await ChatPreview.LoadAsync(FullPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Сюда попадает только отмена (и unforeseen из фонового потока). Превью —
            // украшение, ронять из-за него нечего.
            DiagnosticsLog.Log($"preview load crashed: {FullPath}: {ex.GetType().Name}: {ex.Message}");
            result = PreviewLoadResult.Failed(PreviewStatus.Unavailable);
        }

        if (generation != Volatile.Read(ref _loadGeneration))
        {
            return result;
        }

        await ApplyOnUiAsync(() =>
        {
            Preview = result.Image;
            Status = result.Status;
        }).ConfigureAwait(false);

        return result;
    }

    private void SetStatusOnUi(PreviewStatus status)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            Status = status;
            return;
        }
        _dispatcher.BeginInvoke(() => Status = status);
    }

    /// <summary>INPC для WPF обязан слаться на UI-поток — маршалим явно.</summary>
    private Task ApplyOnUiAsync(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(action).Task;
    }
}
