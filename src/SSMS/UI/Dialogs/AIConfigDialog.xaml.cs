using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// Dialog for configuring AI API settings.
/// </summary>
public partial class AIConfigDialog : Window
{
    private readonly IConfigurationService _configService;
    private readonly ILoggerService _logger;
    private AIConfiguration _config = null!;

    public AIConfigDialog(IConfigurationService configService, ILoggerService logger)
    {
        InitializeComponent();
        _configService = configService;
        _logger = logger;

        Loaded += async (_, _) => await LoadConfigAsync();
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            _config = await _configService.GetAIConfigurationAsync();
            ApiEndpointBox.Text = _config.ApiEndpoint;
            ApiKeyBox.Password = _config.ApiKey;
            ModelNameBox.Text = _config.ModelName;
            TemperatureBox.Text = _config.DefaultTemperature.ToString();
            MaxTokensBox.Text = _config.DefaultMaxTokens.ToString();
            TimeoutBox.Text = _config.TimeoutSeconds.ToString();
            CodebuddyCliPathBox.Text = _config.CodebuddyCliPath;

            // Select formatting style
            var styleTag = _config.DefaultFormattingStyle.ToString();
            var item = FormattingStyleBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string?)i.Tag, styleTag, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals((string?)i.Tag, ((FormatStyle)_config.DefaultFormattingStyle).ToString(), StringComparison.OrdinalIgnoreCase));
            if (item != null)
                FormattingStyleBox.SelectedItem = item;
            else
                FormattingStyleBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载 AI 配置失败");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.ApiEndpoint = ApiEndpointBox.Text.Trim();
            _config.ApiKey = ApiKeyBox.Password.Trim();
            _config.ModelName = ModelNameBox.Text.Trim();
            _config.CodebuddyCliPath = CodebuddyCliPathBox.Text.Trim();

            if (double.TryParse(TemperatureBox.Text, out var temp))
                _config.DefaultTemperature = temp;
            if (int.TryParse(MaxTokensBox.Text, out var tokens))
                _config.DefaultMaxTokens = tokens;
            if (int.TryParse(TimeoutBox.Text, out var timeout))
                _config.TimeoutSeconds = timeout;

            // Save formatting style
            if (FormattingStyleBox.SelectedItem is ComboBoxItem selectedStyle
                && Enum.TryParse<FormatStyle>((string?)selectedStyle.Tag, true, out var style))
            {
                _config.DefaultFormattingStyle = style;
            }

            await _configService.SaveAIConfigurationAsync(_config);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
