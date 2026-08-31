using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Templates;

/// <summary>
/// Контекст мультимодальности для рендера: живой маркер медиа (из GET /props, рандомизирован
/// на каждый старт сервера) + провайдер base64-вложений по стабильному ID сообщения.
/// Если null — рендер текстовый (без маркеров), клиент шлёт строковый prompt.
/// </summary>
public sealed record MultimodalContext(
    string MediaMarker,
    Func<int, IReadOnlyList<string>> ArtifactsProvider)
{
    /// <summary>
    /// Собирает контекст для хода: маркер из /props (null — сервер текстовый; ошибка опроса
    /// глотается и НЕ кэшируется — см. ServerProps) + провайдер base64 из
    /// artifacts/msg_&lt;id&gt;/ текущей сессии. Файлы читаются лениво — на async-пути только /props.
    /// </summary>
    public static async Task<MultimodalContext?> BuildAsync(
        string sessionDir, string endpoint, ServerProps serverProps, CancellationToken cancellationToken)
    {
        await serverProps.FetchAsync(endpoint, cancellationToken);
        var marker = serverProps.MediaMarker;
        if (marker is null)
        {
            return null;
        }
        var store = new MessageMetaStore(sessionDir);
        return new MultimodalContext(marker, msgId =>
        {
            var result = new List<string>();
            foreach (var path in store.GetArtifacts(msgId))
            {
                if (File.Exists(path))
                {
                    result.Add(Convert.ToBase64String(File.ReadAllBytes(path)));
                }
            }
            return result;
        });
    }
}
