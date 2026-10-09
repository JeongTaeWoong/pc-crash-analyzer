namespace BlackBox.Core;

public interface ISignalSource : IDisposable
{
    string Name { get; }

    event Action<SignalEvent>? OnSignal;

    /// <summary>수집 시작. 절대 throw하지 않는다 — 실패는 unavailable 상태로 반환한다.</summary>
    SourceStatus Start();

    void Stop();
}
