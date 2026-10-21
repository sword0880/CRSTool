using System;
using System.Windows;
using System.Windows.Controls;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>
/// 设置分区稳定容器。所有子视图在窗口初始化时创建且仅切换 Visibility；
/// 隐藏分类不会丢弃待保存草稿或存储密码框内容，锁定仍由 SettingsWindow 集中清理。
/// </summary>
public sealed class SettingsSectionHost : ContentControl
{
    public static readonly DependencyProperty SectionKeyProperty=DependencyProperty.Register(
        nameof(SectionKey),typeof(string),typeof(SettingsSectionHost),new PropertyMetadata("",OnKeyChanged));

    public static readonly DependencyProperty SelectedKeyProperty=DependencyProperty.Register(
        nameof(SelectedKey),typeof(string),typeof(SettingsSectionHost),new PropertyMetadata("",OnKeyChanged));

    public string SectionKey
    {
        get => (string)GetValue(SectionKeyProperty);
        set => SetValue(SectionKeyProperty,value);
    }

    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty,value);
    }

    private static void OnKeyChanged(DependencyObject sender,DependencyPropertyChangedEventArgs args)
    {
        var host=(SettingsSectionHost)sender;
        host.Visibility=string.Equals(host.SectionKey,host.SelectedKey,StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
