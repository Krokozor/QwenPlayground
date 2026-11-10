namespace QwenPlayground.Launcher;

public sealed record PigeonInstance
{
    public required string WorkspaceRoot { get; init; }
    public required string DataDir { get; init; }
    public required string ExecutablePath { get; init; }
    public int Port { get; init; }
    public int Pid { get; init; }
    public DateTime ProcessStartTimeUtc { get; init; }
    public bool LaunchedByLauncher { get; init; }
}
