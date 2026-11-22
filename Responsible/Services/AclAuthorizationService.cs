using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Responsible.Services;

// 一次性 ACL 授权的宿主侧实现。每个工具目录派生一个稳定的 capability SID，
// 只在目录根写入一条可继承（OI|CI）的 ACE，让 Windows 在访问检查时把权限
// 传播给整棵树，不逐个改写子对象 DACL；撤销时会清理旧版本遗留的逐对象 ACE。
// 不重写 DACL、不碰 owner/SACL，因此原有权限保持原样。
public sealed class AclAuthorizationService : IAclAuthorizationService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "acl-authorizations.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // GENERIC_READ | GENERIC_EXECUTE | FILE_TRAVERSE
    private const uint ToolRights = 0x80000000u | 0x20000000u | 0x20u;
    private const int SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x00000004u;
    private const int TrusteeIsSid = 0;
    private const int TrusteeIsUnknown = 0;
    private const int GrantAccess = 1;
    private const int RevokeAccess = 4;
    private const int ObjectInheritAce = 0x1;
    private const int ContainerInheritAce = 0x2;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly List<AclToolGrant> _grants;

    public AclAuthorizationService()
    {
        _grants = Load(out var loadError);
        LoadError = loadError;
    }

    public IReadOnlyList<AclToolGrant> Grants
    {
        get { lock (_lock) return _grants.ToArray(); }
    }

    public string? LoadError { get; }

    public IReadOnlyList<AclValidationIssue> RefreshValidity()
    {
        lock (_lock)
        {
            var issues = new List<AclValidationIssue>();
            var changed = false;
            for (var index = 0; index < _grants.Count; index++)
            {
                var grant = _grants[index];
                var (valid, detail) = ValidateGrant(grant);
                if (!valid) issues.Add(new AclValidationIssue(grant.Path, grant.Name, detail));
                if (grant.IsValid == valid) continue;
                _grants[index] = grant with { IsValid = valid };
                changed = true;
            }

            if (changed && !TrySaveLocked(out var saveError))
                issues.Add(new AclValidationIssue("ACL grants", "acl-authorizations.json", saveError));
            return issues;
        }
    }

    private static (bool Valid, string Detail) ValidateGrant(AclToolGrant grant)
    {
        if (!Directory.Exists(grant.Path)) return (false, "The tool directory no longer exists");
        try
        {
            if (!HasInheritableAceAt(grant.Path, grant.CapabilitySid))
                return (false, "The directory root is missing an inheritable RVM ACE; regrant it");
            if (!string.Equals(DeriveCapabilitySid(CapabilityName(grant.Path)),
                    grant.CapabilitySid, StringComparison.OrdinalIgnoreCase))
                return (false, "The capability SID does not match the directory path; remove and re-add it");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return (false, $"Unable to read directory permissions: {ex.Message}");
        }
        return (true, string.Empty);
    }

    public (AclToolGrant? Grant, string? Error) Prepare(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "Invalid tool directory path.");
        }
        full = Path.TrimEndingDirectorySeparator(full);
        if (!Directory.Exists(full)) return (null, "The tool directory does not exist.");
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return (null, "An entire drive cannot be used as a tool directory.");
        lock (_lock)
        {
            if (_grants.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)))
                return (null, "This directory is already in the grant list.");
        }

        string capability;
        try
        {
            capability = DeriveCapabilitySid(CapabilityName(full));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (null, $"Unable to derive the RVM capability SID: {ex.Message}");
        }
        return (new AclToolGrant(Guid.NewGuid().ToString("n"), full,
            new DirectoryInfo(full).Name, capability, 0, AclGrantState.Pending, null), null);
    }

    public async Task<AclResult> GrantAsync(AclToolGrant grant, IProgress<AclProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = grant with { State = AclGrantState.Pending, LastError = null };
            if (!TryUpsert(pending, out var storeError))
                return new AclResult(false, storeError, 0);

            var result = await Task.Run(
                () => ApplyTree(grant.Path, grant.CapabilitySid, grant: true, progress,
                    cancellationToken), cancellationToken).ConfigureAwait(false);
            var state = result.Success
                ? pending with
                {
                    State = AclGrantState.Authorized,
                    AuthorizedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastError = null,
                    IsValid = true,
                }
                : pending with { State = AclGrantState.Error, LastError = result.Message };
            TryUpsert(state, out var saveError);
            return result.Success && !string.IsNullOrEmpty(saveError)
                ? new AclResult(false, saveError, result.FailedObjects)
                : result;
        }
        catch (OperationCanceledException)
        {
            TryUpsert(grant with { State = AclGrantState.Error, LastError = "Operation canceled." },
                out _);
            return new AclResult(false, "Operation canceled.", 0);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<AclResult> RevokeAsync(AclToolGrant grant, IProgress<AclProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = grant with { State = AclGrantState.Pending, LastError = null };
            TryUpsert(pending, out _);
            var result = await Task.Run(
                () => ApplyTree(grant.Path, grant.CapabilitySid, grant: false, progress,
                    cancellationToken), cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                if (!TryRemove(grant.Id, out var storeError))
                    return new AclResult(false, storeError, 0);
            }
            else
            {
                TryUpsert(grant with { State = AclGrantState.Error, LastError = result.Message },
                    out _);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            TryUpsert(grant with { State = AclGrantState.Error, LastError = "Operation canceled." },
                out _);
            return new AclResult(false, "Operation canceled.", 0);
        }
        finally
        {
            _operation.Release();
        }
    }

    private static AclResult ApplyTree(string root, string capabilitySid, bool grant,
        IProgress<AclProgress>? progress, CancellationToken cancellationToken)
    {
        return grant
            ? GrantRoot(root, capabilitySid, progress)
            : RevokeTree(root, capabilitySid, progress, cancellationToken);
    }

    // 一条 OI|CI 的根 ACE 覆盖整棵树：Windows 在访问检查时评估继承，已存在和
    // 将来新增的子对象都自动生效，不需要逐个目录/文件写规则。
    private static AclResult GrantRoot(string root, string capabilitySid,
        IProgress<AclProgress>? progress)
    {
        progress?.Report(new AclProgress(0, 1, root));
        try
        {
            if (!HasInheritableAceAt(root, capabilitySid))
            {
                // 旧版本在根上留下的是不可继承 ACE，先撤掉再写入继承 ACE。
                ApplyAce(root, capabilitySid, grant: false);
                ApplyAce(root, capabilitySid, grant: true,
                    ObjectInheritAce | ContainerInheritAce);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return new AclResult(false, $"{root}：{ex.Message}", 1);
        }
        progress?.Report(new AclProgress(1, 1, root));
        return new AclResult(true, "Wrote an inheritable read/execute grant at the directory root.", 0);
    }

    // 撤销时仍遍历一次：新授权只会在根上留下继承 ACE（撤根即止），但旧版本
    // 可能已经给每个子对象写过显式 ACE，这里顺手清掉，避免遗留授权。
    private static AclResult RevokeTree(string root, string capabilitySid,
        IProgress<AclProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<(string Path, bool IsDirectory)>();
        var errors = new List<string>();
        var failed = 0;
        var changed = 0;
        progress?.Report(new AclProgress(0, 0, root));
        Enumerate(root, entries, errors, ref failed);
        if (entries.Count == 0)
            return new AclResult(false, "The tool directory contains no objects to revoke from.", failed);

        var total = entries.Count;
        var processed = 0;

        // 先撤根 ACE：系统会级联移除子对象上的继承副本，子对象无需重写 DACL。
        try
        {
            if (ApplyAce(root, capabilitySid, grant: false)) ++changed;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            ++failed;
            if (errors.Count < 20) errors.Add($"{root}：{ex.Message}");
        }
        ++processed;
        progress?.Report(new AclProgress(processed, total, root));

        // 文件先处理：给目录写 DACL 可能触发子树继承刷新，放在后面能少做一次。
        for (var pass = 0; pass < 2; ++pass)
        {
            var wantDirectory = pass == 1;
            foreach (var (path, isDirectory) in entries)
            {
                if (isDirectory != wantDirectory) continue;
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // 子对象的继承 ACE 随撤根自动消失；这里只清理旧版本遗留的显式 ACE。
                    if (HasExplicitAce(path, isDirectory, capabilitySid)
                        && ApplyAce(path, capabilitySid, grant: false)) ++changed;
                }
                catch (Exception ex) when (ex is Win32Exception or IOException
                                               or InvalidOperationException
                                               or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                {
                    ++failed;
                    if (errors.Count < 20) errors.Add($"{path}：{ex.Message}");
                }
                ++processed;
                if ((processed & 31) == 0 || processed == total)
                    progress?.Report(new AclProgress(processed, total, path));
            }
        }

        if (failed > 0)
        {
            return new AclResult(false,
                $"Revocation completed, but {failed} objects failed: {string.Join("; ", errors.Take(3))}",
                failed);
        }
        progress?.Report(new AclProgress(total, total, root));
        return new AclResult(true,
            changed == 0 ? "This tool directory has no RVM grant to revoke." : $"Revoked RVM permissions on {changed} objects.",
            0);
    }

    private static void Enumerate(string root, List<(string Path, bool IsDirectory)> entries,
        List<string> errors, ref int failed)
    {
        entries.Add((root, true));
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = directory.EnumerateFileSystemInfos().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                ++failed;
                if (errors.Count < 20) errors.Add($"{directory.FullName}：{ex.Message}");
                continue;
            }
            foreach (var child in children)
            {
                try
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (child is DirectoryInfo subdirectory)
                    {
                        entries.Add((subdirectory.FullName, true));
                        pending.Push(subdirectory);
                    }
                    else
                    {
                        entries.Add((child.FullName, false));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ++failed;
                    if (errors.Count < 20) errors.Add($"{child.FullName}：{ex.Message}");
                }
            }
        }
    }

    private static bool ApplyAce(string path, string capabilitySid, bool grant, int inheritance = 0)
    {
        if (!Native.ConvertStringSidToSidW(capabilitySid, out var sid) || sid == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Invalid RVM capability SID");
        IntPtr descriptor = IntPtr.Zero;
        IntPtr newAcl = IntPtr.Zero;
        try
        {
            var status = Native.GetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, out var dacl, IntPtr.Zero, out descriptor);
            if (status != 0) throw new Win32Exception((int)status);

            var exists = HasAce(dacl, capabilitySid);
            if (grant == exists) return false;

            // 空 DACL 表示所有人完全访问；此时添加 ACE 会收窄权限，宁可拒绝。
            if (dacl == IntPtr.Zero)
                throw new InvalidOperationException("The object has no explicit DACL and cannot be modified without changing permissions.");

            var entry = new Native.ExplicitAccess
            {
                AccessPermissions = grant ? ToolRights : 0,
                AccessMode = grant ? GrantAccess : RevokeAccess,
                Inheritance = grant ? inheritance : 0,
                Trustee = new Native.Trustee
                {
                    TrusteeForm = TrusteeIsSid,
                    TrusteeType = TrusteeIsUnknown,
                    Name = sid,
                },
            };
            status = Native.SetEntriesInAclW(1, [entry], dacl, out newAcl);
            if (status != 0) throw new Win32Exception((int)status);
            status = Native.SetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, newAcl, IntPtr.Zero);
            if (status != 0) throw new Win32Exception((int)status);
            return true;
        }
        finally
        {
            if (newAcl != IntPtr.Zero) Native.LocalFree(newAcl);
            if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor);
            Native.LocalFree(sid);
        }
    }

    private static bool HasInheritableAceAt(string path, string capabilitySid)
    {
        var status = Native.GetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, out var dacl, IntPtr.Zero, out var descriptor);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            return HasInheritableAce(dacl, capabilitySid);
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    // 只统计显式 ACE；继承副本由父目录管理，撤销时不该逐个重写。
    private static bool HasExplicitAce(string path, bool isDirectory, string capabilitySid)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity(path, AccessControlSections.Access)
            : new FileSecurity(path, AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: false,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (string.Equals(rule.IdentityReference.Value, capabilitySid,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasAce(IntPtr dacl, string capabilitySid) =>
        HasMatchingAce(dacl, capabilitySid, requireInheritable: false);

    private static bool HasInheritableAce(IntPtr dacl, string capabilitySid) =>
        HasMatchingAce(dacl, capabilitySid, requireInheritable: true);

    private static bool HasMatchingAce(IntPtr dacl, string capabilitySid, bool requireInheritable)
    {
        if (dacl == IntPtr.Zero) return false;
        var status = Native.GetExplicitEntriesFromAclW(dacl, out var count, out var entries);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            var size = Marshal.SizeOf<Native.ExplicitAccess>();
            for (var index = 0; index < count; ++index)
            {
                var entry = Marshal.PtrToStructure<Native.ExplicitAccess>(
                    IntPtr.Add(entries, checked((int)index * size)));
                if (entry.Trustee.TrusteeForm != TrusteeIsSid || entry.Trustee.Name == IntPtr.Zero)
                    continue;
                if (requireInheritable
                    && (entry.Inheritance & (ObjectInheritAce | ContainerInheritAce))
                    != (ObjectInheritAce | ContainerInheritAce))
                    continue;
                if (!Native.ConvertSidToStringSidW(entry.Trustee.Name, out var text)
                    || text == IntPtr.Zero)
                    continue;
                try
                {
                    var value = Marshal.PtrToStringUni(text);
                    if (string.Equals(value, capabilitySid, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally
                {
                    Native.LocalFree(text);
                }
            }
            return false;
        }
        finally
        {
            Native.LocalFree(entries);
        }
    }

    private static string CapabilityName(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return "ResponsibleRvmTool-" + hash[..32];
    }

    private static string DeriveCapabilitySid(string name)
    {
        foreach (var libraryName in new[] { "kernelbase.dll", "kernel32.dll" })
        {
            if (!NativeLibrary.TryLoad(libraryName, out var module)) continue;
            if (!NativeLibrary.TryGetExport(module, "DeriveCapabilitySidsFromName", out var address))
                continue;
            var derive = Marshal.GetDelegateForFunctionPointer<DeriveCapabilitySids>(address);
            if (!derive(name, out var groupSids, out var groupCount, out var sids,
                    out var sidCount))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (sidCount == 0 || sids == IntPtr.Zero)
                    throw new InvalidOperationException("The system did not return a capability SID.");
                var sid = Marshal.ReadIntPtr(sids);
                if (!Native.ConvertSidToStringSidW(sid, out var text) || text == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    return Marshal.PtrToStringUni(text) ??
                           throw new InvalidOperationException("The capability SID is empty.");
                }
                finally
                {
                    Native.LocalFree(text);
                }
            }
            finally
            {
                for (uint index = 0; index < sidCount; ++index)
                    Native.LocalFree(Marshal.ReadIntPtr(sids, checked((int)index * IntPtr.Size)));
                for (uint index = 0; index < groupCount; ++index)
                    Native.LocalFree(Marshal.ReadIntPtr(groupSids, checked((int)index * IntPtr.Size)));
                Native.LocalFree(sids);
                Native.LocalFree(groupSids);
            }
        }
        throw new InvalidOperationException("The system does not support DeriveCapabilitySidsFromName.");
    }

    private bool TryUpsert(AclToolGrant grant, out string error)
    {
        lock (_lock)
        {
            _grants.RemoveAll(item => item.Id == grant.Id);
            _grants.Add(grant);
            return TrySaveLocked(out error);
        }
    }

    private bool TryRemove(string id, out string error)
    {
        lock (_lock)
        {
            _grants.RemoveAll(item => item.Id == id);
            return TrySaveLocked(out error);
        }
    }

    private bool TrySaveLocked(out string error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temporary = StorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_grants, JsonOptions));
            File.Move(temporary, StorePath, true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Unable to save the ACL grant list: {ex.Message}";
            return false;
        }
    }

    private static List<AclToolGrant> Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(StorePath)) return [];
            var rows = JsonSerializer.Deserialize<List<AclToolGrant>>(File.ReadAllText(StorePath),
                JsonOptions);
            return (rows ?? [])
                .Where(row => !string.IsNullOrWhiteSpace(row.Id)
                              && !string.IsNullOrWhiteSpace(row.Path)
                              && !string.IsNullOrWhiteSpace(row.CapabilitySid))
                .Select(NormalizeLoaded)
                // IsValid 只是启动检查的缓存；旧清单没有这个字段时先按有效加载，
                // 由启动检查队列重新校验并写回。
                .Select(row => row with { IsValid = true })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Unable to read the ACL grant list: {ex.Message}";
            return [];
        }
    }

    private static AclToolGrant NormalizeLoaded(AclToolGrant row)
    {
        if (row.State == AclGrantState.Pending)
            return row with { State = AclGrantState.Error, LastError = "The previous operation did not complete. Retry or remove it." };
        if (row.State != AclGrantState.Authorized) return row;
        if (!Directory.Exists(row.Path))
        {
            return row with
            {
                State = AclGrantState.Error,
                LastError = "The tool directory no longer exists; regrant or remove it.",
            };
        }
        try
        {
            if (!RootHasAce(row.Path, row.CapabilitySid))
            {
                return row with
                {
                    State = AclGrantState.Error,
                    LastError = "No RVM grant was detected on the tool directory; it may have been reinstalled or restored. Regrant it.",
                };
            }
        }
        catch (Win32Exception)
        {
            return row with
            {
                State = AclGrantState.Error,
                LastError = "Unable to read the tool directory permissions; regrant or remove it.",
            };
        }
        return row;
    }

    private static bool RootHasAce(string path, string capabilitySid)
    {
        var status = Native.GetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, out var dacl, IntPtr.Zero, out var descriptor);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            return HasAce(dacl, capabilitySid);
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Trustee
        {
            public IntPtr MultipleTrustee;
            public int MultipleTrusteeOperation;
            public int TrusteeForm;
            public int TrusteeType;
            public IntPtr Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ExplicitAccess
        {
            public uint AccessPermissions;
            public int AccessMode;
            public int Inheritance;
            public Trustee Trustee;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetNamedSecurityInfoW(string objectName, int objectType,
            uint securityInfo, IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl,
            out IntPtr descriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint SetNamedSecurityInfoW(string objectName, int objectType,
            uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint SetEntriesInAclW(uint count,
            [In] ExplicitAccess[] entries, IntPtr oldAcl, out IntPtr newAcl);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern uint GetExplicitEntriesFromAclW(IntPtr acl, out uint count,
            out IntPtr entries);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ConvertStringSidToSidW(string value, out IntPtr sid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr value);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr memory);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool DeriveCapabilitySids(string name, out IntPtr groupSids,
        out uint groupCount, out IntPtr sids, out uint sidCount);

    }