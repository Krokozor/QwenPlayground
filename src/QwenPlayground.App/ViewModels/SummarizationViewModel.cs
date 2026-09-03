using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Вкладка «Суммаризация»: полное стекло в то, как сжимается контекст. Session-centric: все
/// секции относятся к ВЫБРАННОЙ сессии (main и не-main равноправны), объёмные поля — в Expander.
/// 1) Слои L1/L2/L3 выбранной сессии (sessions/&lt;id&gt;/layers.json) — главный вид;
/// 2) Плоское резюме (legacy, блок «[Сжатое резюме...]» в system-сообщении) — для не-main;
/// 3) Промпты суммаризации (config/prompts.json) — правятся здесь, действуют на следующий прогон;
/// 4) Ре-прогоны: ре-генерация резюме и прогон конвейера L1/L2/L3 с показом каждого этапа;
///    «Записать» пишет результат на диск (с бэкапом), ничего не меняя само.
/// </summary>
public partial class SummarizationViewModel : ObservableObject
{
    private static readonly string SessionsRoot = ChatSessions.Root; // единый корень с ChatSessions

    private readonly Func<string, string?, Action<string>?, CancellationToken, Task<string>> _complete;
    private readonly SessionStore _sessionStore = new(SessionsRoot);

    // ── Сессии ───────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<SessionItem> _sessions = new();

    [ObservableProperty]
    private SessionItem? _selectedSession;

    [ObservableProperty]
    private string _sessionNote = "Выберите сессию.";

    // ── Слои L1/L2/L3 ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _layer1 = string.Empty;

    [ObservableProperty]
    private string _layer2 = string.Empty;

    [ObservableProperty]
    private string _layer3 = string.Empty;

    [ObservableProperty]
    private string _layersPromptBlock = string.Empty;

    [ObservableProperty]
    private string _layersStatus = string.Empty;

    // ── Промпты (config/prompts.json) ────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<PromptStepItem> _promptSteps = new();

    [ObservableProperty]
    private PromptStepItem? _selectedStep;

    [ObservableProperty]
    private string _stepRenderedPrompt = string.Empty;

    [ObservableProperty]
    private string _promptStatus = string.Empty;

    // ── Ре-прогоны ───────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _runOutput = string.Empty;

    [ObservableProperty]
    private string _runStatus = string.Empty;

    [ObservableProperty]
    private LayerMemory? _proposedLayers;

    [ObservableProperty]
    private bool _hasLayerProposal;

    public SummarizationViewModel(
        Func<string, string?, Action<string>?, CancellationToken, Task<string>> complete)
    {
        _complete = complete;
        RefreshSessions();
        ReloadLayers();
        RefreshPromptSteps();
    }

    partial void OnSelectedSessionChanged(SessionItem? value)
    {
        ReloadLayers(); // слои следуют за выбранной сессией (per-session)
        if (RefreshSessionView())
        {
            RefreshStepPreviews();
        }
    }

