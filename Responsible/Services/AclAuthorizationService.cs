using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Responsible.Services;

// 一次性 ACL 授权的宿主侧实现。每个工具目录派生一个稳定的 capability SID，
// 只向目录树里的对象添加/删除这个 SID 的显式 ACE；不重写 DACL、不改变继承、
// 不碰 owner/SACL，因此原有权限保持原样。
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

    public (AclToolGrant? Grant, string? Error) Prepare(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "工具目录路径无效。");
        }
        full = Path.TrimEndingDirectorySeparator(full);
        if (!Directory.Exists(full)) return (null, "工具目录不存在。");
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return (null, "不能把整个盘符作为工具目录。");
        lock (_lock)
        {
            if (_grants.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)))
                return (null, "这个目录已经在授权列表中。");
        }

        string capability;
        try
        {
            capability = DeriveCapabilitySid(CapabilityName(full));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (null, $"无法派生 RVM 能力 SID：{ex.Message}");
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
                }
                : pending with { State = AclGrantState.Error, LastError = result.Message };
            TryUpsert(state, out var saveError);
            return result.Success && !string.IsNullOrEmpty(saveError)
                ? new AclResult(false, saveError, result.FailedObjects)
                : result;
        }
        catch (OperationCanceledException)
        {
            TryUpsert(grant with { State = AclGrantState.Error, LastError = "操作已取消。" },
                out _);
            return new AclResult(false, "操作已取消。", 0);
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
            TryUpsert(grant with { State = AclGrantState.Error, LastError = "操作已取消。" },
                out _);
            return new AclResult(false, "操作已取消。", 0);
        }
        finally
        {
            _operation.Release();
        }
    }

    private static AclResult ApplyTree(string root, string capabilitySid, bool grant,
        IProgress<AclProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<(string Path, bool IsDirectory)>();
        var errors = new List<string>();
        var failed = 0;
        progress?.Report(new AclProgress(0, 0, root));
        Enumerate(root, entries, errors, ref failed);
        if (entries.Count == 0)
            return new AclResult(false, "工具目录里没有可授权的对象。", failed);

        var total = entries.Count;
        var processed = 0;
        // 文件先处理：给目录写 DACL 可能触发子树继承刷新，放在后面能少做一次。
        for (var pass = 0; pass < 2; ++pass)
        {
            var wantDirectory = pass == 1;
            foreach (var (path, isDirectory) in entries)
            {
                if (isDirectory != wantDirectory) continue;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ApplyAce(path, capabilitySid, grant);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException
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
            var action = grant ? "授予" : "撤销";
            return new AclResult(false,
                $"{action}完成，但 {failed} 个对象失败：{string.Join("；", errors.Take(3))}",
                failed);
        }
        progress?.Report(new AclProgress(total, total, root));
        return new AclResult(true,
            grant ? $"已为 {total} 个对象授予读取/执行权限。" : $"已撤销 {total} 个对象的 RVM 权限。",
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

    private static void ApplyAce(string path, string capabilitySid, bool grant)
    {
        if (!Native.ConvertStringSidToSidW(capabilitySid, out var sid) || sid == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RVM capability SID 无效");
        IntPtr descriptor = IntPtr.Zero;
        IntPtr newAcl = IntPtr.Zero;
        try
        {
            var status = Native.GetNamedSecurityInfoW(path, SeFileObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, out var dacl, IntPtr.Zero, out descriptor);
            if (status != 0) throw new Win32Exception((int)status);

            var exists = HasExplicitAce(dacl, capabilitySid);
            if (grant == exists) return;

            // 空 DACL 表示所有人完全访问；此时添加 ACE 会收窄权限，宁可拒绝。
            if (dacl == IntPtr.Zero)
                throw new InvalidOperationException("对象没有显式 DACL，无法在不改变权限的情况下修改。");

            var entry = new Native.ExplicitAccess
            {
                AccessPermissions = grant ? ToolRights : 0,
                AccessMode = grant ? GrantAccess : RevokeAccess,
                Inheritance = 0,
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
        }
        finally
        {
            if (newAcl != IntPtr.Zero) Native.LocalFree(newAcl);
            if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor);
            Native.LocalFree(sid);
        }
    }

    private static bool HasExplicitAce(IntPtr dacl, string capabilitySid)
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
                    throw new InvalidOperationException("系统没有返回 capability SID。");
                var sid = Marshal.ReadIntPtr(sids);
                if (!Native.ConvertSidToStringSidW(sid, out var text) || text == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    return Marshal.PtrToStringUni(text) ??
                           throw new InvalidOperationException("capability SID 为空。");
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
        throw new InvalidOperationException("系统不支持 DeriveCapabilitySidsFromName。");
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
            error = $"无法保存 ACL 授权清单：{ex.Message}";
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
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"无法读取 ACL 授权清单：{ex.Message}";
            return [];
        }
    }

    private static AclToolGrant NormalizeLoaded(AclToolGrant row)
    {
        if (row.State == AclGrantState.Pending)
            return row with { State = AclGrantState.Error, LastError = "上次操作未完成，请重试或移除。" };
        if (row.State != AclGrantState.Authorized) return row;
        if (!Directory.Exists(row.Path))
        {
            return row with
            {
                State = AclGrantState.Error,
                LastError = "工具目录已不存在，请重新授权或移除。",
            };
        }
        try
        {
            if (!RootHasAce(row.Path, row.CapabilitySid))
            {
                return row with
                {
                    State = AclGrantState.Error,
                    LastError = "没有在工具目录上检测到 RVM 的授权，可能已被重装或恢复；请重新授权。",
                };
            }
        }
        catch (Win32Exception)
        {
            return row with
            {
                State = AclGrantState.Error,
                LastError = "无法读取工具目录的权限，请重新授权或移除。",
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
            return HasExplicitAce(dacl, capabilitySid);
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