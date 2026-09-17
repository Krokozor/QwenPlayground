using System.Collections.Concurrent;

namespace QwenPlayground.Core.MetaInfo;

/// <summary>
/// Статичная «мусорка» анонсов: push-канал для кода, который НЕ реализует
/// <see cref="IStateAnnouncer"/> (утилиты, tool-исполнители, фоновые задачи, дебаг).
/// ESCAPE HATCH, а не дефолт: stateful per-render анонсы идут через интерфейс —
/// явная проводка в композиционном корне остаётся основным путём.
///
/// Тред-сейф (пуш с фоновых потоков). Дрейнится адаптером-анонсером при каждой сборке
/// блока — содержимое не живёт дольше одного цикла рендера. Кап защищает простое
/// (генерации нет — очередь не растёт бесконечно). Контент по контракту
/// сессие-агностичный/транзитный; на смене сессии очередь чистится (Clear).
/// </summary>
public static class AnnouncementBoard
{
    private static readonly ConcurrentQueue<StateAnnounce> Queue = new();

    /// <summary>Кап очереди (старое отбрасывается) — защита простоя без генерации.</summary>
    private const int Cap = 50;

    /// <summary>
    /// Пушит анонс. Метка обязательна — это история обнаруживаемости:
    /// «откуда эта нота» находится grep'ом по Push().
    /// </summary>
    public static void Push(string label, string message)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        Queue.Enqueue(new StateAnnounce { Label = label, Messages = [message] });
        while (Queue.Count > Cap)
        {
            Queue.TryDequeue(out _);
        }
    }

    /// <summary>Выгребает очередь (вызывает адаптер-анонсер при сборке блока).</summary>
    public static IReadOnlyList<StateAnnounce> Drain()
    {
        var list = new List<StateAnnounce>();
        while (Queue.TryDequeue(out var announce))
        {
            list.Add(announce);
        }
        return list;
    }

    /// <summary>Чистит очередь (смена сессии — не тащим мусор прошлой сессии).</summary>
    public static void Clear() => Queue.Clear();
}

/// <summary>
/// Адаптер: мусорка как анонсер в списке билдера. Билдер не выделяет её —
/// push и pull проходят один и тот же механизм.
/// </summary>
public sealed class BoardAnnouncer : IStateAnnouncer
{
    public IReadOnlyList<StateAnnounce> GetAnnounces() => AnnouncementBoard.Drain();
}
