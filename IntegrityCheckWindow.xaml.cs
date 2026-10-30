using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Mp4Merger.Core;

namespace Mp4Merger
{
    public partial class IntegrityCheckWindow : Window
    {
        private readonly IntegrityCheckModel _model;
        private readonly string _lang;

        public IntegrityCheckWindow(string language)
        {
            InitializeComponent();
            _lang = language;

            _model = new IntegrityCheckModel(AppPaths.ResolveTool("ffmpeg"), AppPaths.ResolveTool("ffprobe"))
            {
                Language = language
            };
            _model.PropertyChanged += Model_PropertyChanged;
            _model.Results.CollectionChanged += (_, _) => RefreshResults();
            _model.RunningFiles.CollectionChanged += (_, _) => RefreshRunningFiles();

            ResultsList.ItemsSource = _model.Results;

            ApplyLanguage();
            RefreshAll();
        }

        // MARK: - Language

        private void ApplyLanguage()
        {
            Title = Localizer.Get("CheckModeTitle", _lang);
            DropPromptTxt.Text = Localizer.Get("DropToCheck", _lang);
            DropSubTxt.Text = Localizer.Get("DropToCheckSub", _lang);
            ChooseBtn.Content = Localizer.Get("ChooseFilesOrFolders", _lang);
            ClearBtn.Content = Localizer.Get("ClearAll", _lang);
            CheckLevelHeader.Text = Localizer.Get("CheckLevel", _lang);
            LevelQuick.Content = Localizer.Get("CheckLevelQuick", _lang);
            LevelStandard.Content = Localizer.Get("CheckLevelStandard", _lang);
            LevelFull.Content = Localizer.Get("CheckLevelFull", _lang);
            SubfoldersCheck.Content = Localizer.Get("IncludeSubfolders", _lang);
            ScanningTxt.Text = Localizer.Get("Scanning", _lang);
            ResultsPlaceholder.Text = Localizer.Get("ReadyToCheck", _lang);
            StartCheckBtn.Content = Localizer.Get("StartCheck", _lang);
            CancelCheckBtn.Content = Localizer.Get("CancelProcessing", _lang);
        }

        // MARK: - Model plumbing

        private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshAll();

        private void RefreshAll()
        {
            bool loaded = _model.SourceLabel.Length > 0;
            DropZone.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            SourceInfoPanel.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            ClearBtn.Visibility = loaded && !_model.IsChecking ? Visibility.Visible : Visibility.Collapsed;

            SourceLabelTxt.Text = _model.SourceLabel;
            SourceSummaryTxt.Text = _model.FoundNothing
                ? Localizer.Get("NoVideoFilesFound", _lang)
                : _model.SummaryText;
            ScanningTxt.Visibility = _model.IsScanning ? Visibility.Visible : Visibility.Collapsed;

            LevelDetailTxt.Text = _model.LevelDetail;
            EstimateTxt.Text = _model.EstimatedText;
            SubfoldersCheck.Visibility = _model.HasFolderInput ? Visibility.Visible : Visibility.Collapsed;
            OptionsPanel.IsEnabled = !_model.IsChecking;
            ChooseBtn.IsEnabled = !_model.IsChecking;

            bool showProgress = _model.IsChecking || _model.Progress > 0;
            ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            CheckProgressBar.Value = _model.Progress;
            RemainingTxt.Text = _model.RemainingText;

            StartCheckBtn.Visibility = _model.IsChecking ? Visibility.Collapsed : Visibility.Visible;
            StartCheckBtn.IsEnabled = _model.CanStart;
            CancelCheckBtn.Visibility = _model.IsChecking ? Visibility.Visible : Visibility.Collapsed;
            CancelCheckBtn.IsEnabled = _model.IsChecking;

            ErrorTxt.Text = _model.ErrorMessage ?? "";
            ErrorTxt.Visibility = _model.HasError ? Visibility.Visible : Visibility.Collapsed;

            RefreshResults();
            RefreshRunningFiles();
        }

