// 스파이크 B: ETW 커널 프로바이더(ProcessStart/Stop, ImageLoad) 10초 구독 후 이벤트 수 집계
// NT Kernel Logger는 관리자 필수 — 비관리자면 unavailable로 기록
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Text.Encodings.Web;
using System.Text.Json;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "B-EtwKernel", ["ranAt"] = DateTime.Now.ToString("s") };
bool elevated = TraceEventSession.IsElevated() ?? false;
result["isElevated"] = elevated;

try
{
    if (!elevated)
    {
        result["status"] = "unavailable";
        result["reason"] = "requires administrator: NT Kernel Logger(커널 ETW 세션)는 관리자 권한 필수";
    }
    else
    {
        int procStart = 0, procStop = 0, imgLoad = 0;
        using var session = new TraceEventSession("CrashCollector-SpikeB");
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);
        session.Source.Kernel.ProcessStart += _ => Interlocked.Increment(ref procStart);
        session.Source.Kernel.ProcessStop += _ => Interlocked.Increment(ref procStop);
        session.Source.Kernel.ImageLoad += _ => Interlocked.Increment(ref imgLoad);
        var pump = Task.Run(() => session.Source.Process());
        Thread.Sleep(10_000);
        session.Stop();
        pump.Wait(3_000);
        result["status"] = "ok";
        result["durationSec"] = 10;
        result["counts"] = new { processStart = procStart, processStop = procStop, imageLoad = imgLoad };
    }
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
