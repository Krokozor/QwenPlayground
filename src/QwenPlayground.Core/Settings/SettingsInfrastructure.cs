using System.Globalization;
using System.Reflection;
using System.Text.Json;
using QwenPlayground.Core.Serialization;
using QwenPlayground.Core.Security;
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
/// Маркирует свойство настроек секретом (план, раздел 13): значение автоматически
/// регистрируется в <see cref="SecretRedactor"/> (цензура transcript/логов) и НЕ
/// показывается инструментам агента: get_settings печатает «&lt;hidden — set, N chars&gt;»,
/// set_setting пишет без эха. Защита не «от агента» (у него rebuild_self), а гигиена
/// канала: меньше мигания секретами в чате и явные write-only рычаги.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class SettingSecretAttribute : Attribute;

/// <summary>
/// Агентское форматирование значений настроек (план, раздел 13): свойства с
/// <see cref="SettingSecretAttribute"/> никогда не печатаются со значением.
/// </summary>
public static class SettingDisplay
{
    public static bool IsSecret(PropertyInfo property)
        => property.GetCustomAttribute<SettingSecretAttribute>() is not null;

    /// <summary>
    /// Вложенная POCO-группа настроек (не строка, не перечисление, не коллекция, не примитив):
    /// get_settings печатает её отдельной секцией, SettingsStore рекурсивно ищет
    /// в ней [SettingSecret].
    /// </summary>
    public static bool IsNestedObject(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsClass
            && t != typeof(string)
            && !t.IsArray
            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t);
    }

    /// <summary>Значение для печати в get_settings: секрет — маркером, остальное — как есть.</summary>
    public static string Format(PropertyInfo property, object? value)
        => IsSecret(property) ? FormatSecret(value) : FormatPlain(value);

    public static string FormatSecret(object? value)
    {
        if (value is null)
        {
            return "<hidden — empty>";
        }
        if (value is string s)
        {
            return s.Length == 0 ? "<hidden — empty>" : $"<hidden — set, {s.Length} chars>";
        }
        return "<hidden — set>";
    }

    public static string FormatPlain(object? value) => value switch
    {
        null => "(null)",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Ответ set_setting: для секретов без эха значений (факт записи подтверждается).</summary>
    public static string ChangeMessage(string name, bool isSecret, object? oldValue, object? newValue)
        => isSecret
            ? $"set_setting: {name}: <redacted> → <redacted>. Persisted to settings.json."
            : $"set_setting: {name} = {oldValue} → {newValue}. Persisted to settings.json.";
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

    // План, разделы 13–14: свойства [SettingSecret] (включая вложенные POCO-группы,
    // напр. AppSettings.Mail) — авто-регистрация значений в SecretRedactor.
    // Дефолт каждого пути вычисляется из свежего экземпляра: значение, равное дефолту,
    // не регистрируем (иначе будем цензурировать случайные куски обычного текста).
    private static readonly PropertyInfo[][] SecretPaths = FindSecretPaths(typeof(T));

    private static PropertyInfo[][] FindSecretPaths(Type type)
    {
        var result = new List<PropertyInfo[]>();
        FindSecretPathsIn(type, Array.Empty<PropertyInfo>(), result, depth: 0);
        return result.ToArray();
    }

    private static void FindSecretPathsIn(Type type, PropertyInfo[] prefix, List<PropertyInfo[]> result, int depth)
    {
        if (depth > 3)
        {
            return; // защита от безумной вложенности
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
            {
                continue;
            }
            var path = prefix.Concat([property]).ToArray();
            if (property.GetCustomAttribute<SettingSecretAttribute>() is not null && property.PropertyType == typeof(string))
            {
                result.Add(path);
            }
            else if (SettingDisplay.IsNestedObject(property.PropertyType))
            {
                FindSecretPathsIn(property.PropertyType, path, result, depth + 1);
            }
        }
    }

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
        T instance;
        if (!File.Exists(FilePath))
        {
            instance = new T();
        }
        else
        {
            try
            {
                instance = JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath)) ?? new T();
            }
            catch (JsonException)
            {
                // Битый файл (сбой посреди записи до эпохи AtomicFile) — дефолты вместо падения.
                instance = new T();
            }
            catch (IOException)
            {
                // Файл на мгновение занят параллельной записью — лучше дефолт в памяти.
                instance = new T();
            }
        }
        RegisterSecrets(instance);
        return instance;
    }

    /// <summary>
    /// Регистрирует текущие значения свойств [SettingSecret] в SecretRedactor (план, раздел 13).
    /// Гварды против случайной цензуры обычного текста: пустые значения и значения, равные
    /// дефолту типа, не регистрируются; короткие (&lt;8) и Placeholder пропускает сам Redactor.
    /// Идемпотентно: повторная регистрация того же значения — no-op. Вызывается под замком
    /// (из LoadFromDisk/Update), поэтому обход свойств безопасен.
    /// </summary>
    private static void RegisterSecrets(T instance)
    {
        var defaults = new T();
        foreach (var path in SecretPaths)
        {
            var value = GetPathValue(instance, path) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var defaultValue = GetPathValue(defaults, path) as string;
            if (value == defaultValue)
            {
                continue;
            }
            SecretRedactor.Register(value);
        }
    }

    private static object? GetPathValue(object root, PropertyInfo[] path)
    {
        object? current = root;
        foreach (var property in path)
        {
            current = current is null ? null : property.GetValue(current);
            if (current is null)
            {
                return null;
            }
        }
        return current;
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
            RegisterSecrets(instance);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(instance, WriteOptions));
        }
        Changed?.Invoke(instance);
    }
}
