using BlackBox.Core;
using BlackBox.Marking;
using Xunit;

namespace BlackBox.Tests;

public class AutoMarkRateTests
{
    [Fact]
    public void SameTarget_CappedPerHour()
    {
        // 10/29 검토: 크래시 반복(10초마다)이 하루 수천 덤프를 만들던 것 → 같은 대상 1시간 6건
        var dir = Directory.CreateTempSubdirectory("rate").FullName;
        var buffer = new RingBuffer(TimeSpan.FromMinutes(1));
        var ms = new MarkService(buffer, new StateSnapshotter(new BlackBox.Sources.SensorSource()), new DumpWriter(dir),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), new List<SourceStatus>(), new BlackBoxConfig());
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var t = new EventLogTrigger(DateTimeOffset.Now, "Application", 1000, "Application Error", null, "test", "loop.exe", 100 + i,
                    null, "crash-1000", null, "c0000005", null, null, TriggerClassifier.Classify("Application", 1000, "Application Error", null));
                // 쿨다운(10초) 병합을 피하려고 대상 이름은 같게, 병합 여부와 무관하게 상한만 본다
                ms.OnAutoTrigger(t);
            }
        }
        finally { Console.SetOut(original); }
        var log = sw.ToString();
        Assert.Contains("1시간 6건 초과", log);
        Assert.Equal(4, log.Split('\n').Count(l => l.Contains("1시간 6건 초과")));
        ms.Drain(TimeSpan.FromSeconds(30));
    }
}
