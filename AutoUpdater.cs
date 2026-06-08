using System;
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

    /// <summary>Callback to set status text on the UI.</summary>
    public static Action<string>? SetStatusMsg { get; set; }

    /// <summary>Path to a marker file that signals a pending update.</summary>
    private static string PendingMarkerPath
    {
        get
        {
            var dir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            return Path.Combine(dir, ".update", "pending.txt");
        }
    }

    /// <summary>True if an update was downloaded and is ready to apply.</summary>
    public static bool IsUpdatePending => File.Exists(PendingMarkerPath);

    public static string CurrentVersion
    {
        get
        {
            // Read version from version.txt shipped alongside the exe
            try
            {
                var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
                var versionFile = Path.Combine(exeDir, "version.txt");
                if (File.Exists(versionFile))
                {
                    var v = File.ReadAllText(versionFile).Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            // Fallback: assembly version
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null) return $"{ver.Major}.{ver.Minor}.{ver.Build}";
            }
            catch { }
            return "0.0.0";
        }
    }

    /// <summary>
    /// Apply a pending update: launch the updater batch and exit.
    /// Call this when the user clicks "Restart now".
    /// </summary>
    public static void ApplyPendingUpdate()
    {
        var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
        var baseDir = Path.GetFullPath(exeDir);
        var updateDir = Path.Combine(baseDir, ".update");
        var updaterPath = Path.Combine(updateDir, "updater.bat");

        if (!File.Exists(updaterPath))
        {
            MessageBox.Show("Update files not found. Please check for updates again.",
                "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AppLogger.Info("Applying pending update...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" /min \"{updaterPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to start updater", ex);
            MessageBox.Show($"Failed to start updater: {ex.Message}", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        // Force immediate termination so the batch can replace the exe
        Environment.Exit(0);
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

            // Version comparison
            bool isNewer = false;
            string displayVersion = release.tag_name;

            if (UseBetaChannel)
            {
                isNewer = true;
                displayVersion = $"CI Build ({release.created_at})";
                AppLogger.Info($"Beta channel: always showing update (tag=ci)");
            }
            else
            {
                var tag = release.tag_name.TrimStart('v');
                var currentVer = CurrentVersion.TrimStart('v');
                var current = Version.TryParse(currentVer, out var curParsed) ? curParsed : new Version(0, 0, 0);
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
    /// Download update zip, extract, copy non-exe files, and save pending state.
    /// User is prompted to restart; they can choose Later.
    /// </summary>
    public static async Task<bool> DownloadAndInstall(UpdateInfo info, IWin32Window owner)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            AppLogger.Warn("DownloadAndInstall: no download URL");
            MessageBox.Show(owner, "No download URL found in the latest release!", "Update Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        AppLogger.Info($"DownloadAndInstall: version={info.Version}, url={info.DownloadUrl}");

        try
        {
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            var baseDir = Path.GetFullPath(exeDir);
            var updateDir = Path.Combine(baseDir, ".update");
            AppLogger.Info($"Update dir: {updateDir}");

            // Fresh start
            if (Directory.Exists(updateDir))
            {
                try { Directory.Delete(updateDir, true); }
                catch { AppLogger.Warn("Could not delete old update dir"); }
            }
            Directory.CreateDirectory(updateDir);

            var zipPath = Path.Combine(updateDir, "update.zip");
            AppLogger.Info($"Downloading to: {zipPath}");

            // Download
            using var resp = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            AppLogger.Info($"Download response OK, size={resp.Content.Headers.ContentLength ?? -1}");

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
            AppLogger.Info($"Downloaded {readSoFar} bytes");

            // Extract zip
            AppLogger.Info("Extracting update zip...");
            int extractedCount = 0;
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
                        AppLogger.Warn($"Skipping path traversal: {entryName} -> {destPath}");
                        continue;
                    }
                    if (entry.Name.Length == 0) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                    extractedCount++;
                }
            }
            AppLogger.Info($"Extracted {extractedCount} files");

            // Delete zip
            await RetryDeleteAsync(zipPath);

            // Copy non-exe files
            var allowedFiles = new[] { "CantoneseDictation.exe", "version.txt" };
            foreach (var f in allowedFiles)
            {
                if (f == "CantoneseDictation.exe") continue;
                var src = Path.Combine(updateDir, f);
                if (File.Exists(src))
                {
                    var dst = Path.Combine(baseDir, f);
                    File.Copy(src, dst, overwrite: true);
                    AppLogger.Info($"Updated: {f}");
                }
                else
                {
                    AppLogger.Warn($"File not found in update: {f}");
                }
            }

            // Update model folder if present
            var updateModelDir = Path.Combine(updateDir, SenseVoiceEngine.ModelDirName);
            if (Directory.Exists(updateModelDir))
            {
                AppLogger.Info("Updating model folder...");
                var targetModelDir = Path.Combine(baseDir, SenseVoiceEngine.ModelDirName);
                Directory.CreateDirectory(targetModelDir);
                foreach (var srcFile in Directory.GetFiles(updateModelDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(updateModelDir, srcFile);
                    var dst = Path.Combine(targetModelDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(srcFile, dst, overwrite: true);
                }
                AppLogger.Info("Model folder updated");
            }

            // Write updater batch
            var currentPid = Environment.ProcessId;
            var currentExe = Application.ExecutablePath;
            var updaterPath = Path.Combine(updateDir, "updater.bat");
            var batchContent = GetUpdaterBatchContent(currentPid, currentExe, updateDir, exeDir);
            File.WriteAllText(updaterPath, batchContent);
            AppLogger.Info($"Updater batch written: {updaterPath}");

            // Save pending marker
            File.WriteAllText(PendingMarkerPath, info.Version);
            AppLogger.Info($"Pending marker saved: {PendingMarkerPath}");

            AppLogger.Info("Update ready — prompting user to restart");
            SetStatusMsg?.Invoke("Update ready");

            // Ask user to restart (show as task dialog to ensure it's on top)
            var msg = $"Update {info.Version} has been downloaded!\n\nRestart to apply the update?";
            var restart = MessageBox.Show(owner, msg, "Update Ready",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            AppLogger.Info($"User response: {(restart == DialogResult.Yes ? "Restart now" : "Later")}");

            if (restart == DialogResult.Yes)
            {
                ApplyPendingUpdate();
            }
            else
            {
                SetStatusMsg?.Invoke("Update ready — click Update to restart");
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("DownloadAndInstall failed", ex);
            MessageBox.Show(owner, $"Update failed: {ex.Message}\n\nCheck the log file for details.",
                "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

start "" ""{currentExe}""

:: Self-delete
(goto) 2>&1 & del ""%~f0""
";
    }
}
