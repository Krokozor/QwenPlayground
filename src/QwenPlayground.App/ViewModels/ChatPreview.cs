using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Состояние превью вложения. Отдельное от «по расширению это картинка»: решает, что
/// именно рисует UI — картинку, честную заглушку или ничего. Раньше Visibility картинки
/// был привязан к IsImage, поэтому неудачный декод давал пустой Image на почти чёрном
/// фоне карточки — пользователь видел «чёрный скриншот».
/// </summary>
public enum PreviewStatus
{
    /// <summary>Не картинка (по расширению) — превью не нужно.</summary>
    NotAnImage,

    /// <summary>Декодируем (фоновый поток, UI не заблокирован).</summary>
    Loading,

    /// <summary>Есть bitmap.</summary>
    Ready,

    /// <summary>Файла нет (удалили/перенесли).</summary>
    Missing,

    /// <summary>Файл есть, но декод не удался: формат не читается WIC или файл битый.</summary>
    Unavailable
}

/// <summary>Итог загрузки превью: статус + картинка (Image не null только при Ready).</summary>
public sealed record PreviewLoadResult(PreviewStatus Status, ImageSource? Image)
{
    public static PreviewLoadResult Ready(ImageSource image) => new(PreviewStatus.Ready, image);

    public static PreviewLoadResult Failed(PreviewStatus status) => new(status, null);
}

/// <summary>
/// Загрузчик превью вложений. Ключевые решения (каждое — под конкретный баг):
///
/// <list type="bullet">
/// <item>Декод из <b>потока байтов</b> (StreamSource), а не через UriSource. UriSource
/// идёт через глобальный URI-кэш WPF: перезаписанный по тому же пути файл продолжал
/// отдавать старый битмап, и превью «залипало» навсегда.</item>
/// <item>Файл сначала дожидается стабилизации (длина + метка времени перестали меняться),
/// а неудачный декод означает «прочитать заново». Раньше превью декодировалось в том же
/// проходе разметки, что и копирование, и файл, который ещё писался (снапшот-тул,
/// screencap/adb pull, загрузка), давал битый кадр — и он запоминался навсегда.</item>
/// <item>Чтение с FileShare.ReadWrite — чужой писатель больше не блокирует чтение.</item>
/// <item>DecodePixelHeight — не держим в памяти полноразмерные кадры (OnLoad +
/// без даунскейла: 4K-скриншот 32 бита = ~33 МБ на картинку, и их десятки за сессию).</item>
/// <item>Каждая неудача пишется в DiagnosticsLog. Раньше был тихий catch { return null },
/// из-за чего баг был недиагностируемым в принципе.</item>
/// </list>
/// </summary>
internal static class ChatPreview
{
    /// <summary>Высота декодирования. 140 — логическая высота превью в пузыре, ×2 на HiDPI.</summary>
    public const int DecodeHeight = 280;

    /// <summary>Пауза между замерами размера файла (ждём, пока допишется).</summary>
    private const int SettleDelayMs = 100;

    /// <summary>Сколько раз пробуем прочитать и декодировать файл (≈0.5–1 c суммарно).</summary>
    private const int MaxAttempts = 4;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".ico"
    };

    /// <summary>
    /// Расширение — только признак «картинка ли это» для маршрутизации в мультимодальность.
    /// Не гарантия, что WIC сумеет превью отрисовать: .webp/.tiff в списке есть, а
    /// кодека в Windows может не быть — тогда превью честно уходит в Unavailable,
    /// и UI показывает заглушку, а модель по-прежнему получает картинку (base64).
    /// </summary>
    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Дождаться стабильного файла и декодировать превью. Не бросает.
    ///
    /// Ключевое: единственный достоверный признак «файл готов» — <b>успешный декод</b>.
    /// Проверка «длина файла не менялась 100 мс» обманута паузой писателя: копирование
    /// большого PNG идёт блоками с паузами, и в такой паузе файл выглядит стабильным —
    /// превью получалось битым. Поэтому неудачный декод — не финал, а повод прочитать
    /// файл заново. Именно это и давало «UI отрисовал раньше, чем файл докачался».
    /// </summary>
    public static async Task<PreviewLoadResult> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!IsImageFile(path))
        {
            return PreviewLoadResult.Failed(PreviewStatus.NotAnImage);
        }

        var seen = false;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (outcome, bytes) = await ReadSettledAsync(path, ct).ConfigureAwait(false);
            if (outcome == ReadOutcome.Missing)
            {
                // Файла пока нет — снапшот-тул мог ещё не создать его. Не считаем провалом.
                continue;
            }

            seen = true;
            if (bytes is not null)
            {
                var image = TryDecode(bytes, path);
                if (image is not null)
                {
                    return PreviewLoadResult.Ready(image);
                }
                DiagnosticsLog.Log($"preview decode failed (attempt {attempt}/{MaxAttempts}): {path}");
            }
            // Файл ещё дописывается либо декод не удался — пробуем прочитать заново.
        }

        DiagnosticsLog.Log($"preview gave up (file seen: {seen}): {path}");
        return PreviewLoadResult.Failed(seen ? PreviewStatus.Unavailable : PreviewStatus.Missing);
    }

    private enum ReadOutcome
    {
        /// <summary>Файл стабилен, байты прочитаны.</summary>
        Ok,

        /// <summary>Файла нет (или он пустой).</summary>
        Missing,

        /// <summary>Файл меняется прямо сейчас — читать рано.</summary>
        Unstable
    }

    /// <summary>
    /// Два замера (длина + LastWriteTime) с паузой между ними: если совпали — файл
    /// вряд ли пишется и его можно читать. Иначе Unstable: читатель сам решит, пробовать
    /// ли ещё раз.
    /// </summary>
    private static async Task<(ReadOutcome Outcome, byte[]? Bytes)> ReadSettledAsync(
        string path, CancellationToken ct)
    {
        try
        {
            if (Probe(path) is not { } first)
            {
                return (ReadOutcome.Missing, null);
            }

            await Task.Delay(SettleDelayMs, ct).ConfigureAwait(false);

            if (Probe(path) != first)
            {
                return (ReadOutcome.Unstable, null);
            }

            return (ReadOutcome.Ok, ReadBytes(path));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // Файл занят эксклюзивно (пишущий не дал FileShare) — следующая попытка.
            return (ReadOutcome.Unstable, null);
        }
        catch (UnauthorizedAccessException)
        {
            // Нет прав на чтение — превью не покажем, но и не уроним.
            return (ReadOutcome.Missing, null);
        }
    }

    /// <summary>Длина + метка времени файла; null — файла нет или он пустой.</summary>
    private static (long Length, long WriteTicks)? Probe(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    /// <summary>
    /// Чтение с FileShare.ReadWrite|FileShare.Delete: снапшот-тул или загрузка могут
    /// держать файл открытым на запись, и это не должно ронять превью.
    /// </summary>
    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Декод байтов в bitmap. Любая ошибка WIC — неудача превью, не падение UI.</summary>
    private static ImageSource? TryDecode(byte[] bytes, string path)
    {
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = source;
            bitmap.DecodePixelHeight = DecodeHeight;
            bitmap.EndInit();
            // Freeze обязателен: bitmap создаётся в фоновом потоке, а биндинг читает
            // его на UI. Без Freeze — InvalidOperationException при отдаче в UI.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            // WIC бросает NotSupportedException (нет кодека формата), ArgumentException
            // (мусор в заголовке) и др. — все они означают одно: превью не показать.
            DiagnosticsLog.Log(
                $"preview decode failed ({bytes.Length} bytes): {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
