using System;
using System.Collections.Generic;

namespace Mp4Merger.Core
{
    public static class Localizer
    {
        private static readonly Dictionary<string, string> JaStrings = new Dictionary<string, string>
        {
            { "AppTitle", "MP4 Merger" },
            { "Version", "バージョン 1.0" },
            { "DropZonePrompt", "ここにMP4/MOVファイルをドラッグ＆ドロップ" },
            { "DropZoneSub", "ファイルは名前順にソートされます" },
            { "OptionsHeader", "オプション" },
            { "Resolution", "解像度:" },
            { "FixJitter", "ジッター修正" },
            { "Stabilize", "ジンバル揺れ補正（スタビライズ）" },
            { "StabilizeSmoothing", "スムージング量:" },
            { "Hevc", "HEVC（高圧縮）" },
            { "NormalizeAudio", "音量ノーマライズ" },
            { "DeepCheck", "詳細検証を実行（ディープチェック）" },
            { "MergeOutput", "出力ファイルを一つに結合する" },
            { "OutputNameLabel", "出力ファイル名" },
            { "KeepOptions", "設定を保持" },
            { "WarnOnSlow", "時間がかかる場合は事前に確認する" },
            { "ClearAll", "すべてクリア" },
            { "SortByName", "名前順でソート" },
            { "MergeFiles", "ファイルを結合" },
            { "ProcessFiles", "ファイルを処理" },
            { "ColName", "名前" },
            { "ColSize", "サイズ" },
            { "ColDuration", "長さ" },
            { "ColTags", "タグ" },
            { "Ready", "準備完了" },
            { "SelectFilesWarn", "動画ファイル（MP4 / MOV）を追加してください。" },
            { "SlowWarnTitle", "警告: 処理に時間がかかる可能性があります" },
            { "SlowWarnMessage", "以下の理由により再エンコードが必要なため、処理完了までに時間がかかる可能性があります。続行しますか？\n\n理由:\n{0}" },
            { "SlowReasonFilter", "・フィルターが有効なため（手ブレ補正、音量等）" },
            { "SlowReasonFormat", "・入力ファイルの解像度やフレームレートが異なるため" },
            { "SlowReasonHevc", "・HEVC(H.265)書き出しが指定されているため" },
            { "SlowReasonRescale", "・解像度の変更（1080p/4K）が指定されているため" },
            { "CheckingFfmpeg", "FFmpegをチェック中..." },
            { "DownloadingFfmpeg", "FFmpegを自動ダウンロード中..." },
            { "DownloadedFfmpeg", "FFmpegの準備が完了しました！" },
            { "FfmpegRequired", "本アプリの動作には「FFmpeg（映像処理エンジン）」が必須です。\nダウンロードを自動で開始しますか？（約130MB）\n\n※「いいえ」を選ぶとアプリは終了します。" },
            { "FfmpegRequiredTitle", "必須コンポーネントの確認" },
            { "MergeCompleted", "処理が完了しました！\n保存先: {0}" },
            { "ProcessingProgress", "処理中: {0:F1}% (残り時間: {1})" },
            { "DeepChecking", "詳細検証中... ({0}/{1})" },
            { "ErrorOccurred", "エラーが発生しました: {0}" },
            { "VerifyError", "ファイル「{0}」が破損しているか、読み込めません。" }
        };

        private static readonly Dictionary<string, string> EnStrings = new Dictionary<string, string>
        {
            { "AppTitle", "MP4 Merger" },
            { "Version", "Version 1.0" },
            { "DropZonePrompt", "Drag & Drop MP4/MOV files here" },
            { "DropZoneSub", "Files will be sorted by name" },
            { "OptionsHeader", "Options" },
            { "Resolution", "Resolution:" },
            { "FixJitter", "Fix Jitter" },
            { "Stabilize", "Gimbal Stabilize" },
            { "StabilizeSmoothing", "Smoothing:" },
            { "Hevc", "HEVC (High Compression)" },
            { "NormalizeAudio", "Volume Normalize" },
            { "DeepCheck", "Deep Check" },
            { "MergeOutput", "Merge output files into one" },
            { "OutputNameLabel", "Output Filename" },
            { "KeepOptions", "Keep Options" },
            { "WarnOnSlow", "Warn on Slow Process" },
            { "ClearAll", "Clear All" },
            { "SortByName", "Sort by Name" },
            { "MergeFiles", "Merge Files" },
            { "ProcessFiles", "Process Files" },
            { "ColName", "Name" },
            { "ColSize", "Size" },
            { "ColDuration", "Duration" },
            { "ColTags", "Tags" },
            { "Ready", "Ready" },
            { "SelectFilesWarn", "Please add video files (MP4 / MOV) first." },
            { "SlowWarnTitle", "Warning: Process may be slow" },
            { "SlowWarnMessage", "The process might take some time because re-encoding is required for the following reasons. Do you want to continue?\n\nReasons:\n{0}" },
            { "SlowReasonFilter", "- Filters are enabled (Stabilize, Volume, etc.)" },
            { "SlowReasonFormat", "- Input files have different resolutions or framerates" },
            { "SlowReasonHevc", "- HEVC (H.265) encoding is enabled" },
            { "SlowReasonRescale", "- Output resolution changes (1080p/4K) are specified" },
            { "CheckingFfmpeg", "Checking FFmpeg..." },
            { "DownloadingFfmpeg", "Downloading FFmpeg..." },
            { "DownloadedFfmpeg", "FFmpeg setup finished!" },
            { "FfmpegRequired", "FFmpeg is required for this app to function.\nDo you want to download it automatically? (approx. 130MB)\n\nSelecting 'No' will close the app." },
            { "FfmpegRequiredTitle", "Required Component Missing" },
            { "MergeCompleted", "Processing completed!\nSaved to: {0}" },
            { "ProcessingProgress", "Processing: {0:F1}% (ETA: {1})" },
            { "DeepChecking", "Verifying files... ({0}/{1})" },
            { "ErrorOccurred", "An error occurred: {0}" },
            { "VerifyError", "File \"{0}\" is corrupted or cannot be read." }
        };

        public static string Get(string key, string lang)
        {
            var dict = (lang == "en") ? EnStrings : JaStrings;
            if (dict.TryGetValue(key, out string? value))
            {
                return value;
            }
            return key;
        }
    }
}
