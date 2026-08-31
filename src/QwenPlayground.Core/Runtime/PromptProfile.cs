namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Кусок профиля чата «системный промпт и правила поведения»: текст промпта, контракт
/// результата, политика инструментов и усилие размышления. Отделён от семплера
/// (<see cref="SamplerProfile"/>): то, КАК модель генерирует, не смешивается с тем,
/// КЕМ она является в этом чате и ЧТО ей можно делать.
/// </summary>
public sealed class PromptProfile
{
    /// <summary>Системный промпт специализированного чата; пусто — инъекции нет (обычный чат).</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Требования к формату результата — дополняют промпт секцией.</summary>
    public string ResultContract { get; set; } = string.Empty;

    /// <summary>Рекламировать и исполнять инструменты вообще.</summary>
    public bool Tools { get; set; } = true;

    /// <summary>
    /// Белый список имён инструментов; пусто = все доступные. Гранулярная настройка
    /// поверх флага Tools, а не замена его.
    /// </summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>Усилие размышления («XHigh»/«Medium»/«Low»); пусто — из общих настроек.</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>Промпт для инъекции: текст + секция контракта (если задан); null — инъекции нет.</summary>
    public string? RenderSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(ResultContract)
            ? SystemPrompt
            : SystemPrompt + "\n\n— Контракт результата —\n" + ResultContract.Trim();
    }
}
