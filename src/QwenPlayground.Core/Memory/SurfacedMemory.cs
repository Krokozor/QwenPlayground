namespace QwenPlayground.Core.Memory;

/// <summary>
/// Всплывшее воспоминание: факт + релевантность вектору диалога (state-блок).
/// Sightings — число последовательных сэмплов реколла, где факт встретился. Live-реколл
/// (во время генерации) добавляет с Sightings=1: «слишком рано» отсеивается правилом
/// стабильности — в state-блок попадают факты с Sightings не ниже порога (см. MemorySurfacer).
/// </summary>
public sealed record SurfacedMemory(string Id, string Content, double Score)
{
    public int Sightings { get; set; }
}
