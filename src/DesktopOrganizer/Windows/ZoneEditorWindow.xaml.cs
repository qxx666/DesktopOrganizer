using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;
using Wf = System.Windows.Forms;

namespace DesktopOrganizer.Windows;

/// <summary>新建分区 / 修改分区设置的对话框。</summary>
public partial class ZoneEditorWindow : Window
{
    private static readonly (string Label, string Hex)[] PresetColors =
    {
        ("蓝色", "#4C8DFF"),
        ("绿色", "#2ECC9B"),
        ("橙色", "#F5A623"),
        ("红色", "#FF6B6B"),
        ("紫色", "#A07BFF"),
        ("青色", "#22C3D6"),
        ("粉色", "#F06292"),
        ("石墨", "#6B7A90"),
    };

    private readonly ZoneConfig? _existing;
    private readonly bool _isEditMode;
    private readonly ZoneSortMode _defaultSortMode;

    private string _selectedAccent;
    private bool _initialized;
    private bool _folderManuallyEdited;
    private bool _suppressFolderSync;

    /// <summary>确定后返回的分区配置；取消时为 null。</summary>
    public ZoneConfig? Result { get; private set; }

    public ZoneEditorWindow(ZoneConfig? existing, ZoneSortMode defaultSortMode = ZoneSortMode.Name)
    {
        _existing = existing;
        _isEditMode = existing != null;
        _defaultSortMode = defaultSortMode;
        _selectedAccent = existing?.Accent ?? PresetColors[0].Hex;

        InitializeComponent();
        BuildSwatches();

        if (_isEditMode && existing != null)
        {
            HeaderText.Text = "分区设置";
            BtnConfirm.Content = "保存";
            FolderHint.Text = "改文件夹只是让这个分区框指向新位置，已经存在的文件不会被搬走。";
            NameBox.Text = existing.Name;
            FolderBox.Text = existing.FolderPath;
            _folderManuallyEdited = true;
        }
        else
        {
            HeaderText.Text = "新建分区";
            BtnConfirm.Content = "创建";
            FolderHint.Text = "拖进分区的文件会被真实移动到上面这个文件夹里。";
            NameBox.Text = SuggestName();
            FolderBox.Text = SuggestFolder(NameBox.Text);
        }

        _initialized = true;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            Validate();
        };
    }

    // ================================================================ 初始化

    private void BuildSwatches()
    {
        SwatchHost.Children.Clear();

        foreach (var (label, hex) in PresetColors)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("SwatchRadio"),
                Background = new SolidColorBrush(ParseHex(hex)),
                ToolTip = label,
                Tag = hex,
                GroupName = "ZoneAccent",
                IsChecked = string.Equals(hex, _selectedAccent, StringComparison.OrdinalIgnoreCase),
            };

            swatch.Checked += (sender, _) =>
            {
                if (sender is RadioButton { Tag: string value })
                {
                    _selectedAccent = value;
                }
            };

            SwatchHost.Children.Add(swatch);
        }
    }

    private static string SuggestName()
    {
        var existing = ZoneManager.Instance.Config.Zones;
        for (var index = 1; index < 200; index++)
        {
            var candidate = index == 1 ? "新分区" : $"新分区 {index}";
            if (!existing.Any(z => string.Equals(z.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return "新分区";
    }

    private static string SuggestFolder(string zoneName)
    {
        try
        {
            return ZoneFolderService.BuildZoneFolder(ZoneManager.Instance.RootFolder, zoneName);
        }
        catch
        {
            return Path.Combine(ZoneManager.Instance.RootFolder, ZoneFolderService.SanitizeFolderName(zoneName));
        }
    }

    // ================================================================ 输入联动

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, FolderBox) && !_suppressFolderSync)
        {
            _folderManuallyEdited = true;
        }

        // 新建分区时，文件夹跟着名称自动走（用户手动改过就不再覆盖）
        if (!_isEditMode && !_folderManuallyEdited && ReferenceEquals(sender, NameBox))
        {
            _suppressFolderSync = true;
            FolderBox.Text = SuggestFolder(NameBox.Text.Trim());
            _suppressFolderSync = false;
        }

        Validate();
    }

    private void Validate()
    {
        if (!_initialized)
        {
            return;
        }

        var name = NameBox.Text.Trim();
        var folder = FolderBox.Text.Trim();
        var problems = new List<string>();

        if (string.IsNullOrEmpty(name))
        {
            problems.Add("请填写分区名称。");
        }
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problems.Add(@"名称里不能包含 \ / : * ? "" < > | 这些字符。");
        }

        if (string.IsNullOrEmpty(folder))
        {
            problems.Add("请选择分区对应的文件夹。");
        }
        else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            problems.Add("文件夹路径不合法。");
        }
        else
        {
            var clash = ZoneManager.Instance.Config.Zones.FirstOrDefault(z =>
                !ReferenceEquals(z, _existing) && ZoneFolderService.IsSameFolder(z.FolderPath, folder));

            if (clash != null)
            {
                problems.Add($"这个文件夹已经被分区「{clash.Name}」占用了，每个分区需要各自的文件夹。");
            }
        }

        WarningText.Text = string.Join("\n", problems);
        WarningText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnConfirm.IsEnabled = problems.Count == 0;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new Wf.FolderBrowserDialog
            {
                Description = "选择这个分区对应的文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                AutoUpgradeEnabled = true,
            };

            var current = FolderBox.Text.Trim();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }
            else
            {
                var root = ZoneManager.Instance.RootFolder;
                ZoneFolderService.EnsureFolder(root);
                dialog.SelectedPath = root;
            }

            if (dialog.ShowDialog() == Wf.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                _suppressFolderSync = true;
                FolderBox.Text = dialog.SelectedPath;
                _suppressFolderSync = false;
                _folderManuallyEdited = true;
                Validate();
            }
        }
        catch (Exception ex)
        {
            ZoneManager.ShowError($"打开文件夹选择器失败：\n{ex.Message}");
        }
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
            // 鼠标已经松开时会抛异常，忽略
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

        if (e.Key == Key.Enter && BtnConfirm.IsEnabled)
        {
            Confirm_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var folder = FolderBox.Text.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder))
        {
            Validate();
            return;
        }

        if (_isEditMode && _existing != null)
        {
            // 直接改在原有对象上，分区窗口持有的引用保持有效
            _existing.Name = name;
            _existing.FolderPath = folder;
            _existing.Accent = _selectedAccent;
            Result = _existing;
        }
        else
        {
            Result = new ZoneConfig
            {
                Name = name,
                FolderPath = folder,
                Accent = _selectedAccent,
                SortMode = _defaultSortMode,
            };
        }

        DialogResult = true;
    }

    private static Color ParseHex(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
            {
                return color;
            }
        }
        catch
        {
            // ignore
        }

        return Colors.Gray;
    }
}
