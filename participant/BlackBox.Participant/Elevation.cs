using System.Security.AccessControl;
using System.Security.Principal;

namespace BlackBox.Participant;

/// <summary>
/// 관리자 권한(0.8): 매니페스트가 highestAvailable 이라 관리자 계정이면 시작할 때 UAC 로 권한을 받고,
/// 표준 사용자 계정이면 그대로 돈다(관리자 전용 신호는 수집기가 unavailable 로 기록 — CLAUDE.md 5).
/// 관리자로 돌 때는 일반 권한 프로그램이 쓸 수 없는 폴더에만 쓴다 — 사용자 폴더에 미리 심어 둔 연결(정션)을 따라
/// 관리자 권한으로 엉뚱한 곳에 파일을 쓰거나 지우는 일을 막는다.
/// </summary>
public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    });

    private static readonly Lazy<string> Sid = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value ?? "unknown";
    });

    public static bool IsElevated => Elevated.Value;

    public static string UserSid => Sid.Value;

    /// <summary>관리자 실행의 데이터 루트: C:\ProgramData\PcBlackBox (관리자·SYSTEM 만 쓰기).</summary>
    public static string SecureRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcBlackBox");

    /// <summary>관리자 실행의 기본 데이터 폴더: 사용자별 하위 폴더. 본인은 읽기만 된다("폴더 열기"로 기록을 볼 수 있게).</summary>
    public static string SecureDataDir => Path.Combine(SecureRoot, UserSid);

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    // OWNER RIGHTS(S-1-3-4): 관리자 프로세스가 만든 파일도 소유자는 "만든 사용자"라, 소유자의 암묵적 권한 변경(WRITE_DAC)으로
    // 같은 사용자의 일반 권한 프로그램이 쓰기 권한을 되찾을 수 있다. 이 항목이 상속되면 소유자는 읽기만 된다.
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    /// <summary>
    /// 데이터 루트와 사용자 폴더를 만들고 확인한다. 이미 있는데 관리자·SYSTEM 소유가 아니거나 연결(재분석 지점)이면 쓰지 않는다 —
    /// ProgramData 는 일반 사용자도 폴더를 만들 수 있어서, 미리 만들어 둔 함정일 수 있다.
    /// 반환: 문제 없으면 null, 아니면 참여자에게 보여 줄 이유.
    /// </summary>
    public static string? EnsureSecureDataDir(string dataDir)
    {
        var root = SecureRoot;
        try
        {
            // 루트: 관리자·SYSTEM 만 쓰기, OWNER RIGHTS 읽기 — 일반 권한 프로세스는 이 아래에 아무것도 만들 수 없다
            if (Prepare(root, (SystemSid, FileSystemRights.FullControl), (Admins, FileSystemRights.FullControl), (OwnerRights, FileSystemRights.ReadAndExecute)) is { } why)
                return why;
            // 사용자 폴더: 같은 규칙 + 본인 읽기("폴더 열기"로 기록을 볼 수 있게).
            // 권한을 직접 넘겨 만들면 Windows 가 상위 폴더 권한을 상속해 주지 않으므로(10/30 관리자 시험에서 확인) 전부 직접 건다.
            return Prepare(dataDir, (SystemSid, FileSystemRights.FullControl), (Admins, FileSystemRights.FullControl), (OwnerRights, FileSystemRights.ReadAndExecute),
                (new SecurityIdentifier(UserSid), FileSystemRights.ReadAndExecute));
        }
        catch (Exception ex)
        {
            return $"기록 폴더({root})를 준비하지 못했어요: {ex.Message}";
        }
    }

    /// <summary>
    /// 폴더가 없으면 상속을 끊고 주어진 권한만으로 만든다. 있으면 연결·소유자를 확인하고, 관리자 소유인데 권한이 기대와 다르면(이전 버전·관리자 손질)
    /// 다시 건다 — 관리자만 권한을 바꿀 수 있는 폴더라 되돌려도 안전하다. 반환: 문제 없으면 null.
    /// </summary>
    private static string? Prepare(string dir, params (SecurityIdentifier Who, FileSystemRights Rights)[] rules)
    {
        if (!Directory.Exists(dir)) new DirectoryInfo(dir).Create(Dacl(withOwner: true, rules)); // 이미 있으면(경합) 아무것도 안 한다 → 아래 확인
        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{dir} 가 연결(재분석 지점)이라 쓰지 않았어요.";
        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !(owner.Equals(Admins) || owner.Equals(SystemSid)))
            return $"{dir} 를 관리자가 아닌 계정({owner?.Value ?? "?"})이 만들어 두어 쓰지 않았어요. 연구팀에 알려 주세요.";
        if (Unsafe(dir, rules) == null) return null;
        info.SetAccessControl(Dacl(withOwner: false, rules));
        return Unsafe(dir, rules);
    }

    private static DirectorySecurity Dacl(bool withOwner, (SecurityIdentifier Who, FileSystemRights Rights)[] rules)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (withOwner) sec.SetOwner(Admins);
        foreach (var (who, rights) in rules)
            sec.AddAccessRule(new FileSystemAccessRule(who, rights, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>
    /// 폴더 확인: 상속이 끊겨 있고, 관리자·SYSTEM 말고는 쓰기 권한이 없으며, 기대한 항목(관리자 전체 권한·OWNER RIGHTS 등)이 하위로 상속되게 걸려 있어야 한다.
    /// 반환: 문제 없으면 null.
    /// </summary>
    private static string? Unsafe(string dir, (SecurityIdentifier Who, FileSystemRights Rights)[] expected)
    {
        var acl = new DirectoryInfo(dir).GetAccessControl();
        if (!acl.AreAccessRulesProtected) return $"{dir} 의 권한이 상위 폴더에서 상속되고 있어 쓰지 않았어요. 연구팀에 알려 주세요.";
        const FileSystemRights writeLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        foreach (var r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & writeLike) == 0) continue;
            var who = (SecurityIdentifier)r.IdentityReference;
            if (!who.Equals(Admins) && !who.Equals(SystemSid))
                return $"{dir} 에 관리자가 아닌 계정({who.Value})의 쓰기 권한이 있어 쓰지 않았어요. 연구팀에 알려 주세요.";
        }
        foreach (var (who, rights) in expected)
        {
            bool present = rules.Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(who)
                && (r.FileSystemRights & rights) == rights && r.InheritanceFlags == Inherit && r.PropagationFlags == PropagationFlags.None);
            if (!present) return $"{dir} 에 필요한 권한 항목({who.Value})이 없어 쓰지 않았어요. 연구팀에 알려 주세요.";
        }
        return null;
    }
}
