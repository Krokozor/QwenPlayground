using System.Text.Json.Nodes;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Subagents;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Синхронный спавн субагента (решение 2026-09-22): отдельный агент с собственным
/// контекстом и инструментами (профиль «subagent», слот 1) выполняет задачу и возвращает
/// отчёт как ответ этого тула. Пока ходит субагент, main-агент ждёт: его KV-кеш
/// сохраняется в файл (sessions/&lt;id&gt;-kv.bin) и восстанавливается после (SubagentSpawner).
///
/// Доступен main-агенту и побочным окнам; субагентам — НЕТ (DeniedTools в их профиле:
/// без рекурсии). Идентичность/промпт субагента правится в редакторе профилей (ключ «subagent»).
/// </summary>
[Tool("spawn_subagent",
    "Запустить субагента: отдельного агента с собственным контекстом и инструментами, который синхронно выполнит задачу и вернёт отчёт как ответ этого тула. " +
    "Пока субагент работает, ты ждёшь (твой контекст сохраняется в файл и восстанавливается). " +
    "Используй для задач, которые не стоит тащить в свой контекст: объёмные исследования, работа с файлами, отчёты.\n\n" +
    "Когда НЕ использовать:\n" +
    "- Одиночное чтение файла или поиск по 2-3 файлам — сделай сам read_file/glob/grep (быстрее).\n" +
    "- Простая команда shell — запусти сам.\n" +
    "- Задачу, для которой нужен ТВОЙ контекст: субагент его не видит — придётся описывать всё заново в task.\n\n" +
    "Правила:\n" +
    "- task — полная формулировка: цель, контекст, ограничения, что считать результатом.\n" +
    "- Укажи явно: писать код/файлы или только исследовать (читать, искать, веб).\n" +
    "- Если возможно — как субагенту проверить свою работу (тесты, сборка, команды).\n" +
    "- Не дублируй делегированную работу сам. Отчёт субагента доверяй; он не виден владельцу — перескажи ему сам.\n\n" +
    "Окно субагента остаётся открытым: владелец может наблюдать за работой и продолжить разговор с ним.")]
public sealed class SpawnSubagentTool : AgentTool
{
    [ToolParameter("Полная формулировка задачи для субагента: цель, контекст, ограничения, что считать результатом. Субагент не видит твой контекст — опиши всё необходимое.", Required = true)]
    public string Task { get; set; } = string.Empty;

    [ToolParameter("Короткое название задачи (2–6 слов) — для заголовка окна субагента.", Required = false)]
    public string? Title { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var task = Task.Trim();
        if (task.Length == 0)
        {
            return "spawn_subagent: поле task пусто — опиши задачу для субагента.";
        }

        // Полное имя: «Main» здесь неоднозначно (класс и одноимённое пространство имён).
        var main = QwenPlayground.Core.Main.Main.Instance;
        if (main is null)
        {
            return "spawn_subagent: недоступно (композиционный корень не инициализирован).";
        }

        // Слот вызывающего: main → 0, побочное окно → 2 (ToolContext.SlotId из пиннинга хода).
        var outcome = await main.Subagents.SpawnAsync(task, Title?.Trim(), context.SlotId, cancellationToken);
        var note = outcome.KvNote is null ? string.Empty : $"\n\n[примечание по KV-кешу: {outcome.KvNote}]";
        var anchored = outcome.KvAnchored ? "Контекст main-агента сохранён в файл и восстановлен." : string.Empty;
        return $"Субагент завершил задачу. {anchored}\n\n— Отчёт субагента —\n{outcome.Report}{note}";
    }
}
