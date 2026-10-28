using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace AmrPDF.Updater;

public static class UpdateManager
{
    public const string CurrentVersion = "1.2.18";
    private const string ManifestUrl = "https://raw.githubusercontent.com/amrnizar2003-design/AmrPDF-Updates/main/stable.json";
    private static int _started;
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amr.PDF");
    private static readonly string StatePath = Path.Combine(DataRoot, "update-state.json");
    private static readonly string LogPath = Path.Combine(DataRoot, "logs", "updater.log");

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (ShouldCheck()) await CheckAsync();
            }
            catch (Exception ex) { Log("startup: " + ex); }
        });
    }

    public static async Task CheckAsync(bool manual = false)
    {
        try
        {
            MarkChecked();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Amr.PDF-Updater/1.2.18");
            string json = await http.GetStringAsync(ManifestUrl);
            var manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || !Version.TryParse(manifest.Version, out var remote) || remote <= Version.Parse(CurrentVersion))
            {
                if (manual) Show("لديك أحدث إصدار من Amr.PDF.", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!IsApprovedUrl(manifest.PackageUrl) || manifest.Sha256.Length != 64 || manifest.SizeBytes <= 0)
                throw new InvalidDataException("بيانات التحديث غير مكتملة.");

            var answer = Show($"يتوفر تحديث جديد لـ Amr.PDF\n\nالإصدار الحالي: {CurrentVersion}\nالإصدار الجديد: {manifest.Version}\n\n{manifest.NotesAr}\n\nهل تريد تنزيله وتثبيته الآن؟",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            string updateRoot = Path.Combine(DataRoot, "Updates", manifest.Version);
            Directory.CreateDirectory(updateRoot);
            string zipPath = Path.Combine(updateRoot, "update.zip.download");
            await DownloadAsync(http, manifest.PackageUrl, zipPath, manifest.SizeBytes);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(zipPath))).ToLowerInvariant();
            if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("بصمة حزمة التحديث غير مطابقة.");

            string stage = Path.Combine(updateRoot, "staged");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            ExtractSafely(zipPath, stage);
            if (!File.Exists(Path.Combine(stage, "Amr.PDF.dll")))
                throw new InvalidDataException("حزمة التحديث لا تحتوي ملفات التشغيل المطلوبة.");

            var install = Show("اكتمل تنزيل التحديث والتحقق منه. سيُغلق Amr.PDF لثوانٍ ثم يعاد تشغيله.\n\nهل تريد المتابعة؟",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (install != MessageBoxResult.Yes) return;
            ApplyAfterExit(stage);
        }
        catch (Exception ex)
        {
            Log("check: " + ex);
            if (manual) Show("تعذر التحقق من التحديثات الآن. سيستمر التطبيق بالعمل بصورة طبيعية.", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool ShouldCheck()
    {
        try
        {
            if (!File.Exists(StatePath)) return true;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
            return state is null || DateTimeOffset.UtcNow - state.LastCheckUtc >= TimeSpan.FromHours(12);
        }
        catch { return true; }
    }

    private static void MarkChecked()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(DateTimeOffset.UtcNow)), Encoding.UTF8);
        }
        catch { }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination, long expectedSize)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("حجم حزمة التحديث غير مطابق.");
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await source.CopyToAsync(output);
        await output.FlushAsync();
        if (new FileInfo(destination).Length != expectedSize) throw new InvalidDataException("لم يكتمل تنزيل التحديث.");
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("مسار غير آمن داخل التحديث.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static void ApplyAfterExit(string stage)
    {
        string install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(install, "Amr.PDF.exe");
        int pid = Environment.ProcessId;
        string script = Path.Combine(Path.GetTempPath(), "AmrPDF-Update-" + Guid.NewGuid().ToString("N") + ".ps1");
        string body = $"$ErrorActionPreference='Stop'\n" +
                      $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\n" +
                      $"Copy-Item -LiteralPath '{Ps(stage)}\\*' -Destination '{Ps(install)}' -Recurse -Force\n" +
                      $"Start-Process -FilePath '{Ps(exe)}'\n" +
                      $"Remove-Item -LiteralPath '{Ps(stage)}' -Recurse -Force -ErrorAction SilentlyContinue\n" +
                      "$me=$MyInvocation.MyCommand.Path; Start-Sleep -Milliseconds 300; Remove-Item -LiteralPath $me -Force -ErrorAction SilentlyContinue\n";
        File.WriteAllText(script, body, new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static bool IsApprovedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string Ps(string value) => value.Replace("'", "''");
    private static MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var app = Application.Current;
        if (app is null) return MessageBoxResult.None;
        return app.Dispatcher.Invoke(() => MessageBox.Show(app.MainWindow, text, "تحديث Amr.PDF", buttons, icon, MessageBoxResult.None, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign));
    }
    private static void Log(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}\n", Encoding.UTF8); } catch { }
    }

    private sealed record Manifest(string Version, string PackageUrl, string Sha256, long SizeBytes, bool Mandatory, string NotesAr);
    private sealed record State(DateTimeOffset LastCheckUtc);
}