    // ── Сессии ───────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshSessions()
    {
        var items = new List<SessionItem>
        {
            new(MainAgent.SessionId, "★ main-агент (слои L1/L2/L3)", IsMain: true)
        };
        foreach (var info in _sessionStore.List())
        {
            if (info.Id == MainAgent.SessionId)
            {
                continue;
            }
            items.Add(new SessionItem(info.Id, info.Title, IsMain: false));
        }
        Sessions = new ObservableCollection<SessionItem>(items);
        if (SelectedSession is not null)
        {
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == SelectedSession.Id) ?? Sessions.FirstOrDefault();
        }
        else if (Sessions.Count > 0)
        {
            SelectedSession = Sessions[0];
        }
    }

    private bool RefreshSessionView()
    {
        if (SelectedSession is null)
        {
            SessionNote = "Выберите сессию.";
            return false;
        }
        SessionNote = "Слои L1/L2/L3 выбранной сессии (sessions/<id>/layers.json).";
        return true;
    }

    // ── Слои L1/L2/L3 ────────────────────────────────────────────────────────────────

    /// <summary>Store слоёв выбранной сессии (per-session, sessions/<id>/layers.json).</summary>
    private MemoryLayerStore StoreForSelected() =>
        new(Path.Combine(SessionsRoot, SelectedSession?.Id ?? MainAgent.SessionId));

    private void ReloadLayers()
    {
        var store = StoreForSelected();
        var layers = store.Load();
        Layer1 = layers.L1;
        Layer2 = layers.L2;
        Layer3 = layers.L3;
        UpdateLayersPromptBlock();
        LayersStatus = "загружено из " + store.FilePath;
    }

    [RelayCommand]
    private void LoadLayers()
    {
        ReloadLayers();
        RefreshStepPreviews();
    }

    [RelayCommand]
    private void SaveLayers()
    {
        var sessionId = SelectedSession?.Id ?? MainAgent.SessionId;
        try
        {
            new ContextBackupStore(SessionsRoot).Save(sessionId);
        }
        catch (Exception ex)
        {
            LayersStatus = $"бэкап не удался, сохранить нельзя: {ex.Message}";
            return;
        }
        var store = StoreForSelected();
        store.Save(new LayerMemory { L1 = Layer1.Trim(), L2 = Layer2.Trim(), L3 = Layer3.Trim() });
        UpdateLayersPromptBlock();
        LayersStatus = $"сохранено в {store.FilePath}";
        RefreshStepPreviews();
    }

    private void UpdateLayersPromptBlock()
    {
        LayersPromptBlock = new LayerMemory { L1 = Layer1.Trim(), L2 = Layer2.Trim(), L3 = Layer3.Trim() }
            .ToPromptBlock();
    }

    // ── Промпты ──────────────────────────────────────────────────────────────────────

    private void RefreshPromptSteps()
    {
        var templates = PromptCatalog.Load();
        PromptSteps = new ObservableCollection<PromptStepItem>
        {
            NewStep("Merge", "Слияние L1+L2", PromptCatalog.Defaults.Merge, templates.Merge),
            NewStep("MergeValidation", "Сверка слияния L1+L2", PromptCatalog.Defaults.MergeValidation, templates.MergeValidation),
            NewStep("SegmentSummary", "Сегмент → L3", PromptCatalog.Defaults.SegmentSummary, templates.SegmentSummary),
            NewStep("SegmentValidation", "Сверка сегмента", PromptCatalog.Defaults.SegmentValidation, templates.SegmentValidation),
            NewStep("MemoryExtraction", "Извлечение фактов", PromptCatalog.Defaults.MemoryExtraction, templates.MemoryExtraction),
            NewStep("MemoryExtractionSystem", "Извлечение фактов: system", PromptCatalog.Defaults.MemoryExtractionSystem, templates.MemoryExtractionSystem)
        };
        SelectedStep = PromptSteps.FirstOrDefault();
    }

    private static PromptStepItem NewStep(string key, string name, string defaultText, string current) =>
        new(key, name, defaultText) { Template = current };

    partial void OnSelectedStepChanged(PromptStepItem? value) => RefreshStepPreviews();

    private void RefreshStepPreviews()
    {
        if (SelectedStep is null)
        {
            StepRenderedPrompt = string.Empty;
            return;
        }
        var messages = SelectedSession is { } session && !session.IsMain ? LoadConversation(session.Id) : new List<ChatMessage>();
        var layers = new LayerMemory { L1 = Layer1.Trim(), L2 = Layer2.Trim(), L3 = Layer3.Trim() };
        var templates = PromptCatalog.Load();
        var transcript = messages.Count == 0
            ? "(нет выбранной сессии — транскрипт будет взят из открытого разговора на прогоне)"
            : ContextCompactor.BuildTranscript(messages, messages.Count);

        switch (SelectedStep.Key)
        {
            case "Merge":
                StepRenderedPrompt = StructuredCompletion.Render(MemoryLayerPipeline.BuildMergePrompt(layers.L1, layers.L2));
                break;
            case "MergeValidation":
                var temp = string.IsNullOrWhiteSpace(layers.L1) && string.IsNullOrWhiteSpace(layers.L2)
                    ? "(пусто — мерджа не было)"
                    : "(результат merge, вычисляется на прогоне)";
                StepRenderedPrompt = StructuredCompletion.Render(
                    MemoryLayerPipeline.BuildMergeValidationPrompt(layers.L1, layers.L2, temp));
                break;
            case "SegmentSummary":
                StepRenderedPrompt = StructuredCompletion.Render(MemoryLayerPipeline.BuildSegmentSummaryPrompt(transcript));
                break;
            case "SegmentValidation":
                StepRenderedPrompt = StructuredCompletion.Render(
                    MemoryLayerPipeline.BuildSegmentValidationPrompt(transcript, layers.L3.Length == 0 ? "(пусто)" : layers.L3));
                break;
            case "MemoryExtraction":
                StepRenderedPrompt = StructuredCompletion.Render(MemoryExtractor.BuildExtractionPrompt(transcript));
                break;
            case "MemoryExtractionSystem":
                StepRenderedPrompt = templates.MemoryExtractionSystem;
                break;
        }
    }

    /// <summary>Сохранение отредактированного шаблона в config/prompts.json (действует на будущие прогоны).</summary>
    [RelayCommand]
    private void SavePromptTemplate()
    {
        if (SelectedStep is null)
        {
            return;
        }
        var templates = PromptCatalog.Load();
        ApplyTo(templates, SelectedStep);
        PromptCatalog.Save(templates);
        SelectedStep.IsDefault = SelectedStep.Template == PromptCatalog.DefaultsOf(SelectedStep.Key);
        PromptStatus = $"Шаблон «{SelectedStep.Name}» сохранён в config/prompts.json.";
        RefreshStepPreviews();
    }

    [RelayCommand]
    private void ResetPromptTemplate()
    {
        if (SelectedStep is null)
        {
            return;
        }
        SelectedStep.Template = PromptCatalog.DefaultsOf(SelectedStep.Key);
        SelectedStep.IsDefault = true;
        PromptStatus = $"Шаблон «{SelectedStep.Name}» сброшен к встроенному дефолту.";
    }

    private static void ApplyTo(PromptTemplateSet templates, PromptStepItem step)
    {
        switch (step.Key)
        {
            case "Merge": templates.Merge = step.Template; break;
            case "MergeValidation": templates.MergeValidation = step.Template; break;
            case "SegmentSummary": templates.SegmentSummary = step.Template; break;
            case "SegmentValidation": templates.SegmentValidation = step.Template; break;
            case "MemoryExtraction": templates.MemoryExtraction = step.Template; break;
            case "MemoryExtractionSystem": templates.MemoryExtractionSystem = step.Template; break;
        }
    }

        // ── Ре-прогоны ───────────────────────────────────────────────────────────────────

    /// <summary>Полный сухой-прогон конвейера L1/L2/L3 на выбранной сессии. Результат — в ProposedLayers.</summary>
    [RelayCommand]
    private async Task RerunPipelineAsync()
    {
        var messages = SelectedSession is { } session && !session.IsMain
            ? LoadConversation(session.Id)
            : new List<ChatMessage>();
        var layers = StoreForSelected().Load();
        var transcript = ContextCompactor.BuildTranscript(messages, messages.Count);

        IsRunning = true;
        _runBuffer.Clear();
        _runThrottle.Reset();
        RunOutput = string.Empty;
        HasLayerProposal = false;
        ProposedLayers = null;
        RunStatus = "прогон конвейера L1/L2/L3...";
        try
        {
            var result = await MemoryLayerPipeline.RunAsync(
                layers, transcript,
                complete: (userContent, ct) => _complete(userContent, null, AppendRunToken, ct),
                onStage: stage => AppendRunToken("\n── " + stage + " ──\n"));
            AppendRunToken("\n──────\nфакты валидаций: " + (result.Facts.Count == 0 ? "не найдено" : string.Join(" | ", result.Facts)) + "\n");

            if (!result.MergeSucceeded || !result.SegmentSucceeded)
            {
                RunStatus = "Конвейер не завершил критичные шаги (merge=" + (result.MergeSucceeded ? "ok" : "fail") +
                            ", сегмент=" + (result.SegmentSucceeded ? "ok" : "fail") + "). Ротация слоёв не применяется.";
                return;
            }
            ProposedLayers = result.Next;
            HasLayerProposal = true;
            RunStatus = "Конвейер отработал: L1/L2/L3 рассчитаны. Нажмите «Записать слои» (будет бэкап).";
        }
        catch (Exception ex)
        {
            RunStatus = "Ошибка конвейера: " + ex.Message;
        }
        finally
        {
            FlushRunOutput();
            IsRunning = false;
        }
    }

    // Стрим вывода прогона: буфер + троттлинг публикации в UI (паттерн CompactionPreview).
    // Прежний RunOutput += token на каждый чанк — квадратичное конкатенирование строк
    // и полный ре-рендер TextBox на каждый токен.
    private readonly StringBuilder _runBuffer = new();
    private readonly System.Diagnostics.Stopwatch _runThrottle = new();

    private void AppendRunToken(string token)
    {
        _runBuffer.Append(token);
        if (!_runThrottle.IsRunning || _runThrottle.ElapsedMilliseconds >= 50)
        {
            RunOutput = _runBuffer.ToString();
            _runThrottle.Restart();
        }
    }

    /// <summary>Финальная публикация буфера после завершения прогона (в т.ч. после ошибки).</summary>
    private void FlushRunOutput() => RunOutput = _runBuffer.ToString();

    [RelayCommand]
    private void ClearRun()
    {
        _runBuffer.Clear();
        _runThrottle.Reset();
        RunOutput = string.Empty;
        HasLayerProposal = false;
        ProposedLayers = null;
    }

    [RelayCommand]
    private void ApplyProposedLayers()
    {
        if (ProposedLayers is null)
        {
            return;
        }
        try
        {
            new ContextBackupStore(SessionsRoot).Save(SelectedSession?.Id ?? MainAgent.SessionId);
        }
        catch (Exception ex)
        {
            LayersStatus = $"бэкап не удался, сохранить нельзя: {ex.Message}";
            return;
        }
        var store = StoreForSelected();
        store.Save(ProposedLayers);
        ReloadLayers();
        HasLayerProposal = false;
        LayersStatus = $"слои L1/L2/L3 перезаписаны в {store.FilePath}";
        RefreshStepPreviews();
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────────────

    private List<ChatMessage> LoadConversation(string sessionId)
    {
        var data = _sessionStore.Load(sessionId);
        return data?.Messages.ToList() ?? new List<ChatMessage>();
    }

}

/// <summary>Сессия для списка: id, заголовок, признак main-агента (слои вместо резюме).</summary>
public sealed record SessionItem(string Id, string Title, bool IsMain)
{
    // ComboBox в SummarizationView биндится на этот тип; ToString — заголовок (или id),
    // а не "SessionItem {…}", даже если DisplayMemberPath не сработает.
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Id : Title;
}

/// <summary>Шаг суммаризации для редактора промптов: ключ шаблона, имя, текст (может быть изменён).</summary>
public partial class PromptStepItem : ObservableObject
{
    public string Key { get; }
    public string Name { get; }

    [ObservableProperty]
    private string _template;

    [ObservableProperty]
    private bool _isDefault;

    public PromptStepItem(string key, string name, string defaultTemplate)
    {
        Key = key;
        Name = name;
        _template = defaultTemplate;
        _isDefault = true;
    }
}