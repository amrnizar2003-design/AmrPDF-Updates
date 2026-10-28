using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AmrPDF.Updater;

public static class UpdateManager
{
    public const string CurrentVersion = "1.2.18";
    private const string ManifestUrl = "https://raw.githubusercontent.com/amrnizar2003-design/AmrPDF-Updates/main/stable.json";
    private static int _started;
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amr.PDF");
    private static readonly string StatePath = Path.Combine(DataRoot, "update-state.json");
    private static readonly string BackupRoot = Path.Combine(DataRoot, "Backup", "previous");
    private static readonly string LogPath = Path.Combine(DataRoot, "logs", "updater.log");

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var window = Application.Current.MainWindow;
            if (window is null) return;
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.U && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    e.Handled = true;
                    _ = CheckAsync(manual: true);
                }
            };
            // Buttons belong inside Settings, not in the MainWindow overlay.
            window.LayoutUpdated += (_, _) => TryPlaceSettingsActions(window);
            TryPlaceSettingsActions(window);

        }));
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


    private static bool _settingsActionsAdded;

    private static void TryPlaceSettingsActions(Window window)
    {
        if (_settingsActionsAdded) return;
        DependencyObject? label = FindSettingsAnchor(window);
        if (label is null) return;

        StackPanel? host = null;
        for (DependencyObject? node = VisualTreeHelper.GetParent(label); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is StackPanel stack && stack.Orientation == Orientation.Vertical)
            {
                host = stack;
                break;
            }
        }
        if (host is null) return;

        var controls = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(4, 12, 4, 4),
            FlowDirection = FlowDirection.RightToLeft
        };
        var check = NewSettingsButton("البحث عن تحديثات الآن");
        check.ToolTip = "التحقق فورًا من وجود تحديث (Ctrl + Shift + U)";
        check.Click += (_, _) => { _ = CheckAsync(manual: true); };

        var rollback = NewSettingsButton("العودة للنسخة السابقة");
        rollback.ToolTip = "استعادة النسخة الاحتياطية التي سبقت آخر تحديث";
        rollback.Click += (_, _) =>
        {
            if (!Directory.Exists(BackupRoot))
            {
                Show("لا توجد نسخة احتياطية سابقة على هذا الجهاز.", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (Show("هل تريد العودة للنسخة السابقة؟ سيتم إغلاق Amr.PDF وإعادة تشغيله بعد الاستعادة.",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                RestorePreviousVersion();
        };
        controls.Children.Add(check);
        controls.Children.Add(rollback);

        // Add after the settings options in the existing vertical flow.
        host.Children.Add(controls);
        _settingsActionsAdded = true;
    }

    private static DependencyObject? FindSettingsAnchor(DependencyObject root)
    {
        if (root is TextBlock text && text.Text.Contains("يعمل في الخلفية", StringComparison.Ordinal))
            return root;
        if (root is ContentControl control && control.Content is string caption &&
            caption.Contains("يعمل في الخلفية", StringComparison.Ordinal))
            return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var match = FindSettingsAnchor(VisualTreeHelper.GetChild(root, i));
            if (match is not null) return match;
        }
        return null;
    }

    private static Button NewSettingsButton(string caption)
    {
        return new Button
        {
            Content = caption,
            MinHeight = 38,
            Margin = new Thickness(0, 5, 0, 5),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(91, 63, 111)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(151, 108, 184)),
            BorderThickness = new Thickness(1)
        };
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
            await using var hashStream = File.OpenRead(zipPath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
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
        // Backup only overwritten files; retain other installed files untouched.
        string body = "$ErrorActionPreference='Stop'\n" +
                      $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\n" +
                      $"$backup='{Ps(BackupRoot)}'; $install='{Ps(install)}'; $stage='{Ps(stage)}'\n" +
                      "$pending=$backup+'.pending'; if(Test-Path -LiteralPath $pending){Remove-Item -LiteralPath $pending -Recurse -Force}\n" +
                      "New-Item -ItemType Directory -Path $pending -Force | Out-Null\n" +
                      "Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {\n" +
                      "  $relative=$_.FullName.Substring($stage.Length).TrimStart('\\'); $current=Join-Path $install $relative\n" +
                      "  if(Test-Path -LiteralPath $current){ $saved=Join-Path $pending $relative; New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null; Copy-Item -LiteralPath $current -Destination $saved -Force }\n" +
                      "}\n" +
                      "if(Test-Path -LiteralPath $backup){Remove-Item -LiteralPath $backup -Recurse -Force}\n" +
                      "Move-Item -LiteralPath $pending -Destination $backup\n" +
                      "try { Get-ChildItem -LiteralPath $stage -Force | Copy-Item -Destination $install -Recurse -Force } catch { Get-ChildItem -LiteralPath $backup -Force | Copy-Item -Destination $install -Recurse -Force; throw }\n" +
                      $"Start-Process -FilePath '{Ps(exe)}'\n";
        LaunchScript(script, body);
    }

    private static void RestorePreviousVersion()
    {
        string install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(install, "Amr.PDF.exe");
        int pid = Environment.ProcessId;
        string script = Path.Combine(Path.GetTempPath(), "AmrPDF-Rollback-" + Guid.NewGuid().ToString("N") + ".ps1");
        string body = "$ErrorActionPreference='Stop'\n" +
                      $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\n" +
                      $"$backup='{Ps(BackupRoot)}'; $install='{Ps(install)}'\n" +
                      "if(!(Test-Path -LiteralPath $backup)){throw 'Previous version backup not found'}\n" +
                      "Get-ChildItem -LiteralPath $backup -Force | Copy-Item -Destination $install -Recurse -Force\n" +
                      "Remove-Item -LiteralPath $backup -Recurse -Force\n" +
                      $"Start-Process -FilePath '{Ps(exe)}'\n";
        LaunchScript(script, body);
    }

    private static void LaunchScript(string script, string body)
    {
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

