namespace QwenPlayground.Core.MetaInfo;

/// <summary>
/// Анонс на «доске сообщений» state-блока: метка (то, что видит модель) + сообщения.
/// Обёртка, а не голая строка — шов под метадату (severity, key для дедупа): добавляется
/// лениво, когда появится реальный спрос, без ломки <see cref="IStateAnnouncer"/>.
/// </summary>
public sealed class StateAnnounce
{
    /// <summary>
    /// Метка — per-instance, собирается анонсером из своих данных ("memory", "mcp:blender").
    /// Это модель-facing контракт, поэтому явная, а не вычитанная из имени типа.
    /// Модель видит строку «метка: сообщение».
    /// </summary>
    public required string Label { get; init; }

    /// <summary>Сообщения (однострочные — парсер блока режет по строкам).</summary>
    public required IReadOnlyList<string> Messages { get; init; }

    /// <summary>
    /// Необязательный непрозрачный «рычаг» для диагностики (инстанс анонсера).
    /// Билдер в него не лезет — просто несёт; инкапсуляция анонсера не нарушается.
    /// </summary>
    public object? Source { get; init; }
}

/// <summary>
/// Анонсер доски сообщений state-блока: pull-источник, вызывается при каждой сборке блока.
/// Пустой список = тишина в этом рендере. Метод один на источник — дубликаты из одного
/// источника структурно невозможны (в отличие от push в статичный метод без трекинга).
/// </summary>
public interface IStateAnnouncer
{
    IReadOnlyList<StateAnnounce> GetAnnounces();
}
