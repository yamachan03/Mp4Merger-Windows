using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mp4Merger.Core;

namespace Mp4Merger
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<VideoItem> VideoFiles { get; set; } = new ObservableCollection<VideoItem>();
        public AppConfig Config { get; private set; } = new AppConfig();

        private DispatcherTimer? _themeTimer;
        private string _currentlyLoadedTheme = "";

        /// Set while a merge is running, so the Cancel button can stop it.
        private CancellationTokenSource? _processingCts;

        // Drag and drop sorting state
        private Point _dragStartPoint;
        private VideoItem? _draggedItem;

        public MainWindow()
        {
            InitializeComponent();
            FileListView.ItemsSource = VideoFiles;
            VideoFiles.CollectionChanged += VideoFiles_CollectionChanged;
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                MergeBtn.IsEnabled = false;

                // 1. Load config settings
                Config = AppConfig.Load();

                // 2. Onboarding Setup on First Launch
                if (string.IsNullOrEmpty(Config.Language))
                {
                    var welcome = new WelcomeWindow();
                    bool? setupResult = welcome.ShowDialog();
                    if (setupResult != true)
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                    Config = welcome.Config;
                }

                // 3. Apply Language and Theme
                ApplyLanguage(Config.Language);
                ApplyTheme(Config.Theme);
                StartThemeTimer();

                // 4. Update initial states
                KeepOptionsCheck.IsChecked = Config.KeepOptions;
                WarnOnSlowCheck.IsChecked = Config.WarnOnSlow;

                if (Config.KeepOptions)
                {
                    RestoreOptionsFromConfig();
                }

                // 5. Dependency Check/Download
                string statusMsg = Config.Language == "en" ? "Checking FFmpeg..." : "FFmpegをチェック中...";
                StatusText.Text = statusMsg;

                if (Core.DependencyManager.IsFfmpegMissing())
                {
                    string missingPrompt = Localizer.Get("FfmpegRequired", Config.Language);
                    string missingTitle = Localizer.Get("FfmpegRequiredTitle", Config.Language);
                    var result = MessageBox.Show(missingPrompt, missingTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        
                    if (result != MessageBoxResult.Yes)
                    {
                        Application.Current.Shutdown();
                        return;
                    }

                    await Core.DependencyManager.DownloadFfmpegAsync((pct, msg) => 
                    {
                        Dispatcher.Invoke(() => 
                        {
                            ProgressBar.Value = pct;
                            StatusText.Text = msg;
                        });
                    });
                }
                else
                {
                    StatusText.Text = Config.Language == "ja" ? "FFmpegの更新を確認中..." : "Checking for FFmpeg updates...";
                    if (await Core.DependencyManager.IsUpdateAvailableAsync("ffmpeg"))
                    {
                        string updatePrompt = Config.Language == "ja"
                            ? "FFmpegの新しいバージョンが利用可能です。\n今すぐ更新（ダウンロード）しますか？"
                            : "A new version of FFmpeg is available.\nDo you want to update it now?";
                        string updateTitle = Config.Language == "ja" ? "アップデートの確認" : "Update Available";
                        var result = MessageBox.Show(updatePrompt, updateTitle, MessageBoxButton.YesNo, MessageBoxImage.Information);
                            
                        if (result == MessageBoxResult.Yes)
                        {
                            await Core.DependencyManager.DownloadFfmpegAsync((pct, msg) => 
                            {
                                Dispatcher.Invoke(() => 
                                {
                                    ProgressBar.Value = pct;
                                    StatusText.Text = msg;
                                });
                            });
                        }
                    }
                }

                // 6. AI Upscaler Check/Download
                bool aiEnabled = true;
                if (Core.DependencyManager.IsAiMissing())
                {
                    string aiPrompt = Config.Language == "ja" 
                        ? "AI超解像度機能（Real-ESRGAN）を使用しますか？\n今すぐダウンロードを開始する場合は「はい」を押してください（約400MB）。\n\n※「いいえ」を選んだ場合、AI超解像度機能は無効になりますが、等倍マージ機能などアプリ自体は利用可能です。"
                        : "Do you want to use the AI Upscaler (Real-ESRGAN)?\nClick 'Yes' to start the download (approx. 400MB).\n\nIf you select 'No', the AI Upscaler will be disabled but the app itself will still function.";
                    string aiTitle = Config.Language == "ja" ? "追加コンポーネントの確認" : "Additional Component Check";
                    var result = MessageBox.Show(aiPrompt, aiTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
                        
                    if (result == MessageBoxResult.Yes)
                    {
                        await Core.DependencyManager.DownloadAiAsync((pct, msg) => 
                        {
                            Dispatcher.Invoke(() => 
                            {
                                ProgressBar.Value = pct;
                                StatusText.Text = msg;
                            });
                        });
                    }
                    else
                    {
                        aiEnabled = false;
                    }
                }
                else
                {
                    StatusText.Text = Config.Language == "ja" ? "AI超解像度機能の更新を確認中..." : "Checking for AI Upscaler updates...";
                    if (await Core.DependencyManager.IsUpdateAvailableAsync("realesrgan"))
                    {
                        string updatePrompt = Config.Language == "ja"
                            ? "AI超解像度機能（Real-ESRGAN）の新しいバージョンが利用可能です。\n今すぐ更新（ダウンロード）しますか？"
                            : "A new version of the AI Upscaler (Real-ESRGAN) is available.\nDo you want to update it now?";
                        string updateTitle = Config.Language == "ja" ? "アップデートの確認" : "Update Available";
                        var result = MessageBox.Show(updatePrompt, updateTitle, MessageBoxButton.YesNo, MessageBoxImage.Information);
                            
                        if (result == MessageBoxResult.Yes)
                        {
                            await Core.DependencyManager.DownloadAiAsync((pct, msg) => 
                            {
                                Dispatcher.Invoke(() => 
                                {
                                    ProgressBar.Value = pct;
                                    StatusText.Text = msg;
                                });
                            });
                        }
                    }
                }

                if (!aiEnabled)
                {
                    foreach (var item in ResolutionCombo.Items)
                    {
                        if (item is ComboBoxItem cbItem && cbItem.Content != null)
                        {
                            string contentStr = cbItem.Content.ToString()!;
                            if (contentStr.Contains("AI"))
                            {
                                cbItem.IsEnabled = false;
                                cbItem.Foreground = System.Windows.Media.Brushes.Gray;
                            }
                        }
                    }
                    if (ResolutionCombo.SelectedIndex >= 3)
                    {
                        ResolutionCombo.SelectedIndex = 0;
                    }
                }

                StatusText.Text = Localizer.Get("Ready", Config.Language);
            }
            catch (Exception ex)
            {
                string errMsg = Localizer.Get("ErrorOccurred", Config.Language);
                StatusText.Text = string.Format(errMsg, ex.Message);
                MessageBox.Show(string.Format(errMsg, ex.Message));
            }
            finally
            {
                MergeBtn.IsEnabled = true;
                ProgressBar.Value = 0;
            }
        }

        private void RestoreOptionsFromConfig()
        {
            FixJitterCheck.IsChecked = Config.FixJitter;
            StabilizeCheck.IsChecked = Config.Stabilize;
            StabilizeSmoothingSlider.Value = Config.StabilizeSmoothing;
            StabilizeSmoothingRow.IsEnabled = Config.Stabilize;
            HevcCheck.IsChecked = Config.Hevc;
            NormalizeAudioCheck.IsChecked = Config.NormalizeAudio;
            DeepCheckCheck.IsChecked = Config.DeepCheck;
            MergeOutputCheck.IsChecked = Config.MergeOutput;

            if (Config.Resolution == "1080p") ResolutionCombo.SelectedIndex = 1;
            else if (Config.Resolution == "4K") ResolutionCombo.SelectedIndex = 2;
            else ResolutionCombo.SelectedIndex = 0;
        }

        private void SaveOptionsToConfig()
        {
            if (!Config.KeepOptions) return;

            Config.FixJitter = FixJitterCheck.IsChecked == true;
            Config.Stabilize = StabilizeCheck.IsChecked == true;
            Config.StabilizeSmoothing = (int)StabilizeSmoothingSlider.Value;
            Config.Hevc = HevcCheck.IsChecked == true;
            Config.NormalizeAudio = NormalizeAudioCheck.IsChecked == true;
            Config.DeepCheck = DeepCheckCheck.IsChecked == true;
            Config.MergeOutput = MergeOutputCheck.IsChecked == true;

            if (ResolutionCombo.SelectedIndex == 1) Config.Resolution = "1080p";
            else if (ResolutionCombo.SelectedIndex == 2) Config.Resolution = "4K";
            else Config.Resolution = "Original";

            Config.Save();
        }

        private void StartThemeTimer()
        {
            _themeTimer = new DispatcherTimer();
            _themeTimer.Interval = TimeSpan.FromMinutes(1);
            _themeTimer.Tick += (s, e) => 
            {
                if (Config.Theme == "auto")
                {
                    ApplyTheme("auto");
                }
            };
            _themeTimer.Start();
        }

        private void ApplyTheme(string theme)
        {
            string targetTheme = theme;
            if (theme == "auto")
            {
                int hour = DateTime.Now.Hour;
                targetTheme = (hour >= 6 && hour < 18) ? "light" : "dark";
            }

            if (_currentlyLoadedTheme == targetTheme) return;

            var dicts = Application.Current.Resources.MergedDictionaries;
            dicts.Clear();

            string source = targetTheme == "light" ? "Themes/Light.xaml" : "Themes/Dark.xaml";
            try
            {
                var resourceDict = Application.LoadComponent(new Uri(source, UriKind.Relative)) as ResourceDictionary;
                if (resourceDict != null)
                {
                    dicts.Add(resourceDict);
                    _currentlyLoadedTheme = targetTheme;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error applying theme: " + ex.Message);
            }
        }

        private void ApplyLanguage(string lang)
        {
            Title = Localizer.Get("AppTitle", lang);
            TitleTxt.Text = Localizer.Get("AppTitle", lang);
            VersionTxt.Text = Localizer.Get("Version", lang);
            DropZonePromptTxt.Text = Localizer.Get("DropZonePrompt", lang);
            DropZoneSubTxt.Text = Localizer.Get("DropZoneSub", lang);
            OptionsHeader.Text = Localizer.Get("OptionsHeader", lang);
            ResolutionLabel.Text = Localizer.Get("Resolution", lang);
            FixJitterCheck.Content = Localizer.Get("FixJitter", lang);
            StabilizeCheck.Content = Localizer.Get("Stabilize", lang);
            StabilizeSmoothingLabel.Text = Localizer.Get("StabilizeSmoothing", lang);
            HevcCheck.Content = Localizer.Get("Hevc", lang);
            NormalizeAudioCheck.Content = Localizer.Get("NormalizeAudio", lang);
            DeepCheckCheck.Content = Localizer.Get("DeepCheck", lang);
            MergeOutputCheck.Content = Localizer.Get("MergeOutput", lang);
            OutputNameLabel.Text = Localizer.Get("OutputNameLabel", lang);
            KeepOptionsCheck.Content = Localizer.Get("KeepOptions", lang);
            WarnOnSlowCheck.Content = Localizer.Get("WarnOnSlow", lang);
            ClearBtn.Content = Localizer.Get("ClearAll", lang);
            SortBtn.Content = Localizer.Get("SortByName", lang);
            
            UpdateMergeButtonText(lang);

            ColName.Header = Localizer.Get("ColName", lang);
            ColSize.Header = Localizer.Get("ColSize", lang);
            ColDuration.Header = Localizer.Get("ColDuration", lang);
            ColTags.Header = Localizer.Get("ColTags", lang);

            ResOptFast.Content = (lang == "ja") ? "オリジナル（高速）" : "Original (Fast)";
            ResOpt1080.Content = (lang == "ja") ? "FHD 1080p (Lanczos)" : "FHD 1080p (Lanczos)";
            ResOpt4k.Content = (lang == "ja") ? "4K UHD (Lanczos)" : "4K UHD (Lanczos)";
            ResOptAi1x.Content = (lang == "ja") ? "AI画質補正（等倍）" : "AI Enhance x1 (Same Resolution)";
            ResOptAi4x.Content = (lang == "ja") ? "AI超解像 x4（実写）" : "AI Upscale x4 (Standard)";
            ResOptAiAnime.Content = (lang == "ja") ? "AI超解像 x4（アニメ）" : "AI Upscale x4 (Anime)";
            StatusText.Text = Localizer.Get("Ready", lang);
        }

        private void UpdateMergeButtonText(string lang)
        {
            if (MergeOutputCheck.IsChecked == true)
            {
                MergeBtn.Content = Localizer.Get("MergeFiles", lang);
            }
            else
            {
                MergeBtn.Content = Localizer.Get("ProcessFiles", lang);
            }
        }

        private void VideoFiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            PromptStack.Visibility = (VideoFiles.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            UpdateOutputFilename();
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow(Config);
            settings.Owner = this;
            if (settings.ShowDialog() == true)
            {
                Config = settings.Config;
                ApplyLanguage(Config.Language);
                ApplyTheme(Config.Theme);
                
                KeepOptionsCheck.IsChecked = Config.KeepOptions;
                WarnOnSlowCheck.IsChecked = Config.WarnOnSlow;
                
                if (Config.KeepOptions)
                {
                    SaveOptionsToConfig();
                }
            }
        }

        private void OptionChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                UpdateOutputFilename();
                SaveOptionsToConfig();
                UpdateMergeButtonText(Config.Language);
            }
        }

        private void StabilizeCheck_Changed(object sender, RoutedEventArgs e)
        {
            StabilizeSmoothingRow.IsEnabled = StabilizeCheck.IsChecked == true;
            OptionChanged(sender, e);
        }

        private void StabilizeSmoothingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Setting the slider's Minimum in XAML coerces its Value, which raises
            // this before the label below it has been created. The generated code
            // attaches the handler as soon as the slider itself exists.
            if (StabilizeSmoothingValueTxt is null) return;

            StabilizeSmoothingValueTxt.Text = ((int)e.NewValue).ToString();
            if (IsLoaded)
            {
                SaveOptionsToConfig();
            }
        }

        private void KeepOptionsCheck_Changed(object sender, RoutedEventArgs e)
        {
            Config.KeepOptions = KeepOptionsCheck.IsChecked == true;
            Config.Save();
            if (Config.KeepOptions)
            {
                SaveOptionsToConfig();
            }
        }

        private void WarnOnSlowCheck_Changed(object sender, RoutedEventArgs e)
        {
            Config.WarnOnSlow = WarnOnSlowCheck.IsChecked == true;
            Config.Save();
        }

        private void Border_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(typeof(VideoItem)))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private async void Border_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    string ffprobePath = ResolveToolPath("ffprobe");

                    foreach (string file in files)
                    {
                        string ext = Path.GetExtension(file).ToLower();
                        // Support mp4/mov and exclude .lrf files
                        if ((ext == ".mp4" || ext == ".mov") && !file.EndsWith(".lrf", StringComparison.OrdinalIgnoreCase))
                        {
                            var placeholderItem = new VideoItem
                            {
                                Name = Path.GetFileName(file),
                                Path = file,
                                Info = "...",
                                DurationStr = "...",
                                TagsStr = "..."
                            };
                            VideoFiles.Add(placeholderItem);

                            // Load details asynchronously
                            var index = VideoFiles.IndexOf(placeholderItem);
                            var details = await Task.Run(() => VideoItem.GetVideoDetailsAsync(file, ffprobePath));
                            if (index >= 0 && index < VideoFiles.Count)
                            {
                                VideoFiles[index] = details;
                            }
                        }
                    }
                    UpdateOutputFilename();
                }
            }
        }

        private string ResolveToolPath(string toolName)
        {
            return Core.AppPaths.ResolveTool(toolName);
        }

        // ListView Drag & Drop Sorting
        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item)
            {
                _dragStartPoint = e.GetPosition(null);
                _draggedItem = item.DataContext as VideoItem;
            }
        }

        private void ListViewItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
            {
                Point position = e.GetPosition(null);
                if (Math.Abs(position.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    DragDrop.DoDragDrop(FileListView, _draggedItem, DragDropEffects.Move);
                }
            }
        }

        private void ListViewItem_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(VideoItem)))
            {
                var sourceItem = e.Data.GetData(typeof(VideoItem)) as VideoItem;
                var targetItem = (sender as ListViewItem)?.DataContext as VideoItem;

                if (sourceItem != null && targetItem != null && sourceItem != targetItem)
                {
                    int sourceIdx = VideoFiles.IndexOf(sourceItem);
                    int targetIdx = VideoFiles.IndexOf(targetItem);

                    if (sourceIdx >= 0 && targetIdx >= 0)
                    {
                        VideoFiles.Move(sourceIdx, targetIdx);
                        UpdateOutputFilename();
                    }
                }
            }
        }

        private void FileListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                var selectedItems = FileListView.SelectedItems.Cast<VideoItem>().ToList();
                foreach (var item in selectedItems)
                {
                    VideoFiles.Remove(item);
                }
                UpdateOutputFilename();
            }
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            VideoFiles.Clear();
            OutputNameBox.Text = "";
            ProgressBar.Value = 0;
            StatusText.Text = Localizer.Get("Ready", Config.Language);

            if (!Config.KeepOptions)
            {
                ResolutionCombo.SelectedIndex = 0;
                FixJitterCheck.IsChecked = false;
                StabilizeCheck.IsChecked = false;
                StabilizeSmoothingSlider.Value = 50;
                StabilizeSmoothingRow.IsEnabled = false;
                HevcCheck.IsChecked = false;
                NormalizeAudioCheck.IsChecked = false;
                DeepCheckCheck.IsChecked = false;
                MergeOutputCheck.IsChecked = true;
            }
        }

        private void SortBtn_Click(object sender, RoutedEventArgs e)
        {
            var sortedList = VideoFiles.OrderBy(v => v.Name).ToList();
            VideoFiles.Clear();
            foreach (var item in sortedList)
            {
                VideoFiles.Add(item);
            }
            UpdateOutputFilename();
        }

        private void UpdateOutputFilename()
        {
            if (VideoFiles.Count == 0)
            {
                OutputNameBox.Text = "";
                return;
            }

            string originalName = Path.GetFileNameWithoutExtension(VideoFiles[0].Path) ?? "video";
            
            // 1. Sequence and existing suffixes Removal
            string baseName = Regex.Replace(originalName, @"(?i)(?:[ _-]+(?:\d{1,4}|norm|stab|hevc|jitter|1080p?|4k|merged|ai(_1x)?|fix))+$", "");
            
            // 2. Build new suffixes based on selections
            string suffix = "";

            if (FixJitterCheck.IsChecked == true) suffix += "_jitter";
            if (StabilizeCheck.IsChecked == true) suffix += "_stab";
            if (HevcCheck.IsChecked == true) suffix += "_hevc";
            if (NormalizeAudioCheck.IsChecked == true) suffix += "_norm";

            if (ResolutionCombo.SelectedIndex == 1) suffix += "_1080";
            else if (ResolutionCombo.SelectedIndex == 2) suffix += "_4k";
            else if (ResolutionCombo.SelectedIndex == 3) suffix += "_ai_1x";
            else if (ResolutionCombo.SelectedIndex == 4) suffix += "_ai";
            else if (ResolutionCombo.SelectedIndex == 5) suffix += "_ai_anime";
            
            // 3. Fallback / Overwrite Protection
            string proposed = $"{baseName}{suffix}.mp4";
            bool conflicts = false;
            foreach (var file in VideoFiles)
            {
                if (string.Equals(Path.GetFileName(file.Path), proposed, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts = true;
                    break;
                }
            }

            if (conflicts)
            {
                proposed = $"{baseName}{suffix}_merged.mp4";
            }
            
            OutputNameBox.Text = proposed;
        }

        private async void MergeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (VideoFiles.Count == 0)
            {
                MessageBox.Show(Localizer.Get("SelectFilesWarn", Config.Language));
                return;
            }

            // Perform slow process check
            if (Config.WarnOnSlow)
            {
                var reasons = GetReencodeReasons();
                if (reasons.Count > 0)
                {
                    string reasonsList = string.Join("\n", reasons.Select(r => Localizer.Get(r, Config.Language)));
                    string warnMsg = string.Format(Localizer.Get("SlowWarnMessage", Config.Language), reasonsList);
                    string warnTitle = Localizer.Get("SlowWarnTitle", Config.Language);

                    var result = MessageBox.Show(warnMsg, warnTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
            }

            MergeBtn.IsEnabled = false;
            CancelBtn.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            _processingCts = new CancellationTokenSource();
            var token = _processingCts.Token;

            try
            {
                var engine = new VideoEngine
                {
                    Language = Config.Language,
                    // The engine asks before anything that costs a full re-encode.
                    ConfirmAsync = question => Dispatcher.InvokeAsync(() =>
                        MessageBox.Show(this, question,
                            Localizer.Get("ReencodeNeededTitle", Config.Language),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes).Task
                };

                string resolution = "Original";
                if (ResolutionCombo.SelectedIndex == 1) resolution = "1080p";
                else if (ResolutionCombo.SelectedIndex == 2) resolution = "4K";
                else if (ResolutionCombo.SelectedIndex == 3) resolution = "AI_1x";
                else if (ResolutionCombo.SelectedIndex == 4) resolution = "AI_4x";
                else if (ResolutionCombo.SelectedIndex == 5) resolution = "AI_Anime";

                var filePaths = VideoFiles.Select(v => v.Path!).ToList();
                string outputName = OutputNameBox.Text.Trim();
                if (string.IsNullOrEmpty(outputName))
                {
                    UpdateOutputFilename();
                    outputName = OutputNameBox.Text.Trim();
                }

                // Strip characters invalid in filenames (also blocks quote injection into ffmpeg args and path traversal)
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    outputName = outputName.Replace(c.ToString(), "");
                }
                if (string.IsNullOrWhiteSpace(outputName)) outputName = "merged";

                if (!outputName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outputName += ".mp4";

                string? outputDir = Path.GetDirectoryName(filePaths[0]);
                if (outputDir == null) outputDir = Path.GetTempPath();
                string outputFile = Path.Combine(outputDir, outputName);

                bool fixJitter = FixJitterCheck.IsChecked == true;
                bool stabilize = StabilizeCheck.IsChecked == true;
                int stabilizeSmoothing = (int)StabilizeSmoothingSlider.Value;
                bool hevc = HevcCheck.IsChecked == true;
                bool normalize = NormalizeAudioCheck.IsChecked == true;
                bool deepCheck = DeepCheckCheck.IsChecked == true;
                bool mergeOutput = MergeOutputCheck.IsChecked == true;

                // Deep check verification if enabled
                if (deepCheck)
                {
                    for (int i = 0; i < filePaths.Count; i++)
                    {
                        StatusText.Text = string.Format(Localizer.Get("DeepChecking", Config.Language), i + 1, filePaths.Count);
                        ProgressBar.Value = (double)(i) / filePaths.Count * 20.0; // Show first 20% for deep checking

                        var check = await engine.VerifyFileAsync(filePaths[i], token);
                        if (check.IsProblem)
                        {
                            string verifyErr = string.Format(Localizer.Get("VerifyError", Config.Language), Path.GetFileName(filePaths[i]));
                            throw new MergeFailedException(verifyErr, check.Log);
                        }
                    }
                }

                // Call the processing engine
                await engine.ProcessFilesAsync(
                    filePaths,
                    outputFile,
                    resolution,
                    fixJitter,
                    stabilize,
                    hevc,
                    normalize,
                    mergeOutput,
                    stabilizeSmoothing,
                    (progress, message) =>
                    {
                        // BeginInvoke, so a burst of progress lines never blocks
                        // the ffmpeg reader thread behind the UI.
                        Dispatcher.BeginInvoke(() =>
                        {
                            // Scale progress if deep check took some time, otherwise direct
                            ProgressBar.Value = deepCheck ? 20.0 + (progress * 0.8) : progress;
                            StatusText.Text = message;
                        });
                    },
                    token);

                StatusText.Text = Localizer.Get("Ready", Config.Language);
                ProgressBar.Value = 100;
                MessageBox.Show(string.Format(Localizer.Get("MergeCompleted", Config.Language), outputFile));
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = Localizer.Get("ProcessingCancelled", Config.Language);
            }
            catch (MergeFailedException ex)
            {
                StatusText.Text = Localizer.Get("Ready", Config.Language);
                ShowFailure(ex.Message, ex.Log);
            }
            catch (Exception ex)
            {
                string errMsg = Localizer.Get("ErrorOccurred", Config.Language);
                StatusText.Text = Localizer.Get("Ready", Config.Language);
                MessageBox.Show(string.Format(errMsg, ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _processingCts?.Dispose();
                _processingCts = null;
                MergeBtn.IsEnabled = true;
                CancelBtn.Visibility = Visibility.Collapsed;
                ProgressBar.Value = 0;
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            CancelBtn.IsEnabled = false;
            _processingCts?.Cancel();
        }

        /// A failed merge, explained in terms of the user's files, with ffmpeg's
        /// own output available behind a second prompt.
        private void ShowFailure(string summary, string log)
        {
            if (string.IsNullOrWhiteSpace(log))
            {
                MessageBox.Show(this, summary, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string prompt = summary + "\n\n" + Localizer.Get("ShowLog", Config.Language) + "?";
            var answer = MessageBox.Show(this, prompt, "Error", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer != MessageBoxResult.Yes) return;

            // The log can run to thousands of repeated decoder complaints; the
            // tail is the part that says how it ended.
            const int limit = 4000;
            string shown = log.Length > limit ? "..." + log.Substring(log.Length - limit) : log;
            MessageBox.Show(this, shown, Localizer.Get("ShowLog", Config.Language),
                MessageBoxButton.OK, MessageBoxImage.None);
        }

        private List<string> GetReencodeReasons()
        {
            var reasons = new List<string>();

            // 1. Any filters active
            if (FixJitterCheck.IsChecked == true || StabilizeCheck.IsChecked == true || NormalizeAudioCheck.IsChecked == true)
            {
                reasons.Add("SlowReasonFilter");
            }
            
            // 2. Output is HEVC
            if (HevcCheck.IsChecked == true)
            {
                reasons.Add("SlowReasonHevc");
            }

            // 3. Output Resolution is forced to rescale
            if (ResolutionCombo.SelectedIndex > 0)
            {
                reasons.Add("SlowReasonRescale");
            }

            // 4. Input formats differ (Resolution or Framerate)
            if (VideoFiles.Count > 1)
            {
                bool mismatch = false;
                var first = VideoFiles[0];
                for (int i = 1; i < VideoFiles.Count; i++)
                {
                    if (VideoFiles[i].Width != first.Width ||
                        VideoFiles[i].Height != first.Height ||
                        VideoFiles[i].FrameRate != first.FrameRate)
                    {
                        mismatch = true;
                        break;
                    }
                }
                if (mismatch)
                {
                    reasons.Add("SlowReasonFormat");
                }
            }

            return reasons;
        }
    }

    public class VideoItem
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Info { get; set; }
        public double DurationSeconds { get; set; }
        public string DurationStr { get; set; } = "00:00";
        public string TagsStr { get; set; } = "";
        
        // Internal metadata details for VFR or different format warning
        public int Width { get; set; }
        public int Height { get; set; }
        public string FrameRate { get; set; } = "";
        public string CodecName { get; set; } = "";

        public static async Task<VideoItem> GetVideoDetailsAsync(string path, string ffprobePath)
        {
            var item = new VideoItem
            {
                Name = System.IO.Path.GetFileName(path),
                Path = path,
                Info = "0 MB",
                DurationSeconds = 0,
                DurationStr = "00:00",
                TagsStr = ""
            };

            string jsonOut = "";
            try
            {
                // 1. Size
                var fileInfo = new FileInfo(path);
                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                item.Info = $"{sizeMb:F1} MB";

                // 2. Probe details via ffprobe
                var p = new Process();
                p.StartInfo.FileName = ffprobePath;
                p.StartInfo.ArgumentList.Add("-v");
                p.StartInfo.ArgumentList.Add("quiet");
                p.StartInfo.ArgumentList.Add("-print_format");
                p.StartInfo.ArgumentList.Add("json");
                p.StartInfo.ArgumentList.Add("-show_format");
                p.StartInfo.ArgumentList.Add("-show_streams");
                p.StartInfo.ArgumentList.Add(path);
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                jsonOut = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();

                // Clean up the "filename" property to bypass ffprobe's multi-byte backslash escaping bug
                jsonOut = Regex.Replace(jsonOut, @"""filename""\s*:\s*"".*?""", @"""filename"": """"");

                using var doc = JsonDocument.Parse(jsonOut);
                var root = doc.RootElement;

                // 3. Format duration and size
                if (root.TryGetProperty("format", out var formatVal))
                {
                    if (formatVal.TryGetProperty("duration", out var durVal))
                    {
                        double duration = ParseDoubleSafe(durVal);
                        if (duration > 0)
                        {
                            item.DurationSeconds = duration;
                            item.DurationStr = TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss");
                        }
                    }

                    // 4. Tags Extraction (common ones: Title, Artist, Tags, etc.)
                    if (formatVal.TryGetProperty("tags", out var tagsVal) && tagsVal.ValueKind == JsonValueKind.Object)
                    {
                        var tagList = new List<string>();
                        foreach (var prop in tagsVal.EnumerateObject())
                        {
                            string key = prop.Name.ToLower();
                            if (key == "title" || key == "creation_time" || key == "artist" || key == "tag" || key == "comment" || key == "genre")
                            {
                                tagList.Add($"{prop.Name}={prop.Value.GetString()}");
                            }
                        }
                        item.TagsStr = string.Join(", ", tagList);
                    }
                }

                // 5. Streams (video resolution, codec, framerate)
                if (root.TryGetProperty("streams", out var streamsVal) && streamsVal.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stream in streamsVal.EnumerateArray())
                    {
                        if (stream.TryGetProperty("codec_type", out var typeVal) && typeVal.GetString() == "video")
                        {
                            if (stream.TryGetProperty("width", out var wVal)) item.Width = wVal.GetInt32();
                            if (stream.TryGetProperty("height", out var hVal)) item.Height = hVal.GetInt32();
                            if (stream.TryGetProperty("r_frame_rate", out var rfrVal)) item.FrameRate = rfrVal.GetString() ?? "";
                            if (stream.TryGetProperty("codec_name", out var codecVal)) item.CodecName = codecVal.GetString() ?? "";
                            
                            // If duration missing in format, get it here
                            if (item.DurationSeconds == 0 && stream.TryGetProperty("duration", out var sdVal))
                            {
                                double duration = ParseDoubleSafe(sdVal);
                                if (duration > 0)
                                {
                                    item.DurationSeconds = duration;
                                    item.DurationStr = TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss");
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ffprobe parse error: " + ex.Message);
                try
                {
                    string logPath = System.IO.Path.Combine(Core.AppPaths.EnsureDataDir(), "ffprobe_error.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now}] Path: {path}\nError: {ex}\nOutput: {jsonOut}\n\n");
                }
                catch { }
            }

            return item;
        }

        private static double ParseDoubleSafe(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.GetDouble();
            }
            if (element.ValueKind == JsonValueKind.String)
            {
                string s = element.GetString() ?? "";
                if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    return val;
                }
            }
            return 0;
        }
    }
}
