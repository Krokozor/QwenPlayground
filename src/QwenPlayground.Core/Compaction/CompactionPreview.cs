using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace QwenPlayground.Core.Compaction;

/// <summary>
/// Живое превью компакции: токены суммаризации стримятся в буфер и публикуются в UI с
/// троттлингом (~50 мс) — работает и для простой компакции, и для конвейера L1/L2/L3
/// main-агента (этапы разделены заголовками «── … ──»). Всё пишется/читается на потоке UI
/// (async-продолжение компакции захватывает Dispatcher).
///
/// Core-класс без MVVM: INotifyPropertyChanged поднят вручную — WPF-биндинги работают
/// как есть, а консольный/веб-UI сможет читать свойства и подписываться на события
/// без CommunityToolkit.
/// </summary>
public sealed class CompactionPreview : INotifyPropertyChanged
{
    private readonly StringBuilder _buffer = new();
    private readonly Stopwatch _throttle = new();

    private bool _isActive;
    private string _preview = string.Empty;
    private string _stage = string.Empty;
    private bool _isHidden;

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(ShowPanel));
        }
    }

    public string Preview
    {
        get => _preview;
        private set
        {
            if (_preview == value) return;
            _preview = value;
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(ShowPanel));
        }
    }

    public string Stage
    {
        get => _stage;
        private set
        {
            if (Stage == value) return;
            _stage = value;
            OnPropertyChanged(nameof(Stage));
        }
    }

    /// <summary>Пользователь скрыл панель «×» (превью сохраняется; новая компакция откроет панель сама).</summary>
    public bool IsHidden
    {
        get => _isHidden;
        private set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(ShowPanel));
        }
    }

    public bool HasPreview => Preview.Length > 0;

    /// <summary>В панели есть что показывать: идёт компакция или остался прошлый вывод.</summary>
    public bool HasContent => IsActive || HasPreview;

    /// <summary>
    /// Панель live-превью видна во время компакции или пока остался прошлый вывод,
    /// пока пользователь не скрыл её «×» (кнопка «Сжатие» в тулбаре открывает обратно).
    /// </summary>
    public bool ShowPanel => HasContent && !IsHidden;

    /// <summary>Начало компакции: чистим буфер и поднимаем панель (перекрывает «×» — авто-открытие, как раньше).</summary>
    public void Begin()
    {
        _buffer.Clear();
        Preview = string.Empty;
        Stage = string.Empty;
        IsActive = true;
        IsHidden = false;
        _throttle.Restart();
    }

    /// <summary>Скрыть панель «×»: превью сохраняется, панель закрывается до открытия из тулбара.</summary>
    public void Hide() => IsHidden = true;

    /// <summary>Открыть панель из тулбара (кнопка активна, когда есть что показывать).</summary>
    public void Open() => IsHidden = false;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
