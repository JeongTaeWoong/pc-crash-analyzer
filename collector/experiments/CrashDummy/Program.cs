// 자동 감지 테스트용 더미 크래시 앱. 인자:
//   a = 즉시 unhandled exception
//   b = 5초 후 Environment.FailFast
//   c = 15초 대기 후 unhandled exception (10초 모듈 스냅샷에 잡힌 뒤 죽는 케이스)
string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "a";
Console.WriteLine($"MODE={mode} PID={Environment.ProcessId} START={DateTimeOffset.Now:O}");
Console.Out.Flush();

switch (mode)
{
    case "a":
        Crash();
        break;
    case "b":
        Thread.Sleep(5000);
        Console.WriteLine($"CRASH_AT={DateTimeOffset.Now:O}");
        Console.Out.Flush();
        Environment.FailFast("CrashDummy: intentional FailFast (case b)");
        break;
    case "c":
        Thread.Sleep(15000);
        Crash();
        break;
    default:
        Console.WriteLine("usage: CrashDummy a|b|c");
        break;
}

static void Crash()
{
    Console.WriteLine($"CRASH_AT={DateTimeOffset.Now:O}");
    Console.Out.Flush();
    throw new InvalidOperationException("CrashDummy: intentional unhandled exception");
}
