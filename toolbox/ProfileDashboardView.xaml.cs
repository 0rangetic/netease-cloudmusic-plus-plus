using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NeteaseToolbox.Wpf
{
    public partial class ProfileDashboardView : UserControl
    {
        public ObservableCollection<TagCategoryGroup> CategoryGroups { get; } = new ObservableCollection<TagCategoryGroup>();
        public ObservableCollection<GenreComparisonItem> Comparisons { get; } = new ObservableCollection<GenreComparisonItem>();
        public ObservableCollection<SongDetailItem> AllSongs { get; } = new ObservableCollection<SongDetailItem>();
        public ObservableCollection<SongDetailItem> FilteredSongs { get; } = new ObservableCollection<SongDetailItem>();
        public ProfileMetrics Metrics { get; } = new ProfileMetrics();
        public AiInsightModel AiInsight { get; } = new AiInsightModel();

        private string currentScope = "all";
        private bool isLoading = false;
        private readonly JavaScriptSerializer jsonSerializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Action<string, string, string> ReportTaskAction { get; set; }

        public ProfileDashboardView()
        {
            InitializeComponent();
            DataContext = this;
            CategoryGroupsControl.ItemsSource = CategoryGroups;
            ComparisonDataGrid.ItemsSource = Comparisons;
            SongDetailsDataGrid.ItemsSource = FilteredSongs;
            UpdateComparisonTab(currentScope);
            Loaded += async (s, e) => { AttachLocalPlayback(); await LoadPortraitDataAsync(currentScope); };
        }

        private void ReportTask(string name, string status, string detail)
        {
            ReportTaskAction?.Invoke(name, status, detail);
        }

        private string ProfileJsonPath
        {
            get
            {
                try
                {
                    string profileDir = NeteaseAuth.AccountFolder("Profile");
                    return Path.Combine(profileDir, "netease-profile.json");
                }
                catch
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"NeteaseToolbox\Profile\netease-profile.json");
                }
            }
        }

        private string GenreDbPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"NeteaseToolbox\GenreAnalysis\analysis.sqlite3");

        private static string AppBaseDirectory =>
            Path.GetDirectoryName(typeof(ProfileDashboardView).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;

        private string PythonExePath
        {
            get
            {
                string bundled = Path.Combine(AppBaseDirectory, @"runtime\python\python.exe");
                if (File.Exists(bundled)) return bundled;
                string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python");
                if (Directory.Exists(localApp))
                {
                    foreach (var dir in Directory.GetDirectories(localApp, "Python3*"))
                    {
                        string exe = Path.Combine(dir, "python.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                return "python.exe";
            }
        }

        private string ScriptPath(string name) =>
            Path.Combine(AppBaseDirectory, name);

        public async Task LoadPortraitDataAsync(string scope, bool reportTask = true)
        {
            if (isLoading) return;
            isLoading = true;
            currentScope = scope;
            UpdateComparisonTab(scope);

            string taskName = scope == "all" ? "所有画像分析" : "近30天画像分析";
            if (reportTask) ReportTask(taskName, "开始", "正在读取本地听歌档案与曲风数据库…");

            try
            {
                string profilePath = ProfileJsonPath;
                string genreDb = GenreDbPath;

                if (!File.Exists(profilePath))
                {
                    AsOfTextBlock.Text = "未找到听歌档案，请先同步云端数据";
                    if (reportTask) ReportTask(taskName, "失败", "未找到本地听歌档案；请点击“同步云端数据”。");
                    return;
                }

                if (!File.Exists(genreDb))
                {
                    AsOfTextBlock.Text = "未找到曲风数据库，请先在曲风分析页扫描";
                    if (reportTask) ReportTask(taskName, "部分失败", "缺少曲风标签数据库；请先进行曲风分析。");
                }

                // 异步读取 profile.json，提取歌曲明细和生成时间
                await Task.Run(() =>
                {
                    try
                    {
                        string profileJsonText = File.ReadAllText(profilePath, Encoding.UTF8);
                        var profileDict = jsonSerializer.Deserialize<Dictionary<string, object>>(profileJsonText);

                        Dispatcher.Invoke(() =>
                        {
                            if (profileDict.TryGetValue("generatedAt", out var genAt) && genAt != null)
                            {
                                AsOfTextBlock.Text = "更新于 " + FormatProfileGeneratedAt(genAt);
                            }

                            if (profileDict.TryGetValue("songs", out var songsObj) && songsObj is System.Collections.ArrayList songList)
                            {
                                AllSongs.Clear();
                                foreach (var item in songList)
                                {
                                    if (item is Dictionary<string, object> song)
                                    {
                                        var detail = new SongDetailItem
                                        {
                                            SongId = song.ContainsKey("songId") ? song["songId"]?.ToString() : "",
                                            Title = song.ContainsKey("title") ? song["title"]?.ToString() : "",
                                            Artist = song.ContainsKey("artist") ? song["artist"]?.ToString() : "",
                                            Album = song.ContainsKey("album") ? song["album"]?.ToString() : "",
                                            PlayCount = song.ContainsKey("playCount") && song["playCount"] != null ? Convert.ToInt64(song["playCount"]) : 0,
                                            FirstListen = song.ContainsKey("firstListenAt") && song["firstListenAt"] != null ? FormatEpochMs(song["firstListenAt"]) : "",
                                            LikedAt = song.ContainsKey("likedAt") && song["likedAt"] != null ? FormatEpochMs(song["likedAt"]) : ""
                                        };
                                        AllSongs.Add(detail);
                                    }
                                }
                                ApplySongFilter();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("解析 profile.json 歌曲明细失败: " + ex.Message);
                    }
                });

                // 调用 profile_analysis.py 获取指标与动态分类组数据
                string analysisScript = ScriptPath("profile_analysis.py");
                if (File.Exists(analysisScript) && File.Exists(genreDb))
                {
                    var result = await RunPythonAnalysisAsync(analysisScript, scope, profilePath, genreDb);
                    ParseAnalysisOutput(result);
                }

                // 读取现有的 AI 解读（如果有）
                LoadAiPortraitInsight(scope);

                if (reportTask) ReportTask(taskName, "完成", "听歌画像数据已成功载入。");
            }
            catch (Exception ex)
            {
                if (reportTask) ReportTask(taskName, "失败", "画像分析异常: " + ex.Message);
            }
            finally
            {
                isLoading = false;
            }
        }

        private static string FormatEpochMs(object val)
        {
            try
            {
                long ms = Convert.ToInt64(val);
                if (ms <= 0) return "";
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            catch { return ""; }
        }

        private static string FormatProfileGeneratedAt(object value)
        {
            if (value == null) return "";
            string raw = value.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return "";

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed.ToLocalTime().ToString("MM.dd，HH:mm", CultureInfo.InvariantCulture);
            }
            return raw;
        }

        private async Task<string> RunPythonAnalysisAsync(string script, string scope, string profilePath, string genreDb)
        {
            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(PythonExePath)
                {
                    Arguments = $"\"{script}\" --scope {scope} --profile \"{profilePath}\" --genre-db \"{genreDb}\"",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"退出码: {process.ExitCode}" : stderr);
                    }
                    return stdout;
                }
            });
        }

        private void ParseAnalysisOutput(string stdout)
        {
            CategoryGroups.Clear();
            Comparisons.Clear();

            var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                string kind = parts[0];
                if (kind == "METRICS" && parts.Length >= 4)
                {
                    int.TryParse(DecodeWorkerText(parts[1]), out int total);
                    int.TryParse(DecodeWorkerText(parts[2]), out int genreCovered);
                    int.TryParse(DecodeWorkerText(parts[3]), out int moodCovered);

                    Metrics.TotalTracks = total;
                    Metrics.GenreCovered = genreCovered;
                    Metrics.MoodCovered = moodCovered;

                    MetricTotalTracks.Text = Metrics.TotalTracksText;
                    MetricGenreCovered.Text = Metrics.GenreCoveredText;
                    MetricMoodCovered.Text = Metrics.MoodCoveredText;
                }
                else if (kind == "CATEGORY" && parts.Length >= 7)
                {
                    // 动态分类标签组（曲风、情绪，以及后续扩展的语种、歌手等任意分类）
                    string key = DecodeWorkerText(parts[1]);
                    string title = DecodeWorkerText(parts[2]);
                    string definition = DecodeWorkerText(parts[3]);
                    int.TryParse(DecodeWorkerText(parts[4]), out int covered);
                    int.TryParse(DecodeWorkerText(parts[5]), out int total);
                    string itemsJson = DecodeWorkerText(parts[6]);

                    var group = new TagCategoryGroup
                    {
                        Key = key,
                        Title = title,
                        Definition = definition,
                        CoveredTracks = covered,
                        TotalTracks = total
                    };

                    try
                    {
                        var rawItems = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(itemsJson);
                        if (rawItems != null)
                        {
                            foreach (var item in rawItems)
                            {
                                string label = item.ContainsKey("label") ? item["label"]?.ToString() : "";
                                int tracks = item.ContainsKey("tracks") && item["tracks"] != null ? Convert.ToInt32(item["tracks"]) : 0;
                                double share = item.ContainsKey("share") && item["share"] != null ? Convert.ToDouble(item["share"]) : 0.0;

                                group.Items.Add(new TagShareItem
                                {
                                    Label = label,
                                    TrackCount = tracks,
                                    Share = share
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"解析分类组 [{title}] 项失败: " + ex.Message);
                    }

                    CategoryGroups.Add(group);
                }
                else if (kind == "COMPARE" && parts.Length >= 7)
                {
                    Comparisons.Add(new GenreComparisonItem
                    {
                        Genre = DecodeWorkerText(parts[1]),
                        RecentCount = int.TryParse(DecodeWorkerText(parts[2]), out int rc) ? rc : 0,
                        RecentPct = DecodeWorkerText(parts[3]),
                        GlobalCount = int.TryParse(DecodeWorkerText(parts[4]), out int gc) ? gc : 0,
                        GlobalPct = DecodeWorkerText(parts[5]),
                        Delta = DecodeWorkerText(parts[6])
                    });
                }
            }
        }

        private static string DecodeWorkerText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string padded = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            byte[] bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            return Encoding.UTF8.GetString(bytes);
        }

        private void LoadAiPortraitInsight(string scope)
        {
            try
            {
                string aiConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NeteaseToolbox\Profile\portrait-ai.json");
                if (File.Exists(aiConfigPath))
                {
                    string text = File.ReadAllText(aiConfigPath, Encoding.UTF8);
                    var dict = jsonSerializer.Deserialize<Dictionary<string, object>>(text);
                    if (dict != null)
                    {
                        string model = dict.ContainsKey("model") ? dict["model"]?.ToString() : "";
                        AiInsight.Model = string.IsNullOrWhiteSpace(model) ? "未配置模型" : model;
                    }
                }

                // 查找最近生成的报告中的 AI 解读
                string latestReport = FindLatestReport(scope);
                if (latestReport != null)
                {
                    string html = File.ReadAllText(latestReport, Encoding.UTF8);
                    int aiStart = html.IndexOf("<div class=\"prose\">");
                    if (aiStart > 0)
                    {
                        aiStart += 19;
                        int aiEnd = html.IndexOf("</div>", aiStart);
                        if (aiEnd > aiStart)
                        {
                            string prose = html.Substring(aiStart, aiEnd - aiStart).Trim();
                            AiInsight.Content = System.Net.WebUtility.HtmlDecode(prose);
                            string insightStatus = "已生成解读";
                            int aiSection = html.IndexOf("<h2>AI 解读</h2>", StringComparison.Ordinal);
                            if (aiSection >= 0)
                            {
                                const string metaOpen = "<p class=\"muted\">";
                                int metaStart = html.IndexOf(metaOpen, aiSection, StringComparison.Ordinal);
                                int metaEnd = metaStart >= 0 ? html.IndexOf("</p>", metaStart, StringComparison.Ordinal) : -1;
                                if (metaStart >= 0 && metaEnd > metaStart)
                                {
                                    string meta = System.Net.WebUtility.HtmlDecode(html.Substring(metaStart + metaOpen.Length, metaEnd - metaStart - metaOpen.Length));
                                    if (meta.Contains("AI 已停用")) insightStatus = "AI 已停用";
                                    else if (meta.Contains("AI 解读失败") || meta.Contains("AI 解读不可用")) insightStatus = "解读失败";
                                    else if (meta.Contains("尚未生成")) insightStatus = "未生成解读";
                                }
                            }
                            if (string.Equals(insightStatus, "已生成解读", StringComparison.Ordinal))
                            {
                                insightStatus += ResolveInsightTimestampSuffix(scope, html, latestReport);
                            }
                            AiInsight.StatusText = insightStatus;
                            return;
                        }
                    }
                }

                AiInsight.StatusText = "未生成解读";
                AiInsight.Content = "点击本卡片右上角“刷新画像”或页面顶部“刷新全部画像”，将基于本地规则统计与 AI 接口自动生成最新深度画像解读。";
            }
            catch (Exception ex)
            {
                AiInsight.StatusText = "读取失败";
                AiInsight.Content = "读取 AI 画像失败: " + ex.Message;
            }
        }

        private string ResolveInsightTimestampSuffix(string scope, string reportHtml, string reportPath)
        {
            DateTime? generatedAt = TryReadAiResultUpdatedAt(scope);
            if (!generatedAt.HasValue) generatedAt = TryReadReportGeneratedAt(reportHtml);
            if (!generatedAt.HasValue) generatedAt = TryGetReportLastWriteTime(reportPath);
            return generatedAt.HasValue
                ? ":" + generatedAt.Value.ToString("MM.dd，HH:mm", CultureInfo.InvariantCulture)
                : "";
        }

        private DateTime? TryReadAiResultUpdatedAt(string scope)
        {
            try
            {
                string statePath = null;
                foreach (string dir in new[]
                {
                    NeteaseAuth.AccountFolder("Profile"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\Profile")
                })
                {
                    string candidate = Path.Combine(dir, "portrait-ai-results.json");
                    if (File.Exists(candidate)) { statePath = candidate; break; }
                }
                if (statePath == null) return null;

                var state = jsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath, Encoding.UTF8));
                if (state == null) return null;

                string key = string.IsNullOrEmpty(scope) ? "all" : scope;
                if (!state.TryGetValue(key, out var entryObj) || !(entryObj is Dictionary<string, object> entry)) return null;

                string status = entry.ContainsKey("status") ? entry["status"]?.ToString() : "";
                if (!string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)) return null;

                string updatedAt = entry.ContainsKey("updatedAt") ? entry["updatedAt"]?.ToString() : "";
                return ParseLocalTimestamp(updatedAt);
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? TryReadReportGeneratedAt(string reportHtml)
        {
            try
            {
                if (string.IsNullOrEmpty(reportHtml)) return null;
                const string marker = "本机生成";
                int markerIndex = reportHtml.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0) return null;

                int colonIndex = reportHtml.IndexOfAny(new[] { '：', ':' }, markerIndex + marker.Length);
                if (colonIndex < 0 || colonIndex - markerIndex > marker.Length + 2) return null;

                int end = colonIndex + 1;
                while (end < reportHtml.Length && reportHtml[end] != '<' && reportHtml[end] != '\r' && reportHtml[end] != '\n')
                {
                    end++;
                }

                string raw = System.Net.WebUtility.HtmlDecode(reportHtml.Substring(colonIndex + 1, end - colonIndex - 1)).Trim();
                return ParseLocalTimestamp(raw);
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseLocalTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)) return null;
            return parsed.ToLocalTime().DateTime;
        }

        private static DateTime? TryGetReportLastWriteTime(string reportPath)
        {
            try
            {
                if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath)) return null;
                return File.GetLastWriteTime(reportPath);
            }
            catch
            {
                return null;
            }
        }

        private string FindLatestReport(string scope)
        {
            string reportsDir = "";
            try
            {
                string reportProject = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chart-report-project.txt");
                if (File.Exists(reportProject))
                {
                    reportsDir = File.ReadAllText(reportProject, Encoding.UTF8).Trim();
                }
            }
            catch { }
            if (string.IsNullOrEmpty(reportsDir) || !Directory.Exists(reportsDir))
            {
                reportsDir = Path.Combine(NeteaseAuth.AccountFolder("Reports"));
            }
            if (!Directory.Exists(reportsDir)) return null;

            string currentReport = Path.Combine(reportsDir, ".data-app-offline", "exports", "music-profile-report-" + scope + ".html");
            if (File.Exists(currentReport)) return currentReport;

            string pattern = scope == "all" ? "listening_all_*.html" : "listening_recent_*.html";
            return Directory.GetFiles(reportsDir, pattern)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }

        private void UpdateComparisonTab(string scope)
        {
            if (ComparisonTab == null) return;
            bool showComparison = scope == "recent";
            ComparisonTab.Visibility = showComparison ? Visibility.Visible : Visibility.Collapsed;
            if (!showComparison && ComparisonTab.IsSelected && SongDetailsTab != null)
            {
                SongDetailsTab.IsSelected = true;
            }
        }

        private void OnScopeChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            string newScope = RadioScopeAll.IsChecked == true ? "all" : "recent";
            UpdateComparisonTab(newScope);
            if (newScope != currentScope)
            {
                _ = LoadPortraitDataAsync(newScope);
            }
        }

        private async void OnRefreshAllPortraitsClick(object sender, RoutedEventArgs e)
        {
            await RefreshAllPortraitsAsync();
        }

        private async void OnRefreshInsightClick(object sender, RoutedEventArgs e)
        {
            await RefreshAllPortraitsAsync();
        }

        private async Task RefreshAllPortraitsAsync()
        {
            RefreshAllButton.IsEnabled = false;
            if (RefreshInsightButton != null) RefreshInsightButton.IsEnabled = false;
            ReportTask("全部画像分析", "开始", "正在刷新两类画像、规则统计与 AI 报告…");
            try
            {
                var toolbox = ToolboxForm.Instance;
                if (toolbox != null) await toolbox.RefreshAllPortraits(false);
                await LoadPortraitDataAsync(currentScope, false);
            }
            catch (Exception ex)
            {
                ReportTask("全部画像分析", "失败", ex.Message);
            }
            finally
            {
                RefreshAllButton.IsEnabled = true;
                if (RefreshInsightButton != null) RefreshInsightButton.IsEnabled = true;
            }
        }

        private async void OnSyncProfileClick(object sender, RoutedEventArgs e)
        {
            SyncProfileButton.IsEnabled = false;
            ReportTask("听歌档案同步", "开始", "正在从网易云服务器同步红心与听歌档案…");
            try
            {
                string script = ScriptPath("profile_fetch.py");
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo(PythonExePath)
                    {
                        Arguments = $"\"{script}\"",
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var p = new Process { StartInfo = psi })
                    {
                        p.Start();
                        var stdoutTask = p.StandardOutput.ReadToEndAsync();
                        var stderrTask = p.StandardError.ReadToEndAsync();
                        p.WaitForExit();
                        Task.WaitAll(stdoutTask, stderrTask);
                        if (p.ExitCode != 0)
                        {
                            string fatal = null;
                            foreach (var line in stdoutTask.Result.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var parts = line.Split('\t');
                                if (parts.Length >= 2 && parts[0] == "FATAL")
                                {
                                    try { fatal = DecodeWorkerText(parts[1]); } catch { }
                                    break;
                                }
                            }
                            string stderrTail = (stderrTask.Result ?? "")
                                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                .LastOrDefault();
                            if (stderrTail != null && stderrTail.Length > 300)
                                stderrTail = stderrTail.Substring(stderrTail.Length - 300);
                            string detail = string.IsNullOrWhiteSpace(fatal) ? stderrTail?.Trim() : fatal;
                            if (string.IsNullOrWhiteSpace(detail))
                                detail = $"网易云档案同步进程异常退出（退出码 {p.ExitCode}），未返回错误详情。";
                            throw new InvalidOperationException("同步失败：" + detail);
                        }
                    }
                });

                await LoadPortraitDataAsync(currentScope);
                ReportTask("听歌档案同步", "完成", "云端听歌档案同步完成。");
            }
            catch (Exception ex)
            {
                ReportTask("听歌档案同步", "失败", ex.Message);
            }
            finally
            {
                SyncProfileButton.IsEnabled = true;
            }
        }

        private void OnOpenExternalReportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string report = FindLatestReport(currentScope);
                if (report != null)
                {
                    Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
                    ReportTask("独立报告", "完成", "已在默认浏览器中打开 HTML 图表报告。");
                    return;
                }
                ReportTask("独立报告", "失败", "尚未找到对应报告，请先点击“刷新全部画像”。");
            }
            catch (Exception ex)
            {
                ReportTask("独立报告", "失败", ex.Message);
            }
        }

        private bool localPlaybackAttached;

        private void AttachLocalPlayback()
        {
            if (localPlaybackAttached || LocalPlaybackHost == null) return;
            var target = NeteaseToolbox.ToolboxForm.Instance;
            var content = target == null ? null : target.GetLocalPlaybackContent();
            if (content == null) return;
            LocalPlaybackHost.Child = content;
            localPlaybackAttached = true;
        }

        private void OnPhoneSyncClick(object sender, RoutedEventArgs e)
        {
            var target = NeteaseToolbox.ToolboxForm.Instance;
            if (target == null) return;
            try { target.ShowPhoneSyncDialog(); }
            catch (Exception ex) { ReportTask("手机同步", "失败", ex.Message); }
        }

        private void OnSongSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySongFilter();
        }

        private void ApplySongFilter()
        {
            string keyword = SongSearchBox?.Text?.Trim() ?? "";
            FilteredSongs.Clear();

            IEnumerable<SongDetailItem> query = AllSongs;
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(s =>
                    (s.Title != null && s.Title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (s.Artist != null && s.Artist.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (s.Album != null && s.Album.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            foreach (var item in query)
            {
                FilteredSongs.Add(item);
            }

            if (SongCountFilterText != null)
            {
                SongCountFilterText.Text = $"共 {FilteredSongs.Count} / {AllSongs.Count} 首";
            }
        }

        private void OnSongRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SongDetailsDataGrid.SelectedItem is SongDetailItem item && !string.IsNullOrEmpty(item.SongId))
            {
                try
                {
                    SongNavigation.OpenSong(item.SongId, (task, status, detail) => ReportTask(task, status, detail));
                }
                catch (Exception ex)
                {
                    ReportTask("打开歌曲", "失败", ex.Message);
                }
            }
        }
    }
}