        private void RefreshResults()
        {
            bool any = _model.Results.Count > 0;
            ResultsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ResultsPlaceholder.Visibility = !any && _model.HasTargets && !_model.IsChecking
                ? Visibility.Visible
                : Visibility.Collapsed;

            SummaryChips.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            OkCountTxt.Text = $"{Localizer.Get("StatusOk", _lang)} {_model.OkCount}";
            WarnCountTxt.Text = $"{Localizer.Get("StatusSuspect", _lang)} {_model.WarningCount}";
            BrokenCountTxt.Text = $"{Localizer.Get("StatusDamaged", _lang)} {_model.BrokenCount}";

            var problems = _model.ProblemPaths;
            RevealProblemsBtn.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RevealProblemsBtn.Content = Localizer.GetDynamic("RevealProblemFiles", _lang, problems.Count.ToString());
        }

        private void RefreshRunningFiles()
        {
            CurrentFilesTxt.Text = _model.RunningFiles.Count > 0
                ? string.Join(", ", _model.RunningFiles)
                : (_model.IsChecking ? Localizer.Get("CheckingProgress", _lang) : "");
        }

        // MARK: - Input

        private void DropTarget_PreviewDragOver(object sender, DragEventArgs e)
        {
            bool accept = !_model.IsChecking && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropTarget_Drop(object sender, DragEventArgs e)
        {
            if (_model.IsChecking) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            _model.Load(paths);
        }

        private void ChooseBtn_Click(object sender, RoutedEventArgs e)
        {
            // One dialog that takes both: picking a folder and picking files are
            // the same action here, and the user should not have to choose first.
            var dialog = new OpenFileDialog
            {
                Title = Localizer.Get("ChooseFilesOrFolders", _lang),
                Multiselect = true,
                CheckFileExists = false,
                Filter = "Video files (*.mp4;*.mov;*.m4v)|*.mp4;*.mov;*.m4v|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            {
                _model.Load(dialog.FileNames);
                return;
            }

            // Offer the folder picker as a second step, so a folder is still reachable.
            var folderDialog = new OpenFolderDialog
            {
                Title = Localizer.Get("ChooseFilesOrFolders", _lang),
                Multiselect = true
            };
            if (folderDialog.ShowDialog(this) == true && folderDialog.FolderNames.Length > 0)
            {
                _model.Load(folderDialog.FolderNames);
            }
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e) => _model.Clear();

        private void Level_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            if (LevelQuick.IsChecked == true) _model.Level = CheckLevel.Quick;
            else if (LevelStandard.IsChecked == true) _model.Level = CheckLevel.Standard;
            else _model.Level = CheckLevel.Full;
        }

        private void Subfolders_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            _model.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        }

        // MARK: - Running

        private async void StartCheck_Click(object sender, RoutedEventArgs e)
        {
            await _model.StartAsync();
        }

        private void CancelCheck_Click(object sender, RoutedEventArgs e)
        {
            CancelCheckBtn.IsEnabled = false;
            _model.Cancel();
        }

        // MARK: - Revealing files

        private void RevealProblems_Click(object sender, RoutedEventArgs e) => Reveal(_model.ProblemPaths);

        private void RevealOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { CommandParameter: string path }) Reveal(new List<string> { path });
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is CheckResult result) Reveal(new List<string> { result.Path });
        }

        /// Opens Explorer with the files selected. Files in different folders
        /// need one window each, so they are grouped by folder first.
        private static void Reveal(IReadOnlyList<string> paths)
        {
            foreach (var group in paths.Where(File.Exists)
                                       .GroupBy(p => Path.GetDirectoryName(p) ?? "")
                                       .Take(8))
            {
                // /select takes a single file; the rest of the folder's files are
                // shown alongside it anyway.
                string first = group.First();
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"")
                    {
                        UseShellExecute = true
                    });
                }
                catch { /* Explorer unavailable; nothing useful to report */ }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _model.Cancel();
            base.OnClosing(e);
        }
    }

    // MARK: - Converters

    public sealed class StatusBrushConverter : IValueConverter
    {
        private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        private static readonly Brush Broken = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            CheckStatus.Ok => Ok,
            CheckStatus.Warning => Warning,
            _ => Broken
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class StatusGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            CheckStatus.Ok => "✓",
            CheckStatus.Warning => "!",
            _ => "✕"
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class HasTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            !string.IsNullOrWhiteSpace(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
