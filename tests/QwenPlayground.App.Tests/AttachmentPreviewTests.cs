using System.IO;
using System.Text;
using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Состояния превью вложения. Каждый тест here — регресс на конкретный симптом бага
/// «скриншоты в чате»: чёрный бокс, залипшее превью, отсутствие кликабельности.
/// </summary>
public sealed class AttachmentPreviewTests : IDisposable
{
    /// <summary>Валидный PNG 1×1 (только сигнатура + IHDR + IEND), чтобы декодеру было что разобрать.</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "qwen_preview_" + Guid.NewGuid().ToString("N"));

    public AttachmentPreviewTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Тест ничего не создал — удалять нечего.
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string WritePng(string name) => WriteFile(name, Convert.FromBase64String(OnePixelPngBase64));

    [Fact]
    public async Task ValidImage_PreviewReady()
    {
        var attachment = new PendingAttachment("shot.png", WritePng("shot.png"));

        var result = await attachment.ReloadPreviewAsync();

        Assert.Equal(PreviewStatus.Ready, result.Status);
        Assert.NotNull(attachment.Preview);
        Assert.True(attachment.HasPreview);
        Assert.False(attachment.IsPreviewBroken);
        Assert.False(attachment.ShowFallback);
    }

    /// <summary>
    /// Регресс на «чёрный скриншот»: расширение картинное, а декод упал. Раньше
    /// Visibility картинки был привязан к IsImage, поэтому UI рисовал пустой Image на
    /// почти чёрном фоне карточки. Теперь HasPreview=false и показывается заглушка.
    /// </summary>
    [Fact]
    public async Task CorruptImage_NoBlackBox_FallsBackToPlaceholder()
    {
        var path = WriteFile("broken.png", Encoding.UTF8.GetBytes("это не png, а мусор"));
        var attachment = new PendingAttachment("broken.png", path);

        var result = await attachment.ReloadPreviewAsync();

        Assert.Equal(PreviewStatus.Unavailable, result.Status);
        Assert.Null(attachment.Preview);
        Assert.True(attachment.IsImage);
        Assert.False(attachment.HasPreview);
        Assert.True(attachment.IsPreviewBroken);
        Assert.True(attachment.ShowFallback);
        Assert.Equal("превью недоступно — открыть", attachment.FallbackText);
    }

    [Fact]
    public async Task MissingImage_ReportsMissing()
    {
        var attachment = new PendingAttachment(
            "gone.png", Path.Combine(_directory, "нет-такого.png"));

        var result = await attachment.ReloadPreviewAsync();

        Assert.Equal(PreviewStatus.Missing, result.Status);
        Assert.True(attachment.IsPreviewBroken);
        Assert.Equal("файл не найден — открыть", attachment.FallbackText);
    }

    /// <summary>Текст/документ: превью не нужно, но чип-«открыть» остаётся (не чёрный бокс).</summary>
    [Fact]
    public async Task NonImage_SkipsDecode_KeepsOpenChip()
    {
        var path = WriteFile("notes.txt", Encoding.UTF8.GetBytes("просто текст"));
        var attachment = new MessageAttachment("notes.txt", path);

        var result = await attachment.ReloadPreviewAsync();

        Assert.Equal(PreviewStatus.NotAnImage, result.Status);
        Assert.False(attachment.IsPreviewBroken);
        Assert.True(attachment.ShowFallback);
        Assert.Equal("открыть", attachment.FallbackText);
    }

    /// <summary>
    /// Регресс на «UI рендерится до того, как файл загружен»: файл дописывается на ходу
    /// (как это делает снапшот-тул или adb pull). Превью обязано дождаться стабилизации и
    /// показать нормальную картинку, а не битый кадр.
    /// </summary>
    [Fact]
    public async Task FileStillBeingWritten_WaitsAndDecodesCleanly()
    {
        var path = Path.Combine(_directory, "growing.png");
        var png = Convert.FromBase64String(OnePixelPngBase64);
        var half = png.Length / 2;

        await File.WriteAllBytesAsync(path, png[..half]);
        var writer = Task.Run(async () =>
        {
            await Task.Delay(250);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(png[half..]);
        });

        var attachment = new PendingAttachment("growing.png", path);
        var result = await attachment.ReloadPreviewAsync();
        await writer;

        Assert.Equal(PreviewStatus.Ready, result.Status);
        Assert.True(attachment.HasPreview);
    }

    /// <summary>
    /// Регресс на «залипает в сломанном состоянии»: неудачный декод запоминался навсегда
    /// (`_preview ??=` в геттере биндинга + record без INPC). Теперь тот же объект после
    /// починки файла честно перечитывает превью.
    /// </summary>
    [Fact]
    public async Task FailedPreview_DoesNotStick_RecoversAfterFileIsFixed()
    {
        var path = WriteFile("later.png", Encoding.UTF8.GetBytes("пока не картинка"));
        var attachment = new MessageAttachment("later.png", path);

        Assert.Equal(PreviewStatus.Unavailable, (await attachment.ReloadPreviewAsync()).Status);

        await File.WriteAllBytesAsync(path, Convert.FromBase64String(OnePixelPngBase64));

        Assert.Equal(PreviewStatus.Ready, (await attachment.ReloadPreviewAsync()).Status);
        Assert.True(attachment.HasPreview);
        Assert.False(attachment.IsPreviewBroken);
    }

    /// <summary>
    /// Старый результат не должен затирать новый: если перезагрузку запустили, пока
    /// предыдущий декод ещё идёт, выигрывает последний.
    /// </summary>
    [Fact]
    public async Task Reload_DoesNotLeaveStaleState()
    {
        var path = WriteFile("race.png", Encoding.UTF8.GetBytes("мусор"));
        var attachment = new MessageAttachment("race.png", path);

        var first = attachment.ReloadPreviewAsync();
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(OnePixelPngBase64));
        var second = attachment.ReloadPreviewAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(PreviewStatus.Ready, attachment.Status);
        Assert.True(attachment.HasPreview);
    }

    /// <summary>
    /// Битмап создаётся в фоновом потоке и отдаётся в UI-биндинг: без Freeze WPF бросил бы
    /// InvalidOperationException при первой отрисовке. Проверяем контракт заранее.
    /// </summary>
    [Fact]
    public async Task Preview_IsFrozen_ForCrossThreadUse()
    {
        var attachment = new MessageAttachment("shot.png", WritePng("shot.png"));

        await attachment.ReloadPreviewAsync();

        Assert.NotNull(attachment.Preview);
        Assert.True(attachment.Preview.IsFrozen);
    }

    [Fact]
    public void PreviewWidth_ScalesToAspectRatio()
    {
        var attachment = new MessageAttachment("shot.png", WritePng("shot.png"));

        // Квадратная 1×1 превращается в квадрат той же высоты, что и в пузыре.
        Assert.Equal(
            MessageAttachment.MessageAttachmentPreviewHeight,
            attachment.PreviewWidth,
            precision: 3);
    }

    [Fact]
    public void ReloadPreviewCommand_IsAvailable()
    {
        var attachment = new MessageAttachment("shot.png", WritePng("shot.png"));

        Assert.NotNull(attachment.ReloadPreviewCommand);
        Assert.True(attachment.ReloadPreviewCommand.CanExecute(null));
    }
}
