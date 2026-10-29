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

    /// <summary>
    /// 데이터 루트와 사용자 폴더를 만들고 확인한다. 루트가 이미 있는데 관리자·SYSTEM 소유가 아니거나 연결(재분석 지점)이면 쓰지 않는다 —
    /// ProgramData 는 일반 사용자도 폴더를 만들 수 있어서, 미리 만들어 둔 함정일 수 있다.
    /// 반환: 문제 없으면 null, 아니면 참여자에게 보여 줄 이유.
    /// </summary>
    public static string? EnsureSecureDataDir(string dataDir)
    {
        var root = SecureRoot;
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        // OWNER RIGHTS(S-1-3-4): 관리자 프로세스가 만든 파일도 소유자는 "만든 사용자"라, 소유자의 암묵적 권한 변경(WRITE_DAC)으로
        // 같은 사용자의 일반 권한 프로그램이 쓰기 권한을 되찾을 수 있다. 이 항목이 상속되면 소유자는 읽기만 된다.
        var ownerRights = new SecurityIdentifier("S-1-3-4");
        try
        {
            if (!Directory.Exists(root))
            {
                // 상속 끊고 관리자·SYSTEM 만 — 일반 권한 프로세스는 이 아래에 아무것도 만들 수 없다
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                sec.SetOwner(admins);
                sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(ownerRights, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(root).Create(sec); // 이미 있으면(경합) 아무것도 안 한다 → 아래 확인에서 걸러진다
            }
            if (Unsafe(root, admins, system, requireOwnerRights: true, ownerRights) is { } why) return why;

            if (!Directory.Exists(dataDir))
            {
                var sec = new DirectorySecurity(); // 루트의 관리자·SYSTEM·OWNER RIGHTS 를 상속 + 본인 읽기
                sec.SetOwner(admins);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(UserSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(dataDir).Create(sec);
            }
            return Unsafe(dataDir, admins, system, requireOwnerRights: true, ownerRights);
        }
        catch (Exception ex)
        {
            return $"기록 폴더({root})를 준비하지 못했어요: {ex.Message}";
        }
    }

    /// <summary>폴더 확인: 연결이 아니고, 관리자·SYSTEM 소유이며, 그 둘 말고는 쓰기 권한이 없고, 소유자 권한 제한(OWNER RIGHTS)이 걸려 있어야 한다.</summary>
    private static string? Unsafe(string dir, SecurityIdentifier admins, SecurityIdentifier system, bool requireOwnerRights, SecurityIdentifier ownerRights)
    {
        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{dir} 가 연결(재분석 지점)이라 쓰지 않았어요.";
        var acl = info.GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !(owner.Equals(admins) || owner.Equals(system)))
            return $"{dir} 를 관리자가 아닌 계정({owner?.Value ?? "?"})이 만들어 두어 쓰지 않았어요. 연구팀에 알려 주세요.";
        const FileSystemRights writeLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule r in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & writeLike) == 0) continue;
            var who = (SecurityIdentifier)r.IdentityReference;
            if (!who.Equals(admins) && !who.Equals(system))
                return $"{dir} 에 관리자가 아닌 계정({who.Value})의 쓰기 권한이 있어 쓰지 않았어요. 연구팀에 알려 주세요.";
        }
        if (requireOwnerRights && !acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Equals(ownerRights) && r.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)))
            return $"{dir} 에 소유자 권한 제한이 없어 쓰지 않았어요. 연구팀에 알려 주세요.";
        return null;
    }
}
