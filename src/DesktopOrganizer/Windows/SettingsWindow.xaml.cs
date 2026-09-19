using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;
using Wf = System.Windows.Forms;

namespace DesktopOrganizer.Windows;

/// <summary>全局设置对话框。点「保存」时才会把界面上的值写回配置。</summary>
public partial class SettingsWindow : Window
{
    private static readonly double[] IconSizeOptions = { 32, 40, 48, 64 };
    private static readonly double[] OpacityOptions = { 0.62, 0.78, 0.90, 1.0 };

    private RadioButton[] _iconSizeButtons = Array.Empty<RadioButton>();
    private RadioButton[] _opacityButtons = Array.Empty<RadioButton>();
    private RadioButton[] _themeButtons = Array.Empty<RadioButton>();
    private RadioButton[] _sortButtons = Array.Empty<RadioButton>();

    public SettingsWindow()
    {
        InitializeComponent();

        BuildSegments();
        LoadFromConfig();

        Loaded += (_, _) =>
        {
            HotkeyBox.Focus();
            HotkeyBox.SelectAll();
        };
    }

    // ================================================================ 构建分段控件

    private void BuildSegments()
    {
        _iconSizeButtons = BuildSegment(IconSizeHost, "IconSize", new (string, object)[]
        {
            ("小", 32d),
            ("中", 40d),
            ("大", 48d),
            ("特大", 64d),
        });

        _opacityButtons = BuildSegment(OpacityHost, "PanelOpacity", new (string, object)[]
        {
            ("通透", 0.62d),
            ("标准", 0.78d),
            ("清晰", 0.90d),
            ("不透明", 1.0d),
        });

        _themeButtons = BuildSegment(ThemeHost, "Appearance", new (string, object)[]
        {
            ("深色", ZoneAppearance.Dark),
            ("浅色", ZoneAppearance.Light),
        });

        _sortButtons = BuildSegment(SortHost, "DefaultSort", new (string, object)[]
        {
            ("名称", ZoneSortMode.Name),
            ("修改时间", ZoneSortMode.DateModified),
            ("大小", ZoneSortMode.Size),
            ("类型", ZoneSortMode.Type),
        });
    }

    private static RadioButton[] BuildSegment(Panel host, string groupName, (string Label, object Value)[] options)
    {
        host.Children.Clear();
        var buttons = new List<RadioButton>();

        foreach (var (label, value) in options)
        {
            var radio = new RadioButton
            {
                Style = (Style)Application.Current.FindResource("SegmentedRadio"),
                Content = label,
                Tag = value,
                GroupName = groupName,
                Margin = new Thickness(0, 0, 6, 0),
            };

            host.Children.Add(radio);
            buttons.Add(radio);
        }

        return buttons.ToArray();
    }

    private static void SelectValue(IEnumerable<RadioButton> buttons, object? value)
    {
        foreach (var button in buttons)
        {
            button.IsChecked = button.Tag != null && button.Tag.Equals(value);
        }
    }

    private static object? GetSelected(IEnumerable<RadioButton> buttons)
    {
        foreach (var button in buttons)
        {
            if (button.IsChecked == true)
            {
                return button.Tag;
            }
        }

        return null;
    }

    private static double Nearest(double[] options, double value)
    {
        var best = options[0];
        foreach (var option in options)
        {
            if (Math.Abs(option - value) < Math.Abs(best - value))
            {
                best = option;
            }
        }

        return best;
    }

    // ================================================================ 读写配置

    private void LoadFromConfig()
    {
        var config = ZoneManager.Instance.Config;

        RootFolderBox.Text = string.IsNullOrWhiteSpace(config.RootFolder)
            ? ConfigService.DefaultRootFolder
            : config.RootFolder;

        HotkeyBox.Text = config.Hotkey;

        SelectValue(_iconSizeButtons, Nearest(IconSizeOptions, config.IconSize));
        SelectValue(_opacityButtons, Nearest(OpacityOptions, config.PanelOpacity));
        SelectValue(_themeButtons, config.Appearance);
        SelectValue(_sortButtons, config.DefaultSortMode);

        StartupCheck.IsChecked = config.StartWithWindows;
        TopmostCheck.IsChecked = config.AlwaysOnTop;
        SnapCheck.IsChecked = config.SnapEnabled;
        LockCheck.IsChecked = config.LayoutLocked;
        HiddenCheck.IsChecked = config.ShowHiddenFiles;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var root = RootFolderBox.Text.Trim();

        if (string.IsNullOrEmpty(root))
        {
            ZoneManager.ShowError("分区根目录不能为空。");
            return;
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            ZoneManager.ShowError($"这个目录没法用：\n{root}\n\n{ex.Message}");
            return;
        }

        var hotkey = HotkeyBox.Text.Trim();
        if (!HotkeyService.TryParseGesture(hotkey, out _, out _))
        {
            ZoneManager.ShowError(
                "快捷键格式不对。\n\n请写成「修饰键 + 主键」的组合，例如：\nCtrl+Alt+D\nWin+Shift+F2");
            return;
        }

        var config = ZoneManager.Instance.Config;

        config.RootFolder = root;
        config.Hotkey = hotkey;

        if (GetSelected(_iconSizeButtons) is double iconSize)
        {
            config.IconSize = iconSize;
        }

        if (GetSelected(_opacityButtons) is double opacity)
        {
            config.PanelOpacity = opacity;
        }

        if (GetSelected(_themeButtons) is ZoneAppearance appearance)
        {
            config.Appearance = appearance;
        }

        if (GetSelected(_sortButtons) is ZoneSortMode sortMode)
        {
            config.DefaultSortMode = sortMode;
        }

        config.StartWithWindows = StartupCheck.IsChecked == true;
        config.AlwaysOnTop = TopmostCheck.IsChecked == true;
        config.SnapEnabled = SnapCheck.IsChecked == true;
        config.LayoutLocked = LockCheck.IsChecked == true;
        config.ShowHiddenFiles = HiddenCheck.IsChecked == true;

        ZoneManager.Instance.SaveNow();
        DialogResult = true;
    }

    // ================================================================ 按钮

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new Wf.FolderBrowserDialog
            {
                Description = "选择存放各分区文件夹的根目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                AutoUpgradeEnabled = true,
            };

            var current = RootFolderBox.Text.Trim();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }

            if (dialog.ShowDialog() == Wf.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                RootFolderBox.Text = dialog.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            ZoneManager.ShowError($"打开文件夹选择器失败：\n{ex.Message}");
        }
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var root = RootFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        ZoneFolderService.EnsureFolder(root);
        ZoneFolderService.ShellOpen(root);
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.ConfigDirectory);
        }
        catch
        {
            // ignore
        }

        ZoneFolderService.ShellOpen(ConfigService.ConfigDirectory);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        SelectValue(_iconSizeButtons, 48d);
        SelectValue(_opacityButtons, 0.78d);
        SelectValue(_themeButtons, ZoneAppearance.Dark);
        SelectValue(_sortButtons, ZoneSortMode.Name);

        HotkeyBox.Text = "Ctrl+Alt+D";

        StartupCheck.IsChecked = false;
        TopmostCheck.IsChecked = true;
        SnapCheck.IsChecked = true;
        LockCheck.IsChecked = false;
        HiddenCheck.IsChecked = false;
    }

    // ================================================================ 窗口行为

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignore
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
