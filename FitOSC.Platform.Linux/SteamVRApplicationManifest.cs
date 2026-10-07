using System.Text.Json;
using System.Text.Json.Nodes;
using FitOSC.Services.Configuration;

namespace FitOSC.Platform.Linux;

public static class SteamVRApplicationManifest
{
    public static string ManifestPath => Path.Combine(
        Path.GetDirectoryName(ConfigurationService.ConfigFilePath)!, "fitosc.vrmanifest");

    public static void EnsureCreated()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var launcher = string.IsNullOrEmpty(appImage) ? Environment.ProcessPath : appImage;
        if (string.IsNullOrEmpty(launcher) || !Path.IsPathFullyQualified(launcher) || !File.Exists(launcher))
            throw new InvalidOperationException("SteamVR requires an existing absolute FitOSC launcher path.");

        launcher = Path.GetFullPath(launcher);
        if (launcher.StartsWith("/tmp/.mount", StringComparison.Ordinal))
            throw new InvalidOperationException("SteamVR cannot register a temporary AppImage mount; APPIMAGE must identify the persistent launcher.");

        var templatePath = Path.Combine(AppContext.BaseDirectory, "SteamVR", "fitosc.vrmanifest");
        var manifest = JsonNode.Parse(File.ReadAllText(templatePath))!.AsObject();
        var application = manifest["applications"]!.AsArray()[0]!.AsObject();
        application.Remove("binary_path_windows");
        application.Remove("action_manifest_path");
        application["binary_path_linux"] = launcher;

        var path = ManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
