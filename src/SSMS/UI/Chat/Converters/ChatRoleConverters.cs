using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.SSMS.UI.Chat.Converters;

/// <summary>
/// 将 ChatRole 转换为 WPF HorizontalAlignment。
/// User → Right（蓝底靠右），Assistant → Left（灰底靠左）。
/// </summary>
public class ChatRoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ChatRole role)
        {
            return role == ChatRole.User
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
        }
        return HorizontalAlignment.Left;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 将 ChatRole 转换为消息气泡背景色画刷。
/// User → 蓝色 #DBEAFE，Assistant → 灰色 #F3F4F6。
/// </summary>
public class ChatRoleToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ChatRole role)
        {
            return role == ChatRole.User
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6));
        }
        return System.Windows.Media.Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 将 ChatRole 转换为 Visibility：User 角色 → Visible（显示代码折叠区），其他 → Collapsed。
/// </summary>
public class ChatRoleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ChatRole role && role == ChatRole.User
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 将字符串转换为 Visibility：非空 → Visible，空/null → Collapsed。
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}