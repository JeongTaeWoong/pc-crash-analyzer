using System.Text.RegularExpressions;

namespace BlackBox.Core;

/// <summary>덤프 직전 직렬화된 JSON 문자열에서 사용자 프로필 경로를 %USERPROFILE%로 치환한다.</summary>
public static class Sanitizer
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string SanitizeJson(string json)
    {
        // JSON 안의 백슬래시는 \\ 로 이스케이프되어 있으므로 그 형태를 먼저,
        // 혹시 모를 원형(슬래시 경로 포함)도 함께 치환한다. 대소문자 무시.
        foreach (var variant in new[]
                 {
                     Profile.Replace("\\", "\\\\"),      // C:\\Users\\name (JSON 이스케이프)
                     Profile,                             // C:\Users\name
                     Profile.Replace("\\", "/"),          // C:/Users/name
                 })
        {
            json = Regex.Replace(json, Regex.Escape(variant), "%USERPROFILE%", RegexOptions.IgnoreCase);
        }
        return json;
    }
}
