using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexWallpaperSkin;

public static class CdpProcessIdentity
{
    private const int Success = 0;
    private const int AddressFamilyInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const int InsufficientBuffer = 122;
    internal const string OfficialPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string OfficialPackageFullNamePrefix = "OpenAI.Codex_";
    private const string OfficialPackageFullNameSuffix = "_x64__2p2nqsd0c76g0";

    public static void EnsureOfficialCodexOwnsPort(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Codex CDP process verification requires Windows.");
        }

        var processIds = GetListenerProcessIds(port).Distinct().ToArray();
        if (processIds.Length == 0)
        {
            throw new InvalidOperationException($"No Windows listener process owns CDP port {port}.");
        }

        var failures = new List<string>();
        foreach (var processId in processIds)
        {
            var identity = TryGetProcessIdentity(processId);
            if (identity is null)
            {
                failures.Add($"PID {processId}: unable to inspect the listener process");
                continue;
            }
            if (identity.PackageFamilyName is null || identity.PackageFullName is null)
            {
                failures.Add($"PID {processId}: the listener has no Windows package identity");
                continue;
            }
            if (!IsOfficialPackageIdentity(identity.PackageFamilyName, identity.PackageFullName))
            {
                failures.Add($"PID {processId}: package family '{identity.PackageFamilyName}' is not OpenAI.Codex");
                continue;
            }
            if (identity.PackageStagedPath is null)
            {
                failures.Add($"PID {processId}: Windows did not return the package's staged install path");
                continue;
            }
            if (!IsExpectedCodexExecutablePath(
                    identity.ImagePath,
                    identity.PackageFullName,
                    identity.PackageStagedPath))
            {
                failures.Add($"PID {processId}: the listener executable is not app\\ChatGPT.exe or app\\Codex.exe in the registered package");
            }
        }
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                $"Port {port} has a listener that is not the official OpenAI.Codex Windows package. " +
                $"Refusing the CDP connection. Details: {string.Join("; ", failures)}.");
        }
    }

    public static IReadOnlyList<int> FindRunningOfficialCodexProcessIds()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<int>();
        foreach (var processName in new[] { "ChatGPT", "Codex" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var identity = TryGetProcessIdentity(process.Id);
                        if (identity?.PackageFamilyName is null
                            || identity.PackageFullName is null
                            || identity.PackageStagedPath is null
                            || !IsOfficialPackageIdentity(identity.PackageFamilyName, identity.PackageFullName)
                            || !IsExpectedCodexExecutablePath(
                                identity.ImagePath, identity.PackageFullName, identity.PackageStagedPath))
                        {
                            continue;
                        }
                        result.Add(process.Id);
                    }
                    catch
                    {
                        // A process can exit while it is being inspected.
                    }
                }
            }
        }
        return result.Distinct().Order().ToArray();
    }

    internal static bool IsOfficialPackageIdentity(string? packageFamilyName, string? packageFullName)
    {
        return string.Equals(packageFamilyName, OfficialPackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && packageFullName is not null
            && packageFullName.StartsWith(OfficialPackageFullNamePrefix, StringComparison.OrdinalIgnoreCase)
            && packageFullName.EndsWith(OfficialPackageFullNameSuffix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasExpectedCodexExecutableLayout(string? imagePath, string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(packageFullName))
        {
            return false;
        }
        try
        {
            var normalizedImagePath = Path.GetFullPath(imagePath).TrimEnd('\\', '/');
            var expectedChatGptSuffix = Path.DirectorySeparatorChar +
                Path.Combine("WindowsApps", packageFullName, "app", "ChatGPT.exe");
            var expectedCodexSuffix = Path.DirectorySeparatorChar +
                Path.Combine("WindowsApps", packageFullName, "app", "Codex.exe");
            return normalizedImagePath.EndsWith(expectedChatGptSuffix, StringComparison.OrdinalIgnoreCase)
                || normalizedImagePath.EndsWith(expectedCodexSuffix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsExpectedCodexExecutablePath(
        string? imagePath,
        string packageFullName,
        string packageStagedPath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)
            || !HasExpectedCodexExecutableLayout(imagePath, packageFullName))
        {
            return false;
        }
        try
        {
            var normalizedStagedPath = Path.GetFullPath(packageStagedPath).TrimEnd('\\', '/');
            if (!Path.GetFileName(normalizedStagedPath).Equals(packageFullName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Directory.GetParent(normalizedStagedPath)?.Name,
                    "WindowsApps",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var executableName = Path.GetFileName(imagePath);
            var stagedExecutablePath = Path.Combine(normalizedStagedPath, "app", executableName);
            return IsSameFile(imagePath, stagedExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSameFile(string firstPath, string secondPath)
    {
        using var first = OpenForIdentity(firstPath);
        using var second = OpenForIdentity(secondPath);
        if (first.IsInvalid || second.IsInvalid
            || !GetFileInformationByHandle(first, out var firstInfo)
            || !GetFileInformationByHandle(second, out var secondInfo))
        {
            return false;
        }
        return firstInfo.VolumeSerialNumber == secondInfo.VolumeSerialNumber
            && firstInfo.FileIndexHigh == secondInfo.FileIndexHigh
            && firstInfo.FileIndexLow == secondInfo.FileIndexLow;
    }

    private static SafeFileHandle OpenForIdentity(string path)
    {
        return CreateFileW(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
    }

    private static IEnumerable<int> GetListenerProcessIds(int expectedPort)
    {
        var size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AddressFamilyInet, TcpTableOwnerPidListener, 0);
        if (first != InsufficientBuffer || size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, true, AddressFamilyInet, TcpTableOwnerPidListener, 0);
            if (result != Success)
            {
                yield break;
            }
            var count = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(rowPointer);
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort));
                var localAddress = new IPAddress(row.LocalAddress);
                if (localPort == expectedPort && IPAddress.IsLoopback(localAddress))
                {
                    yield return checked((int)row.OwningProcessId);
                }
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ProcessIdentity? TryGetProcessIdentity(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var imagePath = TryGetProcessPath(handle);
            var packageFamilyName = TryGetPackageFamilyName(handle);
            var packageFullName = TryGetPackageFullName(handle);
            var packageStagedPath = packageFullName is null ? null : TryGetPackageStagedPath(packageFullName);
            return new ProcessIdentity(imagePath, packageFamilyName, packageFullName, packageStagedPath);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryGetProcessPath(IntPtr process)
    {
        var capacity = 32768;
        var builder = new StringBuilder(capacity);
        return QueryFullProcessImageName(process, 0, builder, ref capacity)
            ? builder.ToString()
            : null;
    }

    private static string? TryGetPackageFamilyName(IntPtr process)
    {
        uint length = 0;
        var first = GetPackageFamilyName(process, ref length, null);
        if (first != InsufficientBuffer || length == 0)
        {
            return null;
        }
        var builder = new StringBuilder(checked((int)length));
        return GetPackageFamilyName(process, ref length, builder) == Success
            ? builder.ToString()
            : null;
    }

    private static string? TryGetPackageFullName(IntPtr process)
    {
        uint length = 0;
        var first = GetPackageFullName(process, ref length, null);
        if (first != InsufficientBuffer || length == 0)
        {
            return null;
        }
        var builder = new StringBuilder(checked((int)length));
        return GetPackageFullName(process, ref length, builder) == Success
            ? builder.ToString()
            : null;
    }

    private static string? TryGetPackageStagedPath(string packageFullName)
    {
        uint length = 0;
        var first = GetStagedPackagePathByFullName(packageFullName, ref length, null);
        if (first != InsufficientBuffer || length == 0)
        {
            return null;
        }
        var builder = new StringBuilder(checked((int)length));
        return GetStagedPackagePathByFullName(packageFullName, ref length, builder) == Success
            ? builder.ToString()
            : null;
    }

    private sealed record ProcessIdentity(
        string? ImagePath,
        string? PackageFamilyName,
        string? PackageFullName,
        string? PackageStagedPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(
        IntPtr process,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFullName(
        IntPtr process,
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetStagedPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        StringBuilder? path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
