using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private string? _failedAlbumStr;
        private readonly object _logFileLock = new object();

        enum FailedType
        {
            NoErr,
            ValidationErr,
            NetworkErr,
            NormalErr
        }

        public MainWindow()
        {
            InitializeComponent();
            CheckEnvironmentVariable();
            _failedAlbumStr = "placeholder this is test";
            _totalAlbumsDownloadedThisSession = 0;
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
            if (_totalAlbumsDownloadedThisSession >= 10)
            {
                LogToDecryptor("[会话停止] 累计下载专辑数已达到10张上限！", Brushes.Red);
                UpdateStatus("专辑总数达到10上限，会话已停止。");
                return;
            }

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StartButton.Content = "处理中...";
            ClearDownloaderLogs();
            ClearDecryptorLogs();
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
                KillAllProcesses();
                _cancellationTokenSource.Cancel();
            }
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            if (string.IsNullOrEmpty(_baseDirectory) || string.IsNullOrEmpty(_dbBaseDirectory)) return;

            string metadataDbPath = Path.Combine(_dbBaseDirectory, "am_metadata.sqlite");
            string progressDbPath = Path.Combine(_dbBaseDirectory, "download_progress.db");
            string failedDbPath = Path.Combine(_dbBaseDirectory, "failed_albums.db");
            InitializeProgressDatabase(progressDbPath);
            InitializeFailedAlbumsDatabase(failedDbPath);
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

                    if (IsAlbumFailed(failedDbPath, album.Id) &&
                        GetAlbumFailedReason(failedDbPath, album.Id) == "FFMPEG ERROR")
                    {
                        LogToDownloader($"  ->[跳过-失败] '{album.Name}' (ID: {album.Id}) 已在失败列表中，本次不再重试。",
                            Brushes.IndianRed);
                        continue;
                    }

                    ClearDownloaderLogs();
                    UpdateStatus($"正在处理专辑: {album.Name}");
                    LogToDownloader($"[任务开始] 正在下载专辑 '{album.Name}' (URL: {album.Url})");

                    // 1. 创建 album.json
                    await CreateAlbumJson(album);
                    // 2. 运行下载器
                    FailedType failedType = await RunAndMonitorDownloader(token);

                    // 3. 清理和记录
                    CleanupAlbumJson();

                    if (failedType == FailedType.NoErr)
                    {
                        MarkAlbumAsProcessed(progressDbPath, album.Id);
                        RemoveFailedAlbumIfExists(failedDbPath, album.Id);
                        _totalAlbumsDownloadedThisSession++;
                        LogToDecryptor($"[成功] '{album.Name}' 下载完成。本轮已下载 {_totalAlbumsDownloadedThisSession} 张专辑。",
                            Brushes.Green);
                    }
                    else
                    {
                        LogToDownloader($"[失败] '{album.Name}' 下载失败。将记录到失败数据库。", Brushes.Red);
                        // 【新增】调用记录失败任务的方法
                        string reason = "NormalError";
                        if (failedType == FailedType.ValidationErr)
                        {
                            reason = "FFMPEG ERROR";
                        }
                        else if (failedType == FailedType.NetworkErr)
                        {
                            reason = "Network ERROR";
                        }

                        LogFailedAlbum(failedDbPath, reason, album);
                    }

                    // 4. 检查会话上限
                    if (_totalAlbumsDownloadedThisSession >= 10)
                    {
                        LogToDownloader("[会话停止] 累计下载专辑数已达到10张上限！", Brushes.Red);
                        UpdateStatus("专辑总数达到10上限，会话已停止。");
                        _cancellationTokenSource?.Cancel();
                        break;
                    }

                    LogToDecryptor("正在等待下个专辑，10s开始");
                    await Task.Delay(10000, token); // 每张专辑之间等待10秒
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
                if (data.Contains("listening m3u8 request on", StringComparison.OrdinalIgnoreCase) ||
                    data.Contains("ready", StringComparison.OrdinalIgnoreCase))
                {
                    decryptorReadyTcs.TrySetResult(true);
                }

                if (data.ToUpper().Contains("ERROR") ||
                    data.Contains("login failed", StringComparison.OrdinalIgnoreCase)) // 左边异常，重启
                {
                    LogToDecryptor("检测到错误，将终止会话...", Brushes.OrangeRed);
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
            var completedTask =
                await Task.WhenAny(decryptorReadyTcs.Task, Task.Delay(TimeSpan.FromSeconds(300), token));
            if (completedTask != decryptorReadyTcs.Task || !decryptorReadyTcs.Task.Result)
            {
                LogToDecryptor("Decryptor 未能在300秒内进入就绪状态。", Brushes.Red);
                return false;
            }

            LogToDecryptor("Decryptor 已就绪。", Brushes.Green);
            return true;
        }

        private async Task<FailedType> RunAndMonitorDownloader(CancellationToken token)
        {
            var batFile = Path.Combine(_baseDirectory!, "2. Run downloader.bat");
            if (!File.Exists(batFile))
            {
                MessageBox.Show("错误: 找不到 '2. Run downloader.bat' 启动脚本。", "文件未找到", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return FailedType.NormalErr;
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
            var outputTcs = new TaskCompletionSource<FailedType>();

            // 使用 StringBuilder 收集所有日志，以便在需要时进行分析
            var processOutputLog = new StringBuilder();

            void HandleOutput(string data)
            {
                if (string.IsNullOrEmpty(data)) return;

                LogToDownloader(data);
                processOutputLog.AppendLine(data); // 收集所有日志

                // 【核心逻辑】根据日志内容实时判断并设置最终结果
                if (data.Contains("Exit Finished!"))
                {
                    outputTcs.TrySetResult(FailedType.NoErr);
                }
                else if (data.Contains("[FAILURE] FFMPEG validation failed"))
                {
                    outputTcs.TrySetResult(FailedType.ValidationErr);
                }
                else if (data.Contains("=======  [✔ ] Completed:") && data.Contains("Errors:"))
                {
                    try
                    {
                        // 使用正则表达式精确匹配 "Errors: X" 部分
                        Match match = Regex.Match(data, @"Errors:\s*(\d+)");
                        if (match.Success)
                        {
                            int errorCount = int.Parse(match.Groups[1].Value);
                            if (errorCount > 0)
                            {
                                outputTcs.TrySetResult(FailedType.NetworkErr);
                            }
                            // 注意：如果 errorCount 是 0，我们不在这里设置成功，等待 "Exit Finished!" 信号
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToDownloader($"解析错误计数失败: {ex.Message}", Brushes.OrangeRed);
                    }
                }
                else if (data.ToUpper().Contains("CRITICAL ERROR") ||
                         (_failedAlbumStr != null && data.Contains(_failedAlbumStr)))
                {
                    outputTcs.TrySetResult(FailedType.NormalErr);
                }
            }

            _downloaderProcess.OutputDataReceived += (s, e) => HandleOutput(e.Data);
            _downloaderProcess.ErrorDataReceived += (s, e) => HandleOutput(e.Data);

            _downloaderProcess.Start();
            _downloaderProcess.BeginOutputReadLine();
            _downloaderProcess.BeginErrorReadLine();
            LogToDownloader($"Downloader 进程已启动 (PID: {_downloaderProcess.Id}).");

            // 等待来自日志的明确信号 或 进程自己退出
            Task processExitTask = _downloaderProcess.WaitForExitAsync(token);
            await Task.WhenAny(outputTcs.Task, processExitTask);

            // 无论哪个任务先完成，都确保进程被终止
            KillProcess(_downloaderProcess);

            // 【最终裁决】
            if (outputTcs.Task.IsCompleted)
            {
                // 我们从日志中收到了明确的信号，以此为准
                return await outputTcs.Task;
            }
            else
            {
                // 进程退出了，但我们没收到任何明确信号。
                // 这通常意味着出错了，比如脚本崩溃。
                LogToDownloader("进程意外退出，未收到明确的成功或失败信号。判定为普通错误。", Brushes.Red);
                return FailedType.NormalErr;
            }
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
            // 步骤 1: 终止 C# 这边管理的 downloader 进程对象
            KillProcess(_downloaderProcess);
            _downloaderProcess = null;
            KillProcess(_decryptorProcess);
            _decryptorProcess = null;
            // 步骤 2: 【关键】执行 WSL 内部的深度清理
            if (string.IsNullOrEmpty(_baseDirectory)) return;

            try
            {
                // 构建 pkill 命令，严格按照您的参考代码逻辑
                // pkill -f 会根据进程的完整命令行进行匹配
                // 'wrapper' 会匹配 "cd && ./wrapper -L 'U:P'"
                // 'controller.py' 会匹配 "python3 -u controller.py"
                // 使用分号确保两条命令都会执行
                string cleanupCommand = "pkill -f 'wrapper'; pkill -f 'controller.py'";

                LogToDecryptor($"[清理] 正在 WSL 内部执行: {cleanupCommand}", Brushes.Orange);

                var processInfo = new ProcessStartInfo
                {
                    // 使用 LxRunOffline.exe 执行 WSL 命令
                    FileName = Path.Combine(_baseDirectory, "wsl1", "LxRunOffline.exe"),

                    // 传入参数：r(run), -n u22-amdl (指定发行版), -c "命令"
                    Arguments = $"r -n u22-amdl -c \"{cleanupCommand}\"",

                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                // 启动清理进程并等待它完成
                using var cleanupProcess = Process.Start(processInfo);
                cleanupProcess?.WaitForExit(10000); // 等待最多10秒
            }
            catch (Exception ex)
            {
                // 在 UI 线程上安全地更新状态栏
                Dispatcher.BeginInvoke(() => UpdateStatus($"WSL cleanup failed: {ex.Message}"));
            }
            finally
            {
                // 步骤 3: 无论 WSL 清理是否成功，最后都尝试终止 C# 这边的 decryptor 进程对象
                KillProcess(_decryptorProcess);
                _decryptorProcess = null;
            }
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

        private void InitializeFailedAlbumsDatabase(string dbPath)
        {
            using var con = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            con.Open();
            string sql = @"
                        CREATE TABLE IF NOT EXISTS failed_tasks (
                            album_id TEXT PRIMARY KEY, 
                            album_name TEXT,
                            album_url TEXT,
                            reason TEXT,
                            failed_at TEXT
                        )";
            using var cmd = new SQLiteCommand(sql, con);
            cmd.ExecuteNonQuery();
        }

        private void RemoveFailedAlbumIfExists(string failedDbPath, string albumId)
        {
            // 检查失败数据库文件是否存在，如果不存在就没必要继续了
            if (!File.Exists(failedDbPath)) return;

            try
            {
                using var con = new SQLiteConnection($"Data Source={failedDbPath};Version=3;");
                con.Open();

                // 准备 DELETE 语句，根据 album_id 删除记录
                string sql = "DELETE FROM failed_tasks WHERE album_id = @id";

                using var cmd = new SQLiteCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", albumId);

                // 执行命令。如果不存在匹配的记录，ExecuteNonQuery 不会报错，只会返回 0。
                int rowsAffected = cmd.ExecuteNonQuery();

                // （可选日志）如果确实删除了记录，可以打印一条日志确认
                if (rowsAffected > 0)
                {
                    LogToDownloader($"  -> 已从失败记录中移除专辑: {albumId}", Brushes.DarkGray);
                }
            }
            catch (Exception ex)
            {
                // 即使删除失败，也不应该影响主流程，只记录错误即可
                LogToDownloader($"从失败记录中移除专辑 {albumId} 时出错: {ex.Message}", Brushes.OrangeRed);
            }
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

        private bool IsAlbumFailed(string failedDbPath, string albumId)
        {
            // 如果失败数据库文件本身就不存在，那里面肯定没有任何记录
            if (!File.Exists(failedDbPath)) return false;

            try
            {
                using var con = new SQLiteConnection($"Data Source={failedDbPath};Version=3;");
                con.Open();

                // 查询 failed_tasks 表中是否存在对应的 album_id
                using var cmd = new SQLiteCommand("SELECT 1 FROM failed_tasks WHERE album_id = @id", con);
                cmd.Parameters.AddWithValue("@id", albumId);

                // cmd.ExecuteScalar() != null 会在找到记录时返回 true，否则返回 false
                return cmd.ExecuteScalar() != null;
            }
            catch (Exception ex)
            {
                // 如果查询时发生错误，为了安全起见，我们假设它没有失败，
                // 让主流程有机会去处理它。同时记录下错误。
                LogToDownloader($"查询失败记录时出错: {ex.Message}", Brushes.OrangeRed);
                return false;
            }
        }

        private string? GetAlbumFailedReason(string failedDbPath, string albumId)
        {
            // 如果失败数据库文件不存在，直接返回 null
            if (!File.Exists(failedDbPath)) return null;

            try
            {
                using var con = new SQLiteConnection($"Data Source={failedDbPath};Version=3;");
                con.Open();

                // 查询 failed_tasks 表中指定 album_id 的 reason 字段
                using var cmd = new SQLiteCommand("SELECT reason FROM failed_tasks WHERE album_id = @id", con);
                cmd.Parameters.AddWithValue("@id", albumId);

                // ExecuteScalar 会返回查询结果的第一行第一列的值
                // 如果没有找到记录，它会返回 null
                object? result = cmd.ExecuteScalar();

                // 将结果转换为字符串。如果结果是 null，ToString() 会抛出异常，
                // 所以我们使用 C# 的模式匹配或条件转换来安全地处理。
                return result?.ToString();
            }
            catch (Exception ex)
            {
                // 如果查询时发生错误，记录日志并返回 null
                LogToDownloader($"查询失败原因时出错: {ex.Message}", Brushes.OrangeRed);
                return null;
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

        private void LogFailedAlbum(string failedDbPath, string reason, AlbumInfo album)
        {
            try
            {
                using var con = new SQLiteConnection($"Data Source={failedDbPath};Version=3;");
                con.Open();
                string sql = @"
        INSERT OR REPLACE INTO failed_tasks (album_id, album_name, album_url, reason,failed_at) 
        VALUES (@id, @name, @url, @reason,@time)";
                using var cmd = new SQLiteCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", album.Id);
                cmd.Parameters.AddWithValue("@name", album.Name);
                cmd.Parameters.AddWithValue("@url", album.Url);
                cmd.Parameters.AddWithValue("@reason", reason);
                cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("s"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                LogToDownloader($"记录失败任务到数据库时出错: {ex.Message}", Brushes.OrangeRed);
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

        private void LogToDownloader(string message, Brush? color = null)
        {
            // 1. 首先，执行原来的操作：将日志显示在界面上
            LogTo(DownloaderLogRtb, message, color);

            // 2. 【新增】然后，将日志消息写入到文件中
            try
            {
                // 确保数据库/日志目录路径有效
                if (!string.IsNullOrEmpty(_dbBaseDirectory))
                {
                    // 构造完整的日志文件路径
                    string logFilePath = Path.Combine(_dbBaseDirectory, "download.log");

                    // 使用 lock 确保同一时间只有一个线程可以写入文件，避免冲突
                    lock (_logFileLock)
                    {
                        // 将消息追加到文件末尾，并添加一个换行符
                        // File.AppendAllText 会自动创建文件（如果不存在）
                        File.AppendAllText(logFilePath, message + Environment.NewLine);
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果写入日志文件失败（例如权限问题），我们不希望程序崩溃。
                // 只是在调试输出中打印一个错误，以便开发者可以看到。
                System.Diagnostics.Debug.WriteLine($"[ERROR] Failed to write to download.log: {ex.Message}");
            }
        }

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