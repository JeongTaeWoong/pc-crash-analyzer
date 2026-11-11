using System.IO.Compression;
using System.Text;

namespace StudyCore;

/// <summary>덤프 폴더를 zip 한 덩어리로 묶고(보내기 전), 받은 zip 을 안전하게 푼다(팀 쪽).</summary>
public static class BundleBuilder
{
    /// <summary>묶음 상한. 넘으면 events.json 을 빼고 다시 묶는다(덤프 대부분은 100KB 안팎).</summary>
    public const long MaxBundleBytes = 6 * 1024 * 1024;

    /// <summary>폴더 최상위의 *.json 만 묶는다(하위 폴더·다른 확장자는 넣지 않는다).</summary>
    public static byte[] FromDirectory(string dir, IReadOnlyDictionary<string, string>? extra, out bool truncated)
    {
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToDictionary(f => Path.GetFileName(f), f => File.ReadAllBytes(f));
        foreach (var kv in extra ?? new Dictionary<string, string>())
            files[kv.Key] = Encoding.UTF8.GetBytes(kv.Value);

        var zip = Zip(files);
        truncated = false;
        if (zip.Length > MaxBundleBytes && files.TryGetValue("events.json", out var ev))
        {
            files["events.json"] = Encoding.UTF8.GetBytes(
                $"{{\"status\":\"unavailable\",\"reason\":\"전송 상한 초과로 생략 (원본 {ev.Length} 바이트, 참여자 PC 에만 있음)\"}}");
            zip = Zip(files);
            truncated = true;
        }
        return zip;
    }

    public static byte[] FromEntries(IReadOnlyDictionary<string, string> entries) =>
        Zip(entries.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)));

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 받은 zip 풀기(팀 쪽). 경로가 든 이름·상위 이동(..)·너무 많은 항목·너무 큰 총량(zip 폭탄)은 거부한다.
    /// </summary>
    public static Dictionary<string, byte[]> Extract(byte[] zipBytes, long maxTotalBytes = 64L * 1024 * 1024, int maxEntries = 64)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > maxEntries) throw new InvalidDataException($"항목 {zip.Entries.Count}개 — 상한 {maxEntries}");
        long total = 0;
        foreach (var e in zip.Entries)
        {
            var name = e.FullName;
            if (name.Length == 0 || name.Length > 80 || name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"허용하지 않는 항목 이름: {name}");
            using var s = e.Open();
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > maxTotalBytes) throw new InvalidDataException("압축 해제 총량 상한 초과");
                ms.Write(buf, 0, n);
            }
            result[name] = ms.ToArray();
        }
        return result;
    }
}
