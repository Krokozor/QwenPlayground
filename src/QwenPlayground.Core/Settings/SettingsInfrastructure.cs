using System.Reflection;
using System.Text.Json;
using QwenPlayground.Core.Serialization;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Settings;

/// <summary>
/// Маркирует класс настроек файлом хранения (паттерн NekoBot): относительный путь
/// разрешается от корня воркспейса. Один класс = один файл = одна зона ответственности.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SettingsFileAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

/// <summary>
/// Типизированный синглтон настроек (порт SettingsStore&lt;T&gt; из NekoBot.Core).
///
/// Правила дома:
/// - модуль читает свои настройки в точке использования: <c>MySettings.Get().Field</c>;
/// - параметры не протаскиваются через сигнатуры «на всякий случай»;
/// - живой экземпляр — источник правды во время работы процесса, диск — персистентность;
/// - UI-свойства делаются тонкими видами над Get(), а не зеркальными копиями.
///
/// Экземпляр лениво читается с диска один раз за процесс; Save пишет атомарно
/// (AtomicFile) под блокировкой — вызовы идут и с UI-потока, и из фоновых дебаунсов.
/// </summary>
public static class SettingsStore<T> where T : class, new()
{
    private static readonly object Gate = new();
    private static readonly string FilePath = ResolvePath();
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static T? _instance;

    /// <summary>
    /// Сработало после <see cref="Update"/> — подписчик (UI-слой) может перерисовать свои
    /// виды: живой экземпляр уже изменён и записан на диск. Вызывается ПОСЛЕ освобождения
    /// замка, поэтому подписчик свободно может <c>Get()</c> без риска дедлока.
    /// </summary>
    public static event Action<T>? Changed;

    private static string ResolvePath()
    {
        var attribute = typeof(T).GetCustomAttribute<SettingsFileAttribute>()
            ?? throw new InvalidOperationException(
                $"Settings type {typeof(T).Name} lacks SettingsFileAttribute.");
        return Path.IsPathRooted(attribute.Path)
            ? attribute.Path
            : Path.Combine(SelfBuildPaths.WorkspaceRoot, attribute.Path);
    }

    private static T LoadFromDisk()
    {
        if (!File.Exists(FilePath))
        {
            return new T();
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath)) ?? new T();
        }
        catch (JsonException)
        {
            // Битый файл (сбой посреди записи до эпохи AtomicFile) — дефолты вместо падения.
            return new T();
        }
        catch (IOException)
        {
            // Файл на мгновение занят параллельной записью — лучше дефолт в памяти.
            return new T();
        }
    }

    /// <summary>Кэшированный экземпляр настроек; первый вызов читает диск.</summary>
    public static T Get()
    {
        lock (Gate)
        {
            return _instance ??= LoadFromDisk();
        }
    }

    /// <summary>Принудительно перечитать с диска (правка файла руками между запусками хода).</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _instance = LoadFromDisk();
        }
    }

    /// <summary>Атомарно сохранить текущий экземпляр. Частоту вызывает сам владелец (дебаунс/закрытие).</summary>
    public static void Save()
    {
        lock (Gate)
        {
            // Сериализация под замком: иначе фоновый Save может сериализовать объект,
            // который UI-поток мутирует прямо в момент обхода свойств.
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_instance ??= LoadFromDisk(), WriteOptions));
        }
    }

    /// <summary>
    /// Write-through: атомарно применить мутацию к живому экземпляру и сохранить на диск.
    ///
    /// Живой экземпляр — источник правды во время работы процесса: правка только
    /// settings.json не подействует, пока процесс жив. Здесь мутация и запись идут под
    /// одним замком — нет окна, где модель изменена, а диск ещё нет (или наоборот).
    /// Используется инструментами агента (set_setting), меняющими настройки изнутри приложения.
    /// После записи поднимает <see cref="Changed"/> — UI-слой перерисовывает свои виды.
    /// </summary>
    public static void Update(Action<T> mutate)
    {
        T instance;
        lock (Gate)
        {
            instance = _instance ??= LoadFromDisk();
            mutate(instance);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(instance, WriteOptions));
        }
        Changed?.Invoke(instance);
    }
}
