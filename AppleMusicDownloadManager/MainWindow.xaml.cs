using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AppleMusicDownloadManager
{
    // JSON 结构，用于生成 album.json
    public record TrackRecord(string Name, int TrackNumber);

    public record AlbumTask(string album_url, string album_name, List<TrackRecord> tracks);

    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _decryptorProcess;
        private Process? _downloaderProcess;
        private string? _baseDirectory; // AMDL-WSL1 路径
        private string? _dbBaseDirectory; // AMData 路径
        private int _totalAlbumsDownloadedThisSession;

        public MainWindow()
        {
            InitializeComponent();
            CheckEnvironmentVariable();
        }

        private void CheckEnvironmentVariable()
        {
            _baseDirectory = Environment.GetEnvironmentVariable("AMDL-WSL1");
            _dbBaseDirectory = Environment.GetEnvironmentVariable("AMData");

            if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(_dbBaseDirectory))
            {
                MessageBox.Show("错误：'AMDL-WSL1' 或 'AMData' 环境变量未设置。", "环境配置错误", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                InfoPathTextBlock.Text = "错误：环境变量配置不完整！";
                return;
            }

            if (Directory.Exists(Path.Combine(_baseDirectory, "wsl1")) &&
                Directory.Exists(Path.Combine(_baseDirectory, "apple-music-downloader")))
            {
                StartButton.IsEnabled = true;
                InfoPathTextBlock.Text = $"工作目录: {_baseDirectory} | DB: {_dbBaseDirectory}";
                UpdateStatus("环境变量加载成功。");
            }
            else
            {
                MessageBox.Show($"错误：'AMDL-WSL1' 指向的路径 '{_baseDirectory}' 无效或不完整。", "路径无效", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StartButton.IsEnabled = false;
                InfoPathTextBlock.Text = $"路径无效: {_baseDirectory}";
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            KillAllProcesses();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StartButton.Content = "处理中...";
            ClearDownloaderLogs();
            ClearDecryptorLogs();
            _totalAlbumsDownloadedThisSession = 0;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await RunWorkflow(_cancellationTokenSource.Token);
                UpdateStatus("所有任务已处理完毕或已达到上限。");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("处理过程被用户取消。");
            }
            catch (Exception ex)
            {
                UpdateStatus($"发生严重错误: {ex.Message}");
                MessageBox.Show(ex.ToString(), "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                StartButton.Content = "开始处理";
                KillAllProcesses();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                LogToDecryptor("用户请求终止操作...");
                LogToDownloader("用户请求终止操作...");
                _cancellationTokenSource.Cancel();
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(_dbBaseDirectory)) return;

            string metadataDbPath = Path.Combine(_dbBaseDirectory, "am_metadata.sqlite");
            string progressDbPath = Path.Combine(_dbBaseDirectory, "download_progress.db");
            InitializeProgressDatabase(progressDbPath);

            // 启动并监控 Decryptor 进程
            if (!await EnsureDecryptorIsRunning(token))
            {
                LogToDecryptor("Decryptor 启动失败，工作流无法继续。", Brushes.Red);
                return;
            }

            var allArtistIds = GetAllArtistIds(metadataDbPath);
            if (allArtistIds == null || !allArtistIds.Any())
            {
                LogToDownloader("未在 am_metadata.sqlite 中找到任何艺术家。", Brushes.Orange);
                return;
            }

            UpdateStatus($"发现 {allArtistIds.Count} 位艺术家。");
            LogToDownloader($"[调度中心] 开始处理 {allArtistIds.Count} 位艺术家的专辑下载任务。", Brushes.Cyan);

            foreach (var artistId in allArtistIds)
            {
                if (token.IsCancellationRequested) break;

                var albums = GetAlbumsForArtist(metadataDbPath, artistId);
                if (!albums.Any()) continue;

                string artistName = albums.First().ArtistName;
                LogToDownloader($"[艺术家] 开始处理 '{artistName}' (ID: {artistId})，共 {albums.Count} 张专辑。",
                    Brushes.LightGreen);

                foreach (var album in albums)
                {
                    if (token.IsCancellationRequested) break;

                    if (IsAlbumProcessed(progressDbPath, album.Id))
                    {
                        LogToDownloader($"  ->[跳过] '{album.Name}' (ID: {album.Id}) 已处理过。", Brushes.Gray);
                        continue;
                    }

                    ClearDownloaderLogs();
                    UpdateStatus($"正在处理专辑: {album.Name}");
                    LogToDownloader($"[任务开始] 正在下载专辑 '{album.Name}' (URL: {album.Url})");

                    // 1. 创建 album.json
                    await CreateAlbumJson(album);

                    // 2. 运行下载器
                    bool success = await RunAndMonitorDownloader(token);

                    // 3. 清理和记录
                    CleanupAlbumJson();

                    if (success)
                    {
                        MarkAlbumAsProcessed(progressDbPath, album.Id);
                        _totalAlbumsDownloadedThisSession++;
                        LogToDownloader($"[成功] '{album.Name}' 下载完成。本轮已下载 {_totalAlbumsDownloadedThisSession} 张专辑。",
                            Brushes.Green);
                    }
                    else
                    {
                        LogToDownloader($"[失败] '{album.Name}' 下载失败。请检查 Downloader 日志。", Brushes.Red);
                        // 可选择：如果失败，是否重试或终止？当前策略是继续下一个
                    }

                    // 4. 检查会话上限
                    if (_totalAlbumsDownloadedThisSession >= 500)
                    {
                        LogToDownloader("[会话停止] 累计下载专辑数已达到500张上限！", Brushes.Red);
                        UpdateStatus("专辑总数达到500上限，会话已停止。");
                        _cancellationTokenSource?.Cancel();
                        break;
                    }

                    await Task.Delay(5000, token); // 每张专辑之间等待5秒
                }
            }
        }

        #region Process Management

        private async Task<bool> EnsureDecryptorIsRunning(CancellationToken token)
        {
            if (_decryptorProcess != null && !_decryptorProcess.HasExited) return true;

            LogToDecryptor("Decryptor 未运行，正在启动...");
            var decryptorReadyTcs = new TaskCompletionSource<bool>();

            var batFile = Directory.GetFiles(_baseDirectory!, "1. Run decryptor*.bat").FirstOrDefault();
            if (batFile == null)
            {
                MessageBox.Show("错误: 找不到 '1. Run decryptor...' 启动脚本。", "文件未找到", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = batFile,
                WorkingDirectory = _baseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            _decryptorProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _decryptorProcess.Exited += (s, e) =>
            {
                Dispatcher.BeginInvoke(() => LogToDecryptor("Decryptor 意外退出！会话将终止。", Brushes.Red));
                _cancellationTokenSource?.Cancel(); // 关键依赖退出，终止整个工作流
                decryptorReadyTcs.TrySetResult(false);
            };

            void HandleOutput(string data)
            {
                LogToDecryptor(data);
                // 假设 Decryptor 启动后输出特定内容表示就绪
                if (data.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                    data.Contains("ready", StringComparison.OrdinalIgnoreCase))
                {
                    decryptorReadyTcs.TrySetResult(true);
                }

                if (data.ToUpper().Contains("ERROR")) // 左边异常，重启
                {
                    LogToDecryptor("检测到错误，将重启会话...", Brushes.OrangeRed);
                    _cancellationTokenSource?.Cancel();
                }
            }

            _decryptorProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) HandleOutput(e.Data);
            };
            _decryptorProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) HandleOutput(e.Data);
            };

            _decryptorProcess.Start();
            _decryptorProcess.BeginOutputReadLine();
            _decryptorProcess.BeginErrorReadLine();
            LogToDecryptor($"Decryptor 已启动 (PID: {_decryptorProcess.Id}). 等待就绪...");

            // 等待就绪信号，或超时
            var completedTask = await Task.WhenAny(decryptorReadyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30), token));
            if (completedTask != decryptorReadyTcs.Task || !decryptorReadyTcs.Task.Result)
            {
                LogToDecryptor("Decryptor 未能在30秒内进入就绪状态。", Brushes.Red);
                KillProcess(_decryptorProcess);
                return false;
            }

            LogToDecryptor("Decryptor 已就绪。", Brushes.Green);
            return true;
        }

        private async Task<bool> RunAndMonitorDownloader(CancellationToken token)
        {
            var batFile = Path.Combine(_baseDirectory!, "2. Run downloader.bat");
            if (!File.Exists(batFile))
            {
                MessageBox.Show("错误: 找不到 '2. Run downloader.bat' 启动脚本。", "文件未找到", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = batFile,
                WorkingDirectory = _baseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            _downloaderProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputTcs = new TaskCompletionSource<bool>();

            void HandleOutput(string data)
            {
                LogToDownloader(data);
                if (data.Contains("Exit Finished!"))
                {
                    outputTcs.TrySetResult(true);
                }
                else if (data.ToUpper().Contains("[FAILURE]") || data.ToUpper().Contains("CRITICAL ERROR"))
                {
                    outputTcs.TrySetResult(false);
                }
            }

            _downloaderProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) HandleOutput(e.Data);
            };
            _downloaderProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) HandleOutput(e.Data);
            };

            _downloaderProcess.Start();
            _downloaderProcess.BeginOutputReadLine();
            _downloaderProcess.BeginErrorReadLine();
            LogToDownloader($"Downloader 进程已启动 (PID: {_downloaderProcess.Id}).");

            Task processExitTask = _downloaderProcess.WaitForExitAsync(token);
            Task completedTask = await Task.WhenAny(outputTcs.Task, processExitTask);

            bool success;
            if (completedTask == outputTcs.Task)
            {
                success = await outputTcs.Task;
            }
            else
            {
                // 进程退出了，但没有收到明确的成功/失败信号
                success = _downloaderProcess.ExitCode == 0;
                LogToDownloader($"Downloader 进程已退出，退出码: {_downloaderProcess.ExitCode}。判定结果: {(success ? "成功" : "失败")}",
                    success ? Brushes.Gray : Brushes.Red);
            }

            KillProcess(_downloaderProcess); // 确保进程被清理
            return success;
        }

        private void KillProcess(Process? process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (Exception)
            {
                /* Ignore */
            }
        }

        private void KillAllProcesses()
        {
            KillProcess(_downloaderProcess);
            _downloaderProcess = null;
            KillProcess(_decryptorProcess);
            _decryptorProcess = null;
        }

        #endregion

        #region File I/O

        private async Task CreateAlbumJson(AlbumInfo album)
        {
            var albumTask = new AlbumTask(album.Url, album.Name, album.Tracks);
            string jsonContent =
                JsonSerializer.Serialize(albumTask, new JsonSerializerOptions { WriteIndented = true });
            string jsonPath = Path.Combine(_baseDirectory!, "album.json");
            await File.WriteAllTextAsync(jsonPath, jsonContent);
            LogToDownloader($"  -> 已创建任务文件: {jsonPath}", Brushes.DarkGray);
        }

        private void CleanupAlbumJson()
        {
            string jsonPath = Path.Combine(_baseDirectory!, "album.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    File.Delete(jsonPath);
                    LogToDownloader("  -> 已清理任务文件。", Brushes.DarkGray);
                }
                catch (Exception ex)
                {
                    LogToDownloader($"  -> 清理任务文件失败: {ex.Message}", Brushes.Red);
                }
            }
        }

        #endregion

        #region Database Helpers

        public record AlbumInfo(string Id, string Name, string Url, string ArtistName, List<TrackRecord> Tracks);

        private void InitializeProgressDatabase(string dbPath)
        {
            using var con = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            con.Open();
            using var cmd =
                new SQLiteCommand(
                    "CREATE TABLE IF NOT EXISTS processed_albums (album_id TEXT PRIMARY KEY, processed_at TEXT)", con);
            cmd.ExecuteNonQuery();
        }

        private List<string>? GetAllArtistIds(string metadataDbPath)
        {
            if (!File.Exists(metadataDbPath)) return null;
            var ids = new List<string>();
            try
            {
                using var con = new SQLiteConnection($"Data Source={metadataDbPath};Version=3;");
                con.Open();
                using var cmd = new SQLiteCommand("SELECT DISTINCT id FROM artists", con);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }
            catch (Exception ex)
            {
                LogToDownloader($"读取艺术家列表失败: {ex.Message}", Brushes.Red);
                return null;
            }

            return ids;
        }

        private List<AlbumInfo> GetAlbumsForArtist(string metadataDbPath, string artistId)
        {
            var albums = new List<AlbumInfo>();
            try
            {
                using var con = new SQLiteConnection($"Data Source={metadataDbPath};Version=3;");
                con.Open();

                // SQL to get all albums for a given artist
                string sql = @"
                    SELECT a.id, a.name, a.url, ar.name 
                    FROM albums a
                    JOIN album_artists aa ON a.id = aa.album_id
                    JOIN artists ar ON aa.artist_id = ar.id
                    WHERE aa.artist_id = @artistId";

                using var cmd = new SQLiteCommand(sql, con);
                cmd.Parameters.AddWithValue("@artistId", artistId);
                using var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var albumId = reader.GetString(0);
                    var albumName = reader.GetString(1);
                    var albumUrl = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var artistName = reader.GetString(3);

                    if (string.IsNullOrEmpty(albumUrl)) continue;

                    // For each album, get its tracks
                    var tracks = new List<TrackRecord>();
                    using var trackCmd =
                        new SQLiteCommand(
                            "SELECT name, track_number FROM tracks WHERE album_id = @albumId ORDER BY track_number",
                            con);
                    trackCmd.Parameters.AddWithValue("@albumId", albumId);
                    using var trackReader = trackCmd.ExecuteReader();
                    while (trackReader.Read())
                    {
                        tracks.Add(new TrackRecord(
                            trackReader.GetString(0),
                            trackReader.GetInt32(1)
                        ));
                    }

                    albums.Add(new AlbumInfo(albumId, albumName, albumUrl, artistName, tracks));
                }
            }
            catch (Exception ex)
            {
                LogToDownloader($"获取艺术家 '{artistId}' 的专辑失败: {ex.Message}", Brushes.Red);
            }

            return albums;
        }

        private bool IsAlbumProcessed(string progressDbPath, string albumId)
        {
            try
            {
                using var con = new SQLiteConnection($"Data Source={progressDbPath};Version=3;");
                con.Open();
                using var cmd = new SQLiteCommand("SELECT 1 FROM processed_albums WHERE album_id = @id", con);
                cmd.Parameters.AddWithValue("@id", albumId);
                return cmd.ExecuteScalar() != null;
            }
            catch
            {
                return false;
            }
        }

        private void MarkAlbumAsProcessed(string progressDbPath, string albumId)
        {
            try
            {
                using var con = new SQLiteConnection($"Data Source={progressDbPath};Version=3;");
                con.Open();
                using var cmd =
                    new SQLiteCommand(
                        "INSERT OR IGNORE INTO processed_albums (album_id, processed_at) VALUES (@id, @time)", con);
                cmd.Parameters.AddWithValue("@id", albumId);
                cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("s"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                LogToDownloader($"标记专辑 '{albumId}' 为已处理时失败: {ex.Message}", Brushes.Red);
            }
        }

        #endregion

        #region UI Helpers

        private void LogTo(RichTextBox rtb, string message, Brush? color = null)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var paragraph = new Paragraph(new Run(message))
                        { Margin = new Thickness(0), Foreground = color ?? SystemColors.WindowTextBrush };
                    rtb.Document.Blocks.Add(paragraph);
                    rtb.ScrollToEnd();
                });
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void LogToDecryptor(string message, Brush? color = null) => LogTo(DecryptorLogRtb, message, color);
        private void LogToDownloader(string message, Brush? color = null) => LogTo(DownloaderLogRtb, message, color);

        private void ClearLogs(RichTextBox rtb)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() => rtb.Document.Blocks.Clear());
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void ClearDecryptorLogs() => ClearLogs(DecryptorLogRtb);
        private void ClearDownloaderLogs() => ClearLogs(DownloaderLogRtb);

        private void UpdateStatus(string message)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() => { StatusTextBlock.Text = message; });
            }
            catch (TaskCanceledException)
            {
            }
        }

        #endregion
    }
}