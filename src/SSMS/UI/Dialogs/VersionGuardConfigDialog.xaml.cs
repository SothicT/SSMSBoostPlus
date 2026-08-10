using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// Dialog for configuring Version Guard settings.
/// </summary>
public partial class VersionGuardConfigDialog : Window
{
    private readonly IConfigurationService _configService;
    private readonly ILoggerService _logger;
    private VersionGuardConfiguration _config = null!;

    public VersionGuardConfigDialog(IConfigurationService configService, ILoggerService logger)
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
            _config = await _configService.GetVersionGuardConfigurationAsync();
            EnabledCheckBox.IsChecked = _config.Enabled;
            SkipTokenBox.Text = _config.SkipToken;
            AskTokenBox.Text = _config.AskToken;
            CaseSensitiveCheckBox.IsChecked = _config.TokenCaseSensitive;
            AutoRefreshCheckBox.IsChecked = _config.AutoRefreshOnMismatch;

            // Set default action radio button
            switch (_config.DefaultAction)
            {
                case GuardAction.StopExecution:
                    ActionStop.IsChecked = true;
                    break;
                case GuardAction.AskUser:
                    ActionAsk.IsChecked = true;
                    break;
                case GuardAction.LogOnly:
                    ActionLog.IsChecked = true;
                    break;
            }

            ExcludedDatabasesBox.Text = string.Join(", ", _config.ExcludedDatabases);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载版本守卫配置失败");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.Enabled = EnabledCheckBox.IsChecked == true;
            _config.SkipToken = SkipTokenBox.Text.Trim();
            _config.AskToken = AskTokenBox.Text.Trim();
            _config.TokenCaseSensitive = CaseSensitiveCheckBox.IsChecked == true;
            _config.AutoRefreshOnMismatch = AutoRefreshCheckBox.IsChecked == true;

            if (ActionStop.IsChecked == true) _config.DefaultAction = GuardAction.StopExecution;
            else if (ActionAsk.IsChecked == true) _config.DefaultAction = GuardAction.AskUser;
            else if (ActionLog.IsChecked == true) _config.DefaultAction = GuardAction.LogOnly;

            _config.ExcludedDatabases = ExcludedDatabasesBox.Text
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            await _configService.SaveVersionGuardConfigurationAsync(_config);
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
