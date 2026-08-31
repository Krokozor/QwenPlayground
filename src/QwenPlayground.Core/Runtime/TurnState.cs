namespace QwenPlayground.Core.Runtime;

/// <summary>Состояние хода в реестре: очередь → исполнение → один из трёх терминальных.</summary>
public enum TurnState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}
