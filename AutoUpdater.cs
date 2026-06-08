using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CantoneseDictation;

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public bool IsNewer { get; set; }
}

public class GitHubRelease
{
    public string tag_name { get; set; } = "";
    public string body { get; set; } = "";
    public GitHubAsset[] assets { get; set; } = Array.Empty<GitHubAsset>();
}

public class GitHubAsset
{
    public string name { get; set; } = "";
    public string browser_download_url { get; set; } = "";
}

public static class AutoUpdater
{
    // ─── User config: change these for your repo ───
    public static string RepoOwner = "nkyang10";
    public static string RepoName = "more-than-stt";
    // ───────────────────────────────────────────────

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string CurrentVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        }
    }

    /// <summary>Check GitHub for latest release. Returns null on error.</summary>
    public static async Task<UpdateInfo?> CheckForUpdate()
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("CantoneseDictation-Updater/1.0");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release == null) return null;

            // Parse version: strip leading 'v' if present
            var tag = release.tag_name.TrimStart('v');
            var current = new Version(CurrentVersion);
            var latest = Version.TryParse(tag, out var parsed) ? parsed : new Version(0, 0, 0);

            // Find zip asset — prefer the app zip (not sensevoice_model)
            var zipAsset = Array.Find(release.assets, a =>
                a.name.StartsWith("CantoneseDictation") && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? Array.Find(release.assets, a =>
                    a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo
            {
                Version = release.tag_name,
                DownloadUrl = zipAsset?.browser_download_url ?? "",
                ReleaseNotes = release.body ?? "",
                IsNewer = latest > current
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download update zip, extract, and launch updater batch file.
    /// The batch waits for this process to exit, copies new files, then restarts.
    /// Model file (model_quant.onnx) is excluded from copy — users download separately.
    /// </summary>
    public static async Task<bool> DownloadAndInstall(UpdateInfo info, IWin32Window owner)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            MessageBox.Show(owner, "No zip asset found in the latest release!", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        try
        {
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            var updateDir = Path.Combine(exeDir, ".update");
            if (Directory.Exists(updateDir)) Directory.Delete(updateDir, true);
            Directory.CreateDirectory(updateDir);

            var zipPath = Path.Combine(updateDir, "update.zip");

            AppLogger.Info($"Downloading update from: {info.DownloadUrl}");

            // Download with progress
            using var resp = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1;
            using var contentStream = await resp.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(zipPath);

            var buffer = new byte[81920];
            long readSoFar = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                readSoFar += bytesRead;
            }

            AppLogger.Info($"Downloaded {readSoFar} bytes to {zipPath}");

            // Extract zip
            AppLogger.Info("Extracting update...");
            ZipFile.ExtractToDirectory(zipPath, updateDir, overwriteFiles: true);
            File.Delete(zipPath);

            // Write xcopy exclude file to skip model
            File.WriteAllText(Path.Combine(updateDir, "_exclude.txt"), "model_quant.onnx\r\n");

            // Write updater batch file
            var currentPid = Environment.ProcessId;
            var currentExe = Application.ExecutablePath;
            var updaterPath = Path.Combine(updateDir, "updater.bat");

            var batchContent = GetUpdaterBatchContent(currentPid, currentExe, updateDir, exeDir);
            File.WriteAllText(updaterPath, batchContent);

            AppLogger.Info($"Launching updater: {updaterPath}");

            // Launch updater and exit
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update download failed", ex);
            MessageBox.Show(owner, $"Update failed: {ex.Message}", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static string GetUpdaterBatchContent(int pid, string currentExe,
        string updateDir, string exeDir)
    {
        return $@"@echo off
title CantoneseDictation Updater

:: Wait for old process to exit
echo Waiting for PID {pid} to exit...
:wait
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I ""{pid}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak >NUL
    goto wait
)

echo Old process exited. Copying new files (skipping model_quant.onnx)...
:: Copy everything except the model file (users download it separately)
xcopy /E /Y /EXCLUDE:""{updateDir}\_exclude.txt"" ""{updateDir}\*"" ""{exeDir}\"" >NUL 2>&1

:: Clean up
rmdir /S /Q ""{updateDir}"" 2>NUL

echo Starting updated version...
start /B "" ""{currentExe}""

:: Self-delete with retry
:del_retry
del ""%~f0"" 2>NUL
if exist ""%~f0"" (
    timeout /t 1 /nobreak >NUL
    goto del_retry
)";
    }
}
