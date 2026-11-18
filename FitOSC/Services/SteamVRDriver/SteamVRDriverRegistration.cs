using System.Diagnostics;
using System.Text.Json;

namespace FitOSC.Services.SteamVRDriver;

/// <summary>
/// Registers the FitOSC driver with SteamVR. It stays registered between runs because SteamVR only loads drivers
/// when it starts; the driver does nothing until FitOSC sends it movement.
/// </summary>
public sealed class SteamVRDriverRegistration(ILogger logger)
{
    private static readonly TimeSpan VrPathRegTimeout = TimeSpan.FromSeconds(10);

    // SteamVR keeps a loaded driver's DLL open, so it runs from a copy outside the app folder that updates replace.
    private static readonly string BundledDriverPath = Path.Combine(AppContext.BaseDirectory, "SteamVR", "driver", "fitosc");
    private static readonly string InstalledDriverPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FitOSC", "SteamVRDriver", "fitosc");

    // A build can leave the driver folder behind without the DLL, so the DLL is what marks the driver as bundled.
    public static bool IsBundled => File.Exists(Path.Combine(BundledDriverPath, "bin", "win64", "driver_fitosc.dll"));

    /// <summary>
    /// Copies the bundled driver into place and registers it, or unregisters it when this build doesn't ship one.
    /// Returns whether the driver is registered afterwards.
    /// </summary>
    public bool Register()
    {
        if (!IsBundled)
        {
            // Don't leave SteamVR loading a driver this build no longer ships.
            Unregister();
            return false;
        }
        // Copy even when already registered, so an update reaches SteamVR on its next start.
        CopyDriver();
        return IsRegistered() || RunVrPathReg("adddriver");
    }

    public void Unregister()
    {
        if (IsRegistered())
            RunVrPathReg("removedriver");
    }

    private void CopyDriver()
    {
        foreach (var source in Directory.EnumerateFiles(BundledDriverPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(InstalledDriverPath, Path.GetRelativePath(BundledDriverPath, source));
            var targetInfo = new FileInfo(target);
            var sourceInfo = new FileInfo(source);
            if (targetInfo.Exists && targetInfo.Length == sourceInfo.Length && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
                continue;

            try
            {
                Directory.CreateDirectory(targetInfo.DirectoryName!);
                File.Copy(source, target, overwrite: true);
            }
            catch (IOException ex)
            {
                // SteamVR has the previous driver loaded; the new one is copied on a later start.
                logger.LogWarning("Could not update {File} while SteamVR is using it: {Message}", target, ex.Message);
            }
        }
    }

    private bool RunVrPathReg(string command)
    {
        var vrPathReg = FindVrPathReg();
        if (vrPathReg == null)
        {
            logger.LogWarning("SteamVR not found; cannot {Command} the FitOSC driver", command);
            return false;
        }

        var startInfo = new ProcessStartInfo(vrPathReg) { CreateNoWindow = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(InstalledDriverPath);
        try
        {
            using var process = Process.Start(startInfo)!;
            if (!process.WaitForExit(VrPathRegTimeout))
            {
                logger.LogWarning("vrpathreg {Command} timed out", command);
                return false;
            }
            logger.LogInformation("vrpathreg {Command} {Path} exited with {Code}", command, InstalledDriverPath, process.ExitCode);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "vrpathreg {Command} failed", command);
            return false;
        }
    }

    /// <summary>Finds vrpathreg through the SteamVR runtime listed in openvrpaths.vrpath.</summary>
    private string? FindVrPathReg() =>
        ReadVrPaths("runtime")?
            .Select(runtime => Path.Combine(runtime, "bin", "win64", "vrpathreg.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Whether openvrpaths.vrpath lists the installed driver as an external driver. An unreadable file counts as
    /// registered, so unregistering still runs.
    /// </summary>
    private bool IsRegistered()
    {
        var drivers = ReadVrPaths("external_drivers");
        if (drivers == null)
            return true;

        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstalledDriverPath));
        return drivers.Any(driver => NormalizedPath(driver) is { } path &&
            string.Equals(path, installed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The full path without a trailing separator, or null for an entry that is not a valid path.</summary>
    private static string? NormalizedPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Reads one path list from SteamVR's openvrpaths.vrpath, or null when the file can't be read.</summary>
    private List<string>? ReadVrPaths(string property)
    {
        var pathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        if (!File.Exists(pathsFile))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pathsFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(property, out var paths)
                || paths.ValueKind != JsonValueKind.Array)
                return [];

            return paths.EnumerateArray()
                .Where(path => path.ValueKind == JsonValueKind.String)
                .Select(path => path.GetString()!)
                .Where(path => path.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Could not read {File}: {Message}", pathsFile, ex.Message);
            return null;
        }
    }
}
