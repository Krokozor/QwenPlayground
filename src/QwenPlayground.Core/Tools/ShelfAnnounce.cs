using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Анонс смены полок (групп инструментов) на доске сообщений state-блока: кто сделал
/// (owner/agent) и что изменилось. Вызывается вызывающими местами — тулом агента
/// (activate_shelf/deactivate_shelf), UI-меню (ShelfUiViewModel.ToggleShelf) и
/// pigeon_connect (авто-активация полки Pigeon) — <see cref="ShelfState"/> остаётся чистой
/// логикой состояния и не знает об анонсе.
///
/// Канал — <see cref="AnnouncementBoard"/> (push, одноразовый: живёт до ближайшей сборки
/// state-блока, где BoardAnnouncer его дрейнит). Метка = исполнитель: «owner» (юзер из UI)
/// или «agent» (модель через тул) — модель видит «метка: сообщение» и понимает, чьё это
/// действие (раньше владелец анонсировал свои переключения словом в чате). Системные смены
/// (FlushPending, авто-выключение неиспользуемых после компакции) не анонсируются — это не
/// действия owner/agent.
/// </summary>
public static class ShelfAnnounce
{
    /// <summary>Метка для действий юзера (UI-меню полок).</summary>
    public const string Owner = "owner";

    /// <summary>Метка для действий модели (activate_shelf/deactivate_shelf, pigeon_connect).</summary>
    public const string Agent = "agent";

    /// <summary>
    /// Пушит анонс смены полки, если операция реально изменила состояние. AlreadyActive/
    /// NotActive — без изменений → тишина. Вызывать из места, которое знает исполнителя
    /// (<see cref="Owner"/>/<see cref="Agent"/>).
    /// </summary>
    public static void Push(string actor, ShelfResult result, ToolGroup group)
    {
        var message = result switch
        {
            ShelfResult.Activated => $"activated shelf '{group}' — its tools join the prompt next turn",
            ShelfResult.Reactivated => $"re-activated shelf '{group}' — pending deactivation canceled, no prompt change",
            ShelfResult.Deactivated => $"scheduled deactivation of shelf '{group}' — it leaves the prompt at the next natural change",
            _ => null,
        };
        if (message is not null)
        {
            AnnouncementBoard.Push(actor, message);
        }
    }
}
