using System.Security.AccessControl;
using System.Security.Principal;

namespace AccountUsageUI.Infrastructure.Profiles;

internal sealed class CurrentUserStorageAcl
{
    private readonly SecurityIdentifier _currentUser;
    private readonly SecurityIdentifier _localSystem = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    public CurrentUserStorageAcl()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _currentUser = identity.User
            ?? throw new InvalidOperationException(
                "현재 Windows 사용자를 확인할 수 없습니다.");
    }

    public void EnsureProtectedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            directory.SetAccessControl(CreateDirectorySecurity());
            return;
        }

        _ = FileSystemAclExtensions.CreateDirectory(
            CreateDirectorySecurity(),
            path);
    }

    private DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(_currentUser);

        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit |
            InheritanceFlags.ObjectInherit;

        security.AddAccessRule(
            new FileSystemAccessRule(
                _currentUser,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        security.AddAccessRule(
            new FileSystemAccessRule(
                _localSystem,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

        return security;
    }
}
