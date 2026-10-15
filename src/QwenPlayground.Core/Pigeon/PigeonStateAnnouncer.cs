using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Pigeon;

/// <summary>
/// Pigeon-анонсер state-блока: на момент рендера ищет живое PigeonSession по каталогу
/// сессии (для main-сессии каталог динамический — селектор главного окна переключает).
/// Не подключено — тишина (анонсер возвращает пустой список).
/// </summary>
public sealed class PigeonStateAnnouncer : IStateAnnouncer
{
    private readonly Func<string> _sessionDir;

    public PigeonStateAnnouncer(Func<string> sessionDir)
    {
        _sessionDir = sessionDir;
    }

    public IReadOnlyList<StateAnnounce> GetAnnounces() =>
        PigeonSession.For(_sessionDir())?.GetAnnounces() ?? [];
}
