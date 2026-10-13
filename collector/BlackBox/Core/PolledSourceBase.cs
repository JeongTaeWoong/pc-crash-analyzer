namespace BlackBox.Core;

/// <summary>주기 샘플러 공통 베이스. 첫 샘플 성공 여부로 가용성을 판정하고, 이후 샘플 실패도 이벤트로 기록한다.</summary>
public abstract class PolledSourceBase : ISignalSource
{
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    protected PolledSourceBase(string name, TimeSpan interval)
    {
        Name = name;
        _interval = interval;
    }

    public string Name { get; }

    public event Action<SignalEvent>? OnSignal;

    protected abstract object Sample();

    public SourceStatus Start()
    {
        object first;
        try { first = Sample(); }
        catch (Exception ex)
        {
            return SourceStatus.Unavailable(Name, $"{ex.GetType().Name}: {ex.Message}");
        }

        Emit(first);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return SourceStatus.Ok(Name, $"interval={_interval.TotalSeconds:0.#}s");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch { }
    }

    public virtual void Dispose() => Stop();

    private void Emit(object payload) => OnSignal?.Invoke(SignalEvent.Create(Name, payload));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Emit(Sample()); }
                catch (Exception ex)
                {
                    // 샘플 실패도 데이터다 — 예외로 죽지 않는다
                    Emit(new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
