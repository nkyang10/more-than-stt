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
    public string created_at { get; set; } = "";
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

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool UseBetaChannel { get; set; } = false;

    public static string CurrentVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }
    }

    /// <summary>Check GitHub for latest release. Returns null on error.</summary>
    public static async Task<UpdateInfo?> CheckForUpdate()
    {
        try
        {
            var url = UseBetaChannel
                ? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/ci"
                : $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

            AppLogger.Info($"Update check: {(UseBetaChannel ? "BETA (tag=ci)" : "STABLE (latest)")}");
            AppLogger.Info($"URL: {url}");

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("CantoneseDictation-Updater/1.0");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release == null)
            {
                AppLogger.Warn("Update check: failed to deserialize release JSON");
                return null;
            }

            AppLogger.Info($"Release found: tag={release.tag_name}, assets={release.assets.Length}");

            // Find zip asset — prefer the app zip (not sensevoice_model)
            var zipAsset = Array.Find(release.assets, a =>
                (a.name.StartsWith("CantoneseDictation_v") || a.name.StartsWith("CantoneseDictation_beta"))
                && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? Array.Find(release.assets, a =>
                    a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (zipAsset == null)
            {
                AppLogger.Warn("Update check: no zip asset found in release");
                return null;
            }

            AppLogger.Info($"Update asset: {zipAsset.name} ({zipAsset.browser_download_url})");

            // ─── Version comparison ───
            bool isNewer = false;
            string displayVersion = release.tag_name;

            if (UseBetaChannel)
            {
                // Beta channel: always consider CI build newer if we found assets
                isNewer = true;
                displayVersion = $"CI Build ({release.created_at})";
                AppLogger.Info($"Beta channel: always showing update (tag=ci, created={release.created_at})");
            }
            else
            {
                // Stable channel: compare semver
                var tag = release.tag_name.TrimStart('v');
                var current = new Version(CurrentVersion);
                var latest = Version.TryParse(tag, out var parsed) ? parsed : new Version(0, 0, 0);
                isNewer = latest > current;
                AppLogger.Info($"Version compare: current={CurrentVersion} latest={tag} => isNewer={isNewer}");
            }

            return new UpdateInfo
            {
                Version = displayVersion,
                DownloadUrl = zipAsset.browser_download_url,
                ReleaseNotes = release.body ?? "",
                IsNewer = isNewer
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update check failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Download update zip, extract, and launch updater batch file.
    /// </summary>
    public static async Task<bool> DownloadAndInstall(UpdateInfo info, IWin32Window owner)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            MessageBox.Show(owner, "No download URL found in the latest release!", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        try
        {
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            // SAFETY: All file ops must stay inside exeDir
            var baseDir = Path.GetFullPath(exeDir);
            var updateDir = Path.Combine(baseDir, ".update");
            if (Directory.Exists(updateDir))
            {
                try { Directory.Delete(updateDir, true); }
                catch { }
            }
            Directory.CreateDirectory(updateDir);

            var zipPath = Path.Combine(updateDir, "update.zip");

            AppLogger.Info($"Downloading update from: {info.DownloadUrl}");

            // Download
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

            // Extract zip — SAFETY: reject any entry that escapes updateDir
            AppLogger.Info("Extracting update...");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var entryName = entry.FullName.Replace('/', '\\');
                    if (entryName.Contains("..\\") || entryName.StartsWith("\\") || Path.IsPathRooted(entryName))
                    {
                        AppLogger.Warn($"Skipping unsafe zip entry: {entryName}");
                        continue;
                    }
                    var destPath = Path.GetFullPath(Path.Combine(updateDir, entryName));
                    if (!destPath.StartsWith(Path.GetFullPath(updateDir), StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn($"Skipping path traversal attempt: {entryName} -> {destPath}");
                        continue;
                    }
                    if (entry.Name.Length == 0) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            // Retry delete zip (Windows Defender / AV may hold a lock briefly)
            AppLogger.Info("Deleting update.zip...");
            await RetryDeleteAsync(zipPath);

            // SAFETY: Only copy EXPLICIT known filenames to baseDir (no wildcard)
            var allowedFiles = new[] { "CantoneseDictation.exe", "tokens.txt", "am.mvn", "README.txt" };
            // Copy non-exe files now (safe while running)
            foreach (var f in allowedFiles)
            {
                if (f == "CantoneseDictation.exe") continue; // exe must be copied AFTER exit via batch
                var src = Path.Combine(updateDir, f);
                if (File.Exists(src))
                {
                    var dst = Path.Combine(baseDir, f);
                    File.Copy(src, dst, overwrite: true);
                    AppLogger.Info($"Updated: {f}");
                }
            }

            // Write updater batch — ONLY copies the exe (other files already done)

            // Write xcopy exclude file
            File.WriteAllText(Path.Combine(updateDir, "_exclude.txt"), "model_quant.onnx\r\n");

            // Write updater batch
            var currentPid = Environment.ProcessId;
            var currentExe = Application.ExecutablePath;
            var updaterPath = Path.Combine(updateDir, "updater.bat");

            var batchContent = GetUpdaterBatchContent(currentPid, currentExe, updateDir, exeDir);
            File.WriteAllText(updaterPath, batchContent);

            AppLogger.Info($"Launching updater: {updaterPath}");
            AppLogger.Info($"Update dir: {updateDir}");
            AppLogger.Info($"Target exe: {currentExe}");

            // Launch updater (visible window so user can see progress)
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                WindowStyle = ProcessWindowStyle.Normal,
                CreateNoWindow = false,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update download failed", ex);
            MessageBox.Show(owner, $"Update failed: {ex.Message}\n\nCheck the log file for details.", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static async Task RetryDeleteAsync(string path, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Delete(path);
                AppLogger.Info($"Deleted: {path}");
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                AppLogger.Warn($"File locked, retrying in 500ms... ({i + 1}/{maxRetries})");
                await Task.Delay(500);
            }
        }
        // Last attempt - let it throw
        File.Delete(path);
    }

    private static string GetUpdaterBatchContent(int pid, string currentExe,
        string updateDir, string exeDir)
    {
        return $@"
@echo off
title CantoneseDictation Updater
echo ================================
echo CantoneseDictation Updater
echo ================================
echo.
echo Waiting for old process (PID {pid}) to exit...
echo.

:wait
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I ""{pid}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak >NUL
    goto wait
)

echo Old process exited.
echo Copying new executable...
echo.

:: Only copy CantoneseDictation.exe (other files already copied by app)
copy /Y ""{updateDir}\CantoneseDictation.exe"" ""{currentExe}"" >NUL 2>&1

:: Clean up
echo Cleaning up...
rmdir /S /Q ""{updateDir}"" >NUL 2>&1

echo.
echo Starting updated version...
echo.

start """" ""{currentExe}""

:: Self-delete
(goto) 2>&1 & del ""%~f0""
";
    }
}
