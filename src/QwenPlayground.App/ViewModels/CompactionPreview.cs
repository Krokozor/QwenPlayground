using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Живое превью компакции: токены суммаризации стримятся в буфер и публикуются в UI с
/// троттлингом (~50 мс) — работает и для простой компакции, и для конвейера L1/L2/L3
/// main-агента (этапы разделены заголовками «── … ──»). Всё пишется/читается на потоке UI
/// (async-продолжение компакции захватывает Dispatcher).
/// </summary>
public partial class CompactionPreview : ObservableObject
{
    private readonly StringBuilder _buffer = new();
    private readonly Stopwatch _throttle = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string _stage = string.Empty;

    public bool HasPreview => Preview.Length > 0;

    /// <summary>Панель live-превью видна во время компакции или пока остался прошлый вывод.</summary>
    public bool ShowPanel => IsActive || HasPreview;

    /// <summary>Начало компакции: чистим буфер и поднимаем панель.</summary>
    public void Begin()
    {
        _buffer.Clear();
        Preview = string.Empty;
        Stage = string.Empty;
        IsActive = true;
        _throttle.Restart();
    }

    /// <summary>Очередной этап конвейера: заголовок-разделитель в превью.</summary>
    public void NewStage(string name)
    {
        Stage = name;
        if (_buffer.Length > 0)
        {
            _buffer.AppendLine().AppendLine();
        }
        _buffer.Append("── ").Append(name).Append(" ──").AppendLine();
        Publish();
    }

    /// <summary>Стриминг токенов: в буфер + публикация с троттлингом.</summary>
    public void Append(string chunk)
    {
        _buffer.Append(chunk);
        Publish();
    }

    /// <summary>Публикация превью с троттлингом: стрим быстрее, чем рендер (не чаще раза в 50 мс).</summary>
    public void Publish()
    {
        if (!_throttle.IsRunning || _throttle.ElapsedMilliseconds >= 50)
        {
            Preview = _buffer.ToString();
            _throttle.Restart();
        }
    }

    /// <summary>Финальный сброс буфера в UI после завершения компакции/шага.</summary>
    public void Flush() => Preview = _buffer.ToString();

    /// <summary>Окончание: финальная публикация + панель гаснет (превью остаётся на экране).</summary>
    public void End()
    {
        Flush();
        IsActive = false;
    }
}
