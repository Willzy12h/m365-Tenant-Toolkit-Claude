namespace BDIT.TenantToolkit.Engine.Execution;

/// <summary>Engineer-driven pause/stop signals. Stop takes effect at the next action boundary; an in-flight write is never abandoned.</summary>
public sealed class DeploymentControl
{
    private volatile bool _paused;
    private volatile bool _stop;

    public bool Paused => _paused;
    public bool StopRequested => _stop;

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    public void Stop()
    {
        _stop = true;
        _paused = false;
    }

    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_paused && !_stop)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
    }
}
