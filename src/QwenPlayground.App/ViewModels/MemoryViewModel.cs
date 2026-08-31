using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// «Витрина памяти» — ручной валидатор ассоциативной памяти (как в NekoBot, где владелец
/// лично гонял текст через классификатор). Ввод текста → классификация (категории A-Z + эмодзи)
/// на CompanionEndpoint → распределения слоёв + сырые буквы модели; кнопка «Реколл» — топ-факты
/// по вектору диалога. Владелец видит не только результат, но и поведение промптов.
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly MemoryStore _store = new();
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    [ObservableProperty]
    private string _testText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Введите текст и нажмите «Классифицировать».";

    [ObservableProperty]
    private string _rawLetters = string.Empty;

    [ObservableProperty]
    private string _rawEmoji = string.Empty;

    [ObservableProperty]
    private ObservableCollection<LayerView> _categories = new();

    [ObservableProperty]
    private ObservableCollection<EmojiView> _emoji = new();

    [ObservableProperty]
    private ObservableCollection<MemoryViewItem> _memories = new();

    [ObservableProperty]
    private bool _showRecall;

    [ObservableProperty]
    private ObservableCollection<RecallHitView> _recallHits = new();

    // ── Выбор в списках (master-detail) ──────────────────────────────────────────────

    [ObservableProperty]
    private MemoryViewItem? _selectedFact;

    [ObservableProperty]
    private string _factEditContent = string.Empty;

    
    [ObservableProperty]
    private ObservableCollection<LayerView> _factCategories = new();

    [ObservableProperty]
    private ObservableCollection<EmojiView> _factEmoji = new();

    partial void OnSelectedFactChanged(MemoryViewItem? value)
    {
        if (value is null)
        {
            FactEditContent = string.Empty;
            FactCategories = new ObservableCollection<LayerView>();
            FactEmoji = new ObservableCollection<EmojiView>();
            return;
        }
        var item = _store.Get(value.Id);
        FactEditContent = item?.Content ?? value.Content;
        

        // Визуализация работы классификатора на реальном факте: его распределения барами.
        FactCategories = new ObservableCollection<LayerView>(
            (item?.CategoryLayers ?? new Dictionary<string, double>())
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new LayerView(kv.Key, MemoryCategories.Names.GetValueOrDefault(kv.Key, string.Empty), kv.Value)));
        FactEmoji = new ObservableCollection<EmojiView>(
            (item?.EmojiLayers ?? new Dictionary<string, double>())
                .OrderByDescending(kv => kv.Value).Select(kv => new EmojiView(kv.Key, kv.Value)));
    }

    /// <summary>Сохранить отредактированные текст/категорию выбранного факта.</summary>
    [RelayCommand]
    private void SaveFact()
    {
        if (SelectedFact is null)
        {
            return;
        }
        var item = _store.Get(SelectedFact.Id);
        if (item is null)
        {
            return;
        }
        item.Content = FactEditContent;
        // Строковые поля редактируются вручную — слои не трогаем (они про распределения).
        
        _store.Update(item);
        RefreshMemories();
        Status = $"Факт {SelectedFact.Id} сохранён.";
    }

    /// <summary>
    /// Сбросить ВСЕ классификации, не трогая записи: слои очищаются, версии обнуляются —
    /// flush на heartbeat (или кнопка «Дочистить») переклассифицирует всё заново.
    /// </summary>
    [RelayCommand]
    private void ResetClassifications()
    {
        var reset = 0;
        foreach (var item in _store.List())
        {
            item.CategoryLayers.Clear();
            item.EmojiLayers.Clear();
            item.LayersVersion = 0;
            _store.Update(item);
            reset++;
        }
        RefreshMemories();
        Status = $"Классификации сброшены у {reset} фактов (записи не тронуты).";
    }

    // ── Надмозг: пары и режим ────────────────────────────────────────────────────────

    private readonly PairsStore _pairs;

    [ObservableProperty]
    private ObservableCollection<PairView> _pendingPairs = new();

    [ObservableProperty]
    private int _distinctCount;

    [ObservableProperty]
    private int _unvectorizedCount;

    public string ModeText =>
        UnvectorizedCount > 0
            ? $"классификация: {UnvectorizedCount} без векторов"
            : "скан дубликатов: векторы у всех";

    // ── Настройки надмоза (строковые обёртки над AppSettings: TextBox-биндинг, безопасный парс;
    //    мусорный ввод игнорируется — свойство просто не меняется) ─────────────────────────

    public string ScanBudgetText
    {
        get => AppSettings.Get().MemoryScanProbeBudget.ToString();
        set { if (int.TryParse(value, out var v) && v > 0) { AppSettings.Get().MemoryScanProbeBudget = v; OnPropertyChanged(); } }
    }

    public string RecallMinScoreText
    {
        get => AppSettings.Get().RecallMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { AppSettings.Get().RecallMinScore = v; OnPropertyChanged(); } }
    }

    public string SimilarMinText
    {
        get => AppSettings.Get().SimilaritySimilarMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { AppSettings.Get().SimilaritySimilarMin = v; OnPropertyChanged(); } }
    }

    public string DistinctMaxText
    {
        get => AppSettings.Get().SimilarityDistinctMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { AppSettings.Get().SimilarityDistinctMax = v; OnPropertyChanged(); } }
    }

    public string EntropyMaxText
    {
        get => AppSettings.Get().SimilarityConfidentMaxEntropy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { AppSettings.Get().SimilarityConfidentMaxEntropy = v; OnPropertyChanged(); } }
    }

    public string FlushBudgetText
    {
        get => AppSettings.Get().MemoryFlushBudget.ToString();
        set { if (int.TryParse(value, out var v) && v > 0) { AppSettings.Get().MemoryFlushBudget = v; OnPropertyChanged(); } }
    }

    public string RecallTopXText
    {
        get => AppSettings.Get().RecallTopX.ToString();
        set { if (int.TryParse(value, out var v) && v > 0) { AppSettings.Get().RecallTopX = v; OnPropertyChanged(); } }
    }

    public string NagIntervalText
    {
        get => AppSettings.Get().MemoryNagIntervalRenders.ToString();
        set { if (int.TryParse(value, out var v) && v >= 0) { AppSettings.Get().MemoryNagIntervalRenders = v; OnPropertyChanged(); } }
    }

    public MemoryViewModel()
    {
        _pairs = new PairsStore(_store.Root);
        // Разовый мусор: пустые записи (созданные до строгого Add) не должны показываться.
        foreach (var empty in _store.List().Where(m => string.IsNullOrWhiteSpace(m.Content)).ToList())
        {
            _store.Remove(empty.Id);
        }
        _pairs.Cleanup(_store.List().Select(i => i.Id));
        RefreshMemories();
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            RefreshMemories();
        };
        StartWatching();
    }

    /// <summary>
    /// Витрина живая: агент чистит/объединяет факты инструментами memory_* прямо в memories/,
    /// а ваш список знает о диске. События файлового каталога сгружаются шагом дебаунса,
    /// чтобы пачка (delete×2 + add при memory_merge) не перерисовывала список пару раз.
    /// </summary>
    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(_store.Root, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnStoreChanged;
            _watcher.Created += OnStoreChanged;
            _watcher.Deleted += OnStoreChanged;
            // AtomicFile публикует запись переименованием temp→целевое имя: без Renamed
            // обновления классификации (store.Update) не доходят до списка.
            _watcher.Renamed += OnStoreChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null; // каталог недоступен — витрина остаётся ручной
        }
    }

    private void OnStoreChanged(object sender, FileSystemEventArgs e)
    {
        // Watcher-события приходят из пула потоков; DispatcherTimer живёт на UI-потоке.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            }));
        }
        else
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }
    }

    [RelayCommand]
    private void RefreshMemories()
    {
        Memories = new ObservableCollection<MemoryViewItem>(
            _store.List().Select(m => new MemoryViewItem(
                m.Id, MemoryClassifier.TopName(m.CategoryLayers), MemoryClassifier.TopEmojiOf(m.EmojiLayers), m.HasSemanticLayers, m.LayersVersion, m.CreatedAt, m.Content)));
        RefreshPairs();
        OnPropertyChanged(nameof(ModeText));
    }

    /// <summary>Стекло надмоза: очередь пар с содержимым обеих сторон + счётчики.</summary>
    private void RefreshPairs()
    {
        PendingPairs = new ObservableCollection<PairView>(
            _pairs.Pending.Select(p => new PairView(
                p.A, p.B,
                Preview(_store.Get(p.A)?.Content),
                Preview(_store.Get(p.B)?.Content),
                p.HistOverlap, p.Score, p.Entropy)));
        DistinctPairs = new ObservableCollection<DistinctPairView>(
            _pairs.Distinct.Select(p => new DistinctPairView(p.A, p.B,
                Preview(_store.Get(p.A)?.Content), Preview(_store.Get(p.B)?.Content))));
        DistinctCount = DistinctPairs.Count;
        UnvectorizedCount = MemoryClassifier.FlushTargets(_store).Count;
        OnPropertyChanged(nameof(ModeText));
    }

    [ObservableProperty]
    private ObservableCollection<DistinctPairView> _distinctPairs = new();

    /// <summary>Вернуть разведённую пару в кандидаты сканера (мимо неё пройдут снова).</summary>
    [RelayCommand]
    private void UnmarkDistinctPair(DistinctPairView? pair)
    {
        if (pair is null)
        {
            return;
        }
        _pairs.UnmarkDistinct(pair.IdA, pair.IdB);
        RefreshPairs();
        Status = $"Пара {pair.IdA} ~ {pair.IdB} возвращена в кандидаты.";
    }

    private int CountDistinct()
    {
        // Разведённые не отдаются списком (могут быть сотни) — только счётчик.
        var file = Path.Combine(_store.Root, "pairs.json");
        if (!File.Exists(file))
        {
            return 0;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("Distinct", out var distinct) &&
                   distinct.ValueKind == JsonValueKind.Array
                ? distinct.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Preview(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "(факт не найден — запись удалена?)";
        }
        var oneLine = content.Replace("\r\n", " ").Replace('\n', ' ');
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }

    /// <summary>Ручной проход сканера дубликатов: бюджет проб за клик. Результаты — в очередь пар.</summary>
    [RelayCommand]
    private async Task ScanDuplicatesAsync()
    {
        var endpoint = AppSettings.Get().CompanionEndpoint;
        IsBusy = true;
        try
        {
            var report = await MemorySimilarity.ScanPassAsync(
                _store, _pairs, endpoint, probeBudget: 10,
                async (prompt, refId, candId, ct) =>
                    await LlmProbeClient.ProbeAsync(endpoint, prompt, nProbs: 20, ct),
                CancellationToken.None);
            RefreshMemories();
            Status = $"Скан: {report.Probes} проб → похожих в очередь: {report.QueuedSimilar}, разведено: {report.MarkedDistinct}. " +
                     $"В очереди: {_pairs.Pending.Count}.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка скана: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnrichFactAsync(string? id)
    {
        var item = _store.Get(id ?? string.Empty);
        if (item is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await MemoryClassifier.EnrichAsync(item, AppSettings.Get().CompanionEndpoint);
            _store.Update(item);
            RefreshMemories();
            Status = $"Слои пересчитаны: {MemoryClassifier.TopName(item.CategoryLayers)} {MemoryClassifier.TopEmojiOf(item.EmojiLayers)}.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка обогащения: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteFact(string? id)
    {
        if (id is null || !_store.Remove(id))
        {
            return;
        }
        _pairs.Cleanup(_store.List().Select(i => i.Id));
        RefreshMemories();
        Status = $"Факт {id} удалён.";
    }

    /// <summary>Разрешение пары слиянием: делегируем memory_merge и чистим связи.</summary>
    [RelayCommand]
    private async Task MergePairAsync(PairView? pair)
    {
        if (pair is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var merger = new MemoryMergeTool { IdA = pair.IdA, IdB = pair.IdB };
            var result = await merger.ExecuteAsync(
                new ToolContext(_store.Root), CancellationToken.None);
            _pairs.Cleanup(_store.List().Select(i => i.Id));
            RefreshMemories();
            Status = result;
        }
        catch (Exception ex)
        {
            Status = $"Ошибка слияния: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Разрешение пары разводом: false positive классификатора фиксируется навсегда.</summary>
    [RelayCommand]
    private void NotSimilarPair(PairView? pair)
    {
        if (pair is null)
        {
            return;
        }
        _pairs.MarkDistinct(pair.IdA, pair.IdB);
        RefreshMemories();
        Status = $"Пара {pair.IdA} ~ {pair.IdB} разведена — больше не предложится.";
    }

    /// <summary>
    /// Ручной flush: до-классифицировать все факты без слоёв или со старой версией словаря.
    /// Дублирует фоновый flush из heartbeat, но весь сразу — для валидатора.
    /// </summary>
    [RelayCommand]
    private async Task FlushAsync()
    {
        var endpoint = AppSettings.Get().CompanionEndpoint;
        IsBusy = true;
        try
        {
            var processed = await MemoryClassifier.FlushAsync(_store, endpoint, budget: int.MaxValue);
            RefreshMemories();
            Status = processed == 0
                ? "Все воспоминания уже векторизованы (актуальная версия слоёв)."
                : $"Векторизовано: {processed}.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка flush: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClassifyAsync()
    {
        var text = TestText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Status = "Введите текст для классификации.";
            return;
        }

        var endpoint = AppSettings.Get().CompanionEndpoint;
        IsBusy = true;
        try
        {
            var detailed = await MemoryClassifier.ClassifyDetailedAsync(text, endpoint);
            Categories = new ObservableCollection<LayerView>(
                detailed.Layers.Categories
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new LayerView(
                        kv.Key, MemoryCategories.Names.GetValueOrDefault(kv.Key, string.Empty), kv.Value)));
            Emoji = new ObservableCollection<EmojiView>(
                detailed.Layers.Emoji.OrderByDescending(kv => kv.Value).Select(kv => new EmojiView(kv.Key, kv.Value)));
            RawLetters = FormatRaw(detailed.CategoryPositions);
            RawEmoji = FormatRaw(detailed.EmojiPositions);
            Status = detailed.Error is not null
                ? $"Ошибка пробы: {detailed.Error}"
                : $"Категорий: {detailed.Layers.Categories.Count}, эмодзи: {detailed.Layers.Emoji.Count}. Эндпоинт: {endpoint}";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка классификации: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RecallAsync()
    {
        var text = TestText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Status = "Введите вектор диалога для реколла.";
            return;
        }

        var endpoint = AppSettings.Get().CompanionEndpoint;
        IsBusy = true;
        ShowRecall = true;
        try
        {
            var hits = await MemoryRecall.RecallAsync(text, _store, endpoint, topX: 5, rerank: true);
            RecallHits = new ObservableCollection<RecallHitView>(
                hits.Select(h => new RecallHitView(
                    h.Item.Id, MemoryClassifier.TopName(h.Item.CategoryLayers), MemoryClassifier.TopEmojiOf(h.Item.EmojiLayers), h.Score, h.Item.Content)));
            Status = hits.Count == 0
                ? $"Реколл: ничего выше порога 0.12 по {_store.List().Count} фактам."
                : $"Реколл: {hits.Count} фактов выше порога (всего {_store.List().Count}).";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка реколла: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Сырые argmax-токены позиций пробы: «F I S X». Очищается, если проба не дала позиций.</summary>
    private static string FormatRaw(IReadOnlyList<ProbeResult>? positions)
    {
        if (positions is null || positions.Count == 0)
        {
            return string.Empty;
        }
        var tokens = positions.Select(p => p.ArgmaxToken.Trim()).Where(t => t.Length > 0).ToList();
        return tokens.Count == 0 ? string.Empty : string.Join(" ", tokens);
    }
}

/// <summary>Категория с долей распределения: буква, имя из справочника, вероятность.</summary>
public sealed record LayerView(string Letter, string Name, double Probability);

/// <summary>Эмодзи-вайб с долей распределения.</summary>
public sealed record EmojiView(string Emoji, double Probability);

/// <summary>Факт из памяти для списка витрины.</summary>
public sealed record MemoryViewItem(
    string Id, string Category, string Emoji, bool HasLayers, int LayersVersion, DateTime CreatedAt, string Content);

/// <summary>Хит реколла: факт + релевантность текущему вектору.</summary>
public sealed record RecallHitView(string Id, string Category, string Emoji, double Score, string Content);

/// <summary>Пара-кандидат на слияние: id обеих сторон, превью содержимого и ФАКТОРЫ решения
/// (схожесть гистограмм + балл/энтропия классификатора) — чтобы было видно, ПОЧЕМУ пара поднята.</summary>
public sealed record PairView(
    string IdA, string IdB, string ContentA, string ContentB,
    double HistOverlap, double DigitScore, double Entropy);

/// <summary>Разведённая пара (false positive классификатора) для инспекции в UI.</summary>
public sealed record DistinctPairView(string IdA, string IdB, string ContentA, string ContentB);
