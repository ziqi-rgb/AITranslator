using System.Windows;

namespace AITranslator.View.Windows
{
    /// <summary>
    /// 批量导入时选择文件格式的窗口
    /// </summary>
    public partial class Window_SelectFormat : Window
    {
        /// <summary>
        /// 用户选择的扩展名（含点号，如 ".json"）
        /// </summary>
        public string SelectedExtension { get; private set; } = "";

        public Window_SelectFormat(Dictionary<string, int> fileCounts, string defaultExt)
        {
            InitializeComponent();

            // 设置各 Radio 的显示文本（含文件数量）
            rb_Csv.Content = $"CSV (T++)  ({fileCounts.GetValueOrDefault(".csv", 0)} 个文件)";
            rb_Json.Content = $"JSON  ({fileCounts.GetValueOrDefault(".json", 0)} 个文件)";
            rb_Srt.Content = $"SRT  ({fileCounts.GetValueOrDefault(".srt", 0)} 个文件)";
            rb_Txt.Content = $"TXT  ({fileCounts.GetValueOrDefault(".txt", 0)} 个文件)";

            // 默认选中数量最多的格式
            switch (defaultExt.ToLowerInvariant())
            {
                case ".csv": rb_Csv.IsChecked = true; break;
                case ".json": rb_Json.IsChecked = true; break;
                case ".srt": rb_Srt.IsChecked = true; break;
                case ".txt": default: rb_Txt.IsChecked = true; break;
            }
        }

        private void Radio_Checked(object sender, RoutedEventArgs e)
        {
            if (rb_Csv.IsChecked == true) SelectedExtension = ".csv";
            else if (rb_Json.IsChecked == true) SelectedExtension = ".json";
            else if (rb_Srt.IsChecked == true) SelectedExtension = ".srt";
            else if (rb_Txt.IsChecked == true) SelectedExtension = ".txt";
        }

        private void Btn_OK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SelectedExtension))
            {
                Window_Message.ShowDialog("提示", "请选择一个文件格式");
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
