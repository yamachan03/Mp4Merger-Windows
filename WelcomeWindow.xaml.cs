using System;
using System.Windows;
using System.Windows.Controls;
using Mp4Merger.Core;

namespace Mp4Merger
{
    public partial class WelcomeWindow : Window
    {
        public AppConfig Config { get; private set; } = new AppConfig();

        public WelcomeWindow()
        {
            InitializeComponent();
            UpdateTexts();
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageCombo == null) return;
            UpdateTexts();
        }

        private void UpdateTexts()
        {
            if (LanguageCombo == null || ThemeLabel == null || ThemeDarkItem == null || 
                ThemeLightItem == null || ThemeAutoItem == null || StartBtn == null) return;

            var selectedItem = LanguageCombo.SelectedItem as ComboBoxItem;
            string lang = selectedItem?.Tag?.ToString() ?? "ja";

            if (lang == "ja")
            {
                Title = "初期設定 - MP4 Merger";
                ThemeLabel.Text = "テーマ:";
                ThemeDarkItem.Content = "ダークモード";
                ThemeLightItem.Content = "ライトモード";
                ThemeAutoItem.Content = "自動切り替え（時間帯）";
                StartBtn.Content = "開始する";
            }
            else
            {
                Title = "Initial Setup - MP4 Merger";
                ThemeLabel.Text = "Theme:";
                ThemeDarkItem.Content = "Dark Mode";
                ThemeLightItem.Content = "Light Mode";
                ThemeAutoItem.Content = "Auto (Time of Day)";
                StartBtn.Content = "Get Started";
            }
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedLang = LanguageCombo.SelectedItem as ComboBoxItem;
            var selectedTheme = ThemeCombo.SelectedItem as ComboBoxItem;

            Config.Language = selectedLang?.Tag?.ToString() ?? "ja";
            Config.Theme = selectedTheme?.Tag?.ToString() ?? "dark";
            Config.KeepOptions = false;
            Config.WarnOnSlow = false;

            Config.Save();

            DialogResult = true;
            Close();
        }
    }
}
