using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.UI.Dialogs
{
    /// <summary>
    /// SQL 模板设置对话框。
    /// </summary>
    public partial class SqlTemplateConfigDialog : Window
    {
        private readonly SqlTemplateService _templateService;
        private ObservableCollection<SqlTemplateViewModel> _templates = new();

        public SqlTemplateConfigDialog(SqlTemplateService templateService)
        {
            InitializeComponent();
            _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));

            TemplateGrid.ItemsSource = _templates;
            LoadTemplates();

            AddButton.Click += OnAddClick;
            DeleteButton.Click += OnDeleteClick;
            RestoreButton.Click += OnRestoreClick;
            TemplateGrid.SelectionChanged += OnSelectionChanged;
        }

        private void LoadTemplates()
        {
            _templates.Clear();
            foreach (var t in _templateService.GetAllTemplates())
            {
                _templates.Add(new SqlTemplateViewModel
                {
                    Abbreviation = t.Abbreviation ?? "",
                    Expansion = t.Expansion ?? ""
                });
            }
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var abbr = AbbreviationBox.Text.Trim();
            var expansion = ExpansionBox.Text;

            if (string.IsNullOrWhiteSpace(abbr))
            {
                MessageBox.Show("请输入缩写。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(expansion))
            {
                MessageBox.Show("请输入展开内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _templateService.AddOrUpdate(abbr, expansion);
            LoadTemplates();

            // 选中刚添加的
            var added = _templates.FirstOrDefault(t =>
                string.Equals(t.Abbreviation, abbr, StringComparison.OrdinalIgnoreCase));
            if (added != null)
            {
                TemplateGrid.SelectedItem = added;
            }

            AbbreviationBox.Clear();
            ExpansionBox.Clear();
            AbbreviationBox.Focus();
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (TemplateGrid.SelectedItem is SqlTemplateViewModel selected)
            {
                var result = MessageBox.Show(
                    $"确认删除缩写 '{selected.Abbreviation}'？",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _templateService.Delete(selected.Abbreviation);
                    LoadTemplates();
                    AbbreviationBox.Clear();
                    ExpansionBox.Clear();
                }
            }
        }

        private void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "恢复默认模板将覆盖当前所有自定义模板，是否继续？",
                "恢复默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 删除文件后重新加载
                try
                {
                    var path = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "sql_templates.json");

                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch { }

                _templateService.Load();
                LoadTemplates();
            }
        }

        private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TemplateGrid.SelectedItem is SqlTemplateViewModel selected)
            {
                AbbreviationBox.Text = selected.Abbreviation;
                ExpansionBox.Text = selected.Expansion;
            }
        }
    }

    /// <summary>
    /// 模板列表的 ViewModel。
    /// </summary>
    public class SqlTemplateViewModel
    {
        public string Abbreviation { get; set; } = "";
        public string Expansion { get; set; } = "";
    }
}
