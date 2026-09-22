using System.Reflection;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Панель настроек — свой DataContext (инкапсуляция: биндинги SettingsView/MemorySettingsView
/// не знают о MainViewModel). Паттерн NekoBot: источник правды — синглтон AppSettings.Get(),
/// свойства ниже — тонкие виды (чтение напрямую, запись = мутация + INPC + отложенный Save).
/// MainViewModel не несёт зеркала: чат-код читает AppSettings напрямую, реакции вида на
/// смену настроек — через события (EndpointChanged) и RefreshAll (внешние изменения).
/// </summary>
public sealed class SettingsViewModel : ObservableObject {
    // ── Механизм: мутация + INPC + отложенное сохранение ────────────────────────────

    /// <summary>Запись настройки с уведомлением биндинга и отложенным сохранением.</summary>
    private void Set<T>(T current, T value, Action<AppSettings, T> assign, [CallerMemberName] string? propertyName = null) {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;

        var settings = AppSettings.Get();
        assign(settings, value);
        OnPropertyChanged(propertyName);
        ScheduleSave();
    }

    /// <summary>Читаемая настройка: <c>S.X</c> короче, чем AppSettings.Get().X, в 40+ свойствах.</summary>
    private AppSettings S => AppSettings.Get();

    private CancellationTokenSource? _settingsSaveDebounce;

    /// <summary>
    /// Отложенная запись настроек на диск: правки полей в UI идут пачками (каждое нажатие
    /// стрелки в numeric-поле — событие), писать на каждый чанг незачем. 800 мс тишины — пишем.
    /// Всё на UI-потоке (дизайн: потоки смещены в один), Save — маленький JSON, без блокировок.
    /// Публичный: ReasoningEffort живёт в MainViewModel (биндинг тулбара чата), а мутация
    /// настройки — общая.
    /// </summary>
    public void ScheduleSave() {
        _settingsSaveDebounce?.Cancel();
        _settingsSaveDebounce?.Dispose();
        _settingsSaveDebounce = new CancellationTokenSource();
        var token = _settingsSaveDebounce.Token;
        _ = Task.Delay(800, token).ContinueWith(t => {
            if (!t.IsCanceled) {
                AppSettings.Save();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Синхронный flush настроек при закрытии. Дебаунс (800 мс) при выключении приложения
    /// не гарантирован: отложенный таск может быть отменён или не успеть выполниться до
    /// завершения процесса — настройки терялись, на старте грузился дефолт.
    /// </summary>
    public void FlushSettingsSave() {
        _settingsSaveDebounce?.Cancel();
        AppSettings.Save();
    }

    /// <summary>
    /// Перерисовать биндинг настроек после внешнего изменения (тул set_setting агента).
    /// Тонкие виды читают живой AppSettings, поэтому достаточно сообщить биндингу, что
    /// соответствующие свойства могли измениться. Собираем рефлексией по совпадению имени
    /// с полем AppSettings: новое поле настроек подхватится автоматически.
    /// </summary>
    public void RefreshAll() {
        var settingsType = typeof(AppSettings);
        foreach (var property in typeof(SettingsViewModel)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite
                                 && settingsType.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance) is not null)) {
            OnPropertyChanged(property.Name);
        }
    }

    // ── Сервер / ход ────────────────────────────────────────────────────────────────

    /// <summary>Адрес llama.cpp-сервера основного хода. Событие — реакция вида в MainViewModel (CanExecute SendCommand).</summary>
    public event Action? EndpointChanged;

    /// <summary>Адрес llama.cpp-сервера основного хода.</summary>
    public string Endpoint {
        get => S.Endpoint;
        set {
            var old = S.Endpoint;
            Set(old, value, (s, v) => s.Endpoint = v);
            if (old != S.Endpoint) {
                EndpointChanged?.Invoke();
            }
        }
    }

    public int MaxTokens {
        get => S.MaxTokens;
        set => Set(S.MaxTokens, value, (s, v) => s.MaxTokens = v);
    }

    public int ContextSize {
        get => S.ContextSize;
        set => Set(S.ContextSize, value, (s, v) => s.ContextSize = v);
    }

    public int MaxIterations {
        get => S.MaxIterations;
        set => Set(S.MaxIterations, value, (s, v) => s.MaxIterations = v);
    }

    public int SanityCheckInterval {
        get => S.SanityCheckInterval;
        set => Set(S.SanityCheckInterval, value, (s, v) => s.SanityCheckInterval = v);
    }

    /// <summary>Пуш на GitHub при самосборке (rebuild_self). По умолчанию выкл.</summary>
    public bool PushOnRebuild {
        get => S.PushOnRebuild;
        set => Set(S.PushOnRebuild, value, (s, v) => s.PushOnRebuild = v);
    }
    public string PushRepo {
        get => S.PushRepo;
        set => Set(S.PushRepo, value, (s, v) => s.PushRepo = v);
    }

    // ── Семплер ─────────────────────────────────────────────────────────────────────

    public string Temperature {
        get => S.Temperature;
        set => Set(S.Temperature, value, (s, v) => s.Temperature = v);
    }

    public string TopP {
        get => S.TopP;
        set => Set(S.TopP, value, (s, v) => s.TopP = v);
    }

    public string TopK {
        get => S.TopK;
        set => Set(S.TopK, value, (s, v) => s.TopK = v);
    }

    public string MinP {
        get => S.MinP;
        set => Set(S.MinP, value, (s, v) => s.MinP = v);
    }

    public string RepeatPenalty {
        get => S.RepeatPenalty;
        set => Set(S.RepeatPenalty, value, (s, v) => s.RepeatPenalty = v);
    }

    public string Seed {
        get => S.Seed;
        set => Set(S.Seed, value, (s, v) => s.Seed = v);
    }

    public string CompactKeepRatio {
        get => S.CompactKeepRatio;
        set => Set(S.CompactKeepRatio, value, (s, v) => s.CompactKeepRatio = v);
    }
    public string ProjectRoot {
        get => S.ProjectRoot;
        set => Set(S.ProjectRoot, value, (s, v) => s.ProjectRoot = v);
    }

    /// <summary>Хранить ли полный промпт каждого хода в истории (chat.json). По умолчанию выкл.</summary>
    public bool SaveGenerationPrompts {
        get => S.SaveGenerationPrompts;
        set => Set(S.SaveGenerationPrompts, value, (s, v) => s.SaveGenerationPrompts = v);
    }

    // ── Heartbeat / сервисы ─────────────────────────────────────────────────────────

    public bool HeartbeatEnabled {
        get => S.HeartbeatEnabled;
        set => Set(S.HeartbeatEnabled, value, (s, v) => s.HeartbeatEnabled = v);
    }

    public int HeartbeatIntervalMinutes {
        get => S.HeartbeatIntervalMinutes;
        set => Set(S.HeartbeatIntervalMinutes, value, (s, v) => s.HeartbeatIntervalMinutes = v);
    }

    /// <summary>
    /// Режим диагностики: детальный трейс в logs/diag-YYYYMMDD.log. Включается без
    /// перезапуска (DiagnosticsLog.SetEnabled) — сразу видно, где процесс стоит.
    /// </summary>
    public bool DiagnosticsMode {
        get => S.DiagnosticsMode;
        set {
            Set(S.DiagnosticsMode, value, (s, v) => s.DiagnosticsMode = v);
            DiagnosticsLog.SetEnabled(value);
            DiagnosticsLog.Log($"diagnostics mode {(value ? "ON" : "OFF")} (UI)");
        }
    }

    /// <summary>Интервал автосохранения драфта окошка ввода (сек). 0 = выключено.</summary>
    public int DraftSaveIntervalSeconds {
        get => S.DraftSaveIntervalSeconds;
        set => Set(S.DraftSaveIntervalSeconds, value, (s, v) => s.DraftSaveIntervalSeconds = v);
    }

    // ── Компаньон / память ──────────────────────────────────────────────────────────

    /// <summary>Компаньон-модель (логит-пробы, векторизация памяти) — отдельная машина.</summary>
    public string CompanionEndpoint {
        get => S.CompanionEndpoint;
        set => Set(S.CompanionEndpoint, value, (s, v) => s.CompanionEndpoint = v);
    }
    /// <summary>Использовать ли companion-модель для проб (тумблер рядом с адресом). Выкл — пробы не летят, память по тексту; адрес сохранён.</summary>
    public bool CompanionEnabled {
        get => S.CompanionEnabled;
        set => Set(S.CompanionEnabled, value, (s, v) => s.CompanionEnabled = v);
    }
    /// <summary>Мастер-переключатель памяти агента (вручную). Выкл — реколл/state-блок/наг/flush и тулы memory_* выключены.</summary>
    public bool MemoryEnabled {
        get => S.MemoryEnabled;
        set => Set(S.MemoryEnabled, value, (s, v) => s.MemoryEnabled = v);
    }

    // ── Память / надмозг ────────────────────────────────────────────────────────────

    public int MemoryFlushBudget {
        get => S.MemoryFlushBudget;
        set => Set(S.MemoryFlushBudget, value, (s, v) => s.MemoryFlushBudget = v);
    }
    public int MemoryScanProbeBudget {
        get => S.MemoryScanProbeBudget;
        set => Set(S.MemoryScanProbeBudget, value, (s, v) => s.MemoryScanProbeBudget = v);
    }
    public int MemorySurfacingThreshold {
        get => S.MemorySurfacingThreshold;
        set => Set(S.MemorySurfacingThreshold, value, (s, v) => s.MemorySurfacingThreshold = v);
    }
    public int MemoryLiveRecallMinTokens {
        get => S.MemoryLiveRecallMinTokens;
        set => Set(S.MemoryLiveRecallMinTokens, value, (s, v) => s.MemoryLiveRecallMinTokens = v);
    }
    public int MemoryLiveRecallIntervalSec {
        get => S.MemoryLiveRecallIntervalSec;
        set => Set(S.MemoryLiveRecallIntervalSec, value, (s, v) => s.MemoryLiveRecallIntervalSec = v);
    }
    public bool MemoryNagEnabled {
        get => S.MemoryNagEnabled;
        set => Set(S.MemoryNagEnabled, value, (s, v) => s.MemoryNagEnabled = v);
    }
    public int MemoryNagIntervalRenders {
        get => S.MemoryNagIntervalRenders;
        set => Set(S.MemoryNagIntervalRenders, value, (s, v) => s.MemoryNagIntervalRenders = v);
    }
    public int RecallTopX {
        get => S.RecallTopX;
        set => Set(S.RecallTopX, value, (s, v) => s.RecallTopX = v);
    }
    public string RecallMinScore {
        get => S.RecallMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.RecallMinScore, v, (s, x) => s.RecallMinScore = x); }
    }
    public string SimilaritySimilarMin {
        get => S.SimilaritySimilarMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilaritySimilarMin, v, (s, x) => s.SimilaritySimilarMin = x); }
    }
    public string SimilarityDistinctMax {
        get => S.SimilarityDistinctMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilarityDistinctMax, v, (s, x) => s.SimilarityDistinctMax = x); }
    }
    public string SimilarityConfidentMaxEntropy {
        get => S.SimilarityConfidentMaxEntropy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilarityConfidentMaxEntropy, v, (s, x) => s.SimilarityConfidentMaxEntropy = x); }
    }
    public int MemoryDialogueBudgetTokens {
        get => S.MemoryDialogueBudgetTokens;
        set => Set(S.MemoryDialogueBudgetTokens, value, (s, v) => s.MemoryDialogueBudgetTokens = v);
    }
    public int MemoryDialogueMaxMessages {
        get => S.MemoryDialogueMaxMessages;
        set => Set(S.MemoryDialogueMaxMessages, value, (s, v) => s.MemoryDialogueMaxMessages = v);
    }
    public int MemoryClassifyNProbs {
        get => S.MemoryClassifyNProbs;
        set => Set(S.MemoryClassifyNProbs, value, (s, v) => s.MemoryClassifyNProbs = v);
    }
    public int MemoryClassifyNPredict {
        get => S.MemoryClassifyNPredict;
        set => Set(S.MemoryClassifyNPredict, value, (s, v) => s.MemoryClassifyNPredict = v);
    }
    public int MemoryRerankNProbs {
        get => S.MemoryRerankNProbs;
        set => Set(S.MemoryRerankNProbs, value, (s, v) => s.MemoryRerankNProbs = v);
    }
    public int MemoryRerankNPredict {
        get => S.MemoryRerankNPredict;
        set => Set(S.MemoryRerankNPredict, value, (s, v) => s.MemoryRerankNPredict = v);
    }
    public int MemoryRerankMaxCandidates {
        get => S.MemoryRerankMaxCandidates;
        set => Set(S.MemoryRerankMaxCandidates, value, (s, v) => s.MemoryRerankMaxCandidates = v);
    }
    public int MemoryRerankCandidateContentLength {
        get => S.MemoryRerankCandidateContentLength;
        set => Set(S.MemoryRerankCandidateContentLength, value, (s, v) => s.MemoryRerankCandidateContentLength = v);
    }
    public string MemoryCategoryWeight {
        get => S.MemoryCategoryWeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.MemoryCategoryWeight, v, (s, x) => s.MemoryCategoryWeight = x); }
    }
    public string MemoryEmojiWeight {
        get => S.MemoryEmojiWeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.MemoryEmojiWeight, v, (s, x) => s.MemoryEmojiWeight = x); }
    }
    public int MemoryMaxFactsPerCompaction {
        get => S.MemoryMaxFactsPerCompaction;
        set => Set(S.MemoryMaxFactsPerCompaction, value, (s, v) => s.MemoryMaxFactsPerCompaction = v);
    }
    public int MemoryDiaryMaxEntryLength {
        get => S.MemoryDiaryMaxEntryLength;
        set => Set(S.MemoryDiaryMaxEntryLength, value, (s, v) => s.MemoryDiaryMaxEntryLength = v);
    }

    // ── Состав панели ───────────────────────────────────────────────────────────────

    /// <summary>Редактор профилей чата (вкладка «Профили» внутри настроек).</summary>
    public ChatProfilesEditorViewModel Profiles { get; } = new();
}
