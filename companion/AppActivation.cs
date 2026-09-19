using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexWallpaperSkin;

public sealed record StartAppCandidate(string Name, string AppId)
{
    public override string ToString() => $"{Name} ({AppId})";
}

public sealed record ActivationResult(uint ProcessId, string Message);

public static class AppActivation
{
    private const uint ActivateOptionsNone = 0;
    internal const string OfficialAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly StartAppCandidate OfficialFallbackCandidate = new(
        "Codex (official package identity; Windows enumeration unavailable)",
        OfficialAumid);

    public static async Task<IReadOnlyList<StartAppCandidate>> FindCodexCandidatesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        const string command = "$items = @(Get-StartApps | Where-Object { $_.Name -match '(?i)codex' -or $_.AppID -match '(?i)^OpenAI\\.Codex_' } | Select-Object Name,AppID); ConvertTo-Json -InputObject $items -Compress";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [OfficialFallbackCandidate];
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [OfficialFallbackCandidate];
            }
            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return [OfficialFallbackCandidate];
            }

            using var document = JsonDocument.Parse(output);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            var candidates = elements
                .Select(element => new StartAppCandidate(
                    ReadString(element, "Name") ?? "Codex",
                    ReadString(element, "AppID") ?? string.Empty))
                .Where(candidate => IsOfficialAumid(candidate.AppId))
                .DistinctBy(candidate => candidate.AppId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.Length > 0 ? candidates : [OfficialFallbackCandidate];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [OfficialFallbackCandidate];
        }
    }

    public static ActivationResult ActivateWithCdp(string aumid, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            throw new ArgumentException("An AUMID is required. Use Detect first or paste the AppID from Get-StartApps.", nameof(aumid));
        }
        if (!IsOfficialAumid(aumid))
        {
            throw new ArgumentException(
                $"The AUMID must be the official Codex package identity ({OfficialAumid}).",
                nameof(aumid));
        }
        var endpointUri = CdpEndpoint.Normalize(endpoint);
        var arguments = $"--remote-debugging-port={endpointUri.Port} --remote-debugging-address=127.0.0.1";
        var activationType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var managerObject = Activator.CreateInstance(activationType)
            ?? throw new InvalidOperationException("IApplicationActivationManager is unavailable.");
        try
        {
            var manager = (IApplicationActivationManager)managerObject;
            var hresult = manager.ActivateApplication(aumid, arguments, ActivateOptionsNone, out var processId);
            Marshal.ThrowExceptionForHR(hresult);
            return new ActivationResult(
                processId,
                "Activation was requested without closing any Codex process. If Codex was already running, Windows may only foreground it and ignore new Chromium flags.");
        }
        finally
        {
            if (Marshal.IsComObject(managerObject))
            {
                Marshal.FinalReleaseComObject(managerObject);
            }
        }
    }

    public static Task<ActivationResult> ActivateWithCdpAsync(
        string aumid,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        // Keep the worker closure free of DispatcherObject instances. Callers
        // must capture UI values before crossing this asynchronous boundary.
        return Task.Run(() => ActivateWithCdp(aumid, endpoint), cancellationToken);
    }

    internal static bool IsOfficialAumid(string? value) =>
        value?.Equals(OfficialAumid, StringComparison.OrdinalIgnoreCase) == true;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }
}
