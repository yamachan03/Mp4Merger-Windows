using System;
using System.Windows;
using System.Windows.Controls;
using Mp4Merger.Core;

namespace Mp4Merger
{
    public partial class SettingsWindow : Window
    {
        public AppConfig Config { get; private set; }

        public SettingsWindow(AppConfig currentConfig)
        {
            InitializeComponent();
            Config = currentConfig;
            
            // Set selections
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (item.Tag?.ToString() == Config.Language)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }

            foreach (ComboBoxItem item in ThemeCombo.Items)
            {
                if (item.Tag?.ToString() == Config.Theme)
                {
                    ThemeCombo.SelectedItem = item;
                    break;
                }
            }

            UpdateTexts();
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageCombo == null) return;
            UpdateTexts();
        }

        private void UpdateTexts()
        {
            if (LanguageCombo == null || SettingsTitle == null || LanguageLabel == null || 
                ThemeLabel == null || ThemeDarkItem == null || ThemeLightItem == null || 
                ThemeAutoItem == null || SaveBtn == null) return;

            var selectedItem = LanguageCombo.SelectedItem as ComboBoxItem;
            string lang = selectedItem?.Tag?.ToString() ?? "ja";

            if (lang == "ja")
            {
                Title = "設定";
                SettingsTitle.Text = "設定";
                LanguageLabel.Text = "言語:";
                ThemeLabel.Text = "テーマ:";
                ThemeDarkItem.Content = "ダークモード";
                ThemeLightItem.Content = "ライトモード";
                ThemeAutoItem.Content = "自動切り替え (時間帯)";
                SaveBtn.Content = "保存";
            }
            else
            {
                Title = "Settings";
                SettingsTitle.Text = "Settings";
                LanguageLabel.Text = "Language:";
                ThemeLabel.Text = "Theme:";
                ThemeDarkItem.Content = "Dark Mode";
                ThemeLightItem.Content = "Light Mode";
                ThemeAutoItem.Content = "Auto (Time of Day)";
                SaveBtn.Content = "Save";
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedLang = LanguageCombo.SelectedItem as ComboBoxItem;
            var selectedTheme = ThemeCombo.SelectedItem as ComboBoxItem;

            Config.Language = selectedLang?.Tag?.ToString() ?? "ja";
            Config.Theme = selectedTheme?.Tag?.ToString() ?? "dark";
            Config.Save();

            DialogResult = true;
            Close();
        }
    }
}
