using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;

namespace DesktopOrganizer.Windows;

public partial class ZoneWindow : Window
{
    private const double CollapsedHeight = 36;
    private const double MinZoneWidth = 190;
    private const double MinZoneHeight = 110;
    private const double SnapThreshold = 10;

    // 名称用的画刷与主题绑定，只有两套。缓存并冻结后不再每次刷新都新建
    // （未冻结的画刷会被 WPF 挂上变更通知，条目多时是一笔不小的常驻开销）。
    private static readonly SolidColorBrush DarkNameBrush = CreateFrozenBrush(0xE6, 0xEB, 0xF3);
    private static readonly SolidColorBrush LightNameBrush = CreateFrozenBrush(0x25, 0x2C, 0x38);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly ZoneConfig _zone;
    private readonly DispatcherTimer _watcherDebounce;

    private FileSystemWatcher? _watcher;
    private bool _isClosing;

    // ---- 拖动分区 ----
    private bool _isDraggingZone;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;

    // ---- 拖动条目 ----
    private FileItemViewModel? _pendingDragItem;
    private Point _itemDragOrigin;

    // ---- 右键菜单 ----
    private FileItemViewModel? _contextItem;

    public ZoneWindow(ZoneConfig zone)
    {
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));

        InitializeComponent();

        // 桌面悬浮面板不应该抢走当前窗口的焦点
        ShowActivated = false;

        _watcherDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _watcherDebounce.Tick += (_, _) =>
        {
            _watcherDebounce.Stop();
            RefreshItems();
        };

        ApplyLayoutFromConfig();
        ApplyAppearance();
        UpdateCollapseVisual();
        SetupWatcher();

        Loaded += (_, _) => RefreshItems();
        Closed += OnClosedInternal;
    }

    public ZoneConfig Config => _zone;

    // ================================================================ 布局

    private void ApplyLayoutFromConfig()
    {
        Width = Math.Max(MinZoneWidth, _zone.Width);
        Height = _zone.Collapsed ? CollapsedHeight : Math.Max(MinZoneHeight, _zone.Height);

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var x = double.IsNaN(_zone.X) ? virtualLeft + 80 : _zone.X;
        var y = double.IsNaN(_zone.Y) ? virtualTop + 80 : _zone.Y;

        // 保证分区不会被拖到完全看不见的地方
        x = Math.Min(Math.Max(x, virtualLeft - Width + 70), virtualRight - 70);
        y = Math.Min(Math.Max(y, virtualTop), virtualBottom - CollapsedHeight);

        Left = x;
        Top = y;

        // 位置被修正过就写回配置
        if (Math.Abs(x - _zone.X) > 0.5 || Math.Abs(y - _zone.Y) > 0.5)
        {
            _zone.X = x;
            _zone.Y = y;
            ZoneManager.Instance.SaveDebounced();
        }
    }

    private void PersistLayout()
    {
        if (_isClosing)
        {
            return;
        }

        _zone.Width = Width;
        if (!_zone.Collapsed)
        {
            _zone.Height = Height;
        }

        _zone.X = Left;
        _zone.Y = Top;

        ZoneManager.Instance.SaveDebounced();
    }

    // ================================================================ 外观

    public void ApplyAppearance()
    {
        var config = ZoneManager.Instance.Config;
        var accent = ParseColor(_zone.Accent, Color.FromRgb(0x4C, 0x8D, 0xFF));
        var dark = config.Appearance == ZoneAppearance.Dark;
        var opacity = Math.Clamp(config.PanelOpacity, 0.30, 1.0);

        var accentAlpha = (byte)Math.Clamp(0x50 + (opacity - 0.3) * 90, 0x40, 0xB0);

        RootPanel.Background = new SolidColorBrush(WithAlpha(
            dark ? Color.FromRgb(0x1C, 0x20, 0x28) : Color.FromRgb(0xF8, 0xF9, 0xFC), opacity));

        RootPanel.BorderBrush = new SolidColorBrush(WithAlpha(accent, 0x60));

        HeaderBar.Background = new SolidColorBrush(WithAlpha(accent, accentAlpha));
        HeaderBar.CornerRadius = _zone.Collapsed
            ? new CornerRadius(11)
            : new CornerRadius(11, 11, 0, 0);

        AccentDot.Fill = new SolidColorBrush(accent);

        TitleText.Text = _zone.Name;
        TitleText.Foreground = new SolidColorBrush(
            dark ? Color.FromRgb(0xF2, 0xF5, 0xFA) : Color.FromRgb(0x16, 0x1B, 0x25));

        CountText.Foreground = new SolidColorBrush(
            dark ? Color.FromRgb(0x9F, 0xB0, 0xC8) : Color.FromRgb(0x5A, 0x64, 0x78));

        var iconForeground = new SolidColorBrush(
            dark ? Color.FromRgb(0xD6, 0xDC, 0xE6) : Color.FromRgb(0x44, 0x4D, 0x5E));

        BtnOpenFolder.Foreground = iconForeground;
        BtnCollapse.Foreground = iconForeground;
        BtnMore.Foreground = iconForeground;

        GripVisual.Stroke = new SolidColorBrush(
            dark ? Color.FromRgb(0x66, 0x74, 0x8C) : Color.FromRgb(0xB4, 0xBC, 0xC8));

        ApplyTopmost();
        RefreshItems();
    }

    public void ApplyTopmost()
    {
        try
        {
            Topmost = ZoneManager.Instance.Config.AlwaysOnTop;
        }
        catch
        {
            // ignore
        }
    }

    private static Color WithAlpha(Color color, double alpha)
    {
        var a = (byte)Math.Clamp(alpha * 255, 0, 255);
        return Color.FromArgb(a, color.R, color.G, color.B);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
            {
                return color;
            }
        }
        catch
        {
            // ignore
        }

        return fallback;
    }

    private void UpdateCollapseVisual()
    {
        var collapsed = _zone.Collapsed;

        ContentArea.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        GripVisual.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        ThumbN.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbW.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbE.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbNW.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbNE.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbSW.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ThumbSE.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        // 折叠状态下只保留底边可以上下拉伸
        ThumbS.Visibility = Visibility.Visible;

        ChevronRotate.Angle = collapsed ? -90 : 0;

        HeaderBar.CornerRadius = collapsed ? new CornerRadius(11) : new CornerRadius(11, 11, 0, 0);

        ToolTipService.SetToolTip(BtnCollapse, collapsed ? "展开分区" : "折叠分区");
    }

    // ================================================================ 文件列表

    public void RefreshItems()
    {
        var config = ZoneManager.Instance.Config;
        var dark = config.Appearance == ZoneAppearance.Dark;

        var nameBrush = dark ? DarkNameBrush : LightNameBrush;

        if (!Directory.Exists(_zone.FolderPath))
        {
            ItemsHost.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Collapsed;
            MissingHint.Visibility = Visibility.Visible;
            MissingPathText.Text = _zone.FolderPath;
            CountText.Text = string.Empty;
            return;
        }

        MissingHint.Visibility = Visibility.Collapsed;

        List<FileItemViewModel> items;
        try
        {
            items = FileItemViewModel.LoadFrom(
                _zone.FolderPath,
                _zone.SortMode,
                _zone.Descending,
                config.ShowHiddenFiles,
                Math.Clamp(config.IconSize, 24, 96),
                nameBrush,
                config.FullItemName,
                config.ItemNameMaxLines,
                config.ItemWidthMode);
        }
        catch
        {
            items = new List<FileItemViewModel>();
        }

        ItemsHost.ItemsSource = items;

        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = items.Count > 0 ? items.Count.ToString() : string.Empty;
    }

    // ================================================================ 文件监听

    private void SetupWatcher()
    {
        TeardownWatcher();

        try
        {
            if (!Directory.Exists(_zone.FolderPath))
            {
                return;
            }

            _watcher = new FileSystemWatcher(_zone.FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
                IncludeSubdirectories = false,
                // 只关心文件名级别的变动，缓冲区用最小值即可（默认 8 KB，
                // 分区多了也是一笔常驻开销）
                InternalBufferSize = 4 * 1024,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnWatchedFolderChanged;
            _watcher.Deleted += OnWatchedFolderChanged;
            _watcher.Changed += OnWatchedFolderChanged;
            _watcher.Renamed += OnWatchedFolderChanged;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void TeardownWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch
        {
            // ignore
        }

        _watcher = null;
    }

    private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher 回调在后台线程，切回 UI 线程并做防抖
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        }));
    }

    // ================================================================ 标题栏交互

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCollapse();
            e.Handled = true;
            return;
        }

        if (ZoneManager.Instance.LayoutLocked)
        {
            return;
        }

        _isDraggingZone = true;
        _dragCursorOrigin = GetCursorInDip();
        _dragWindowOrigin = new Point(Left, Top);
        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingZone)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndZoneDrag();
            return;
        }

        var current = GetCursorInDip();
        var newLeft = _dragWindowOrigin.X + (current.X - _dragCursorOrigin.X);
        var newTop = _dragWindowOrigin.Y + (current.Y - _dragCursorOrigin.Y);

        ApplySnapping(ref newLeft, ref newTop);

        Left = newLeft;
        Top = newTop;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndZoneDrag();

    private void EndZoneDrag()
    {
        if (!_isDraggingZone)
        {
            return;
        }

        _isDraggingZone = false;
        HeaderBar.ReleaseMouseCapture();
        PersistLayout();
    }

    private void Header_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowZoneMenu(e.GetPosition(RootPanel));
        e.Handled = true;
    }

    /// <summary>拖动时对齐到其它分区边缘和屏幕边缘。</summary>
    private void ApplySnapping(ref double left, ref double top)
    {
        if (!ZoneManager.Instance.Config.SnapEnabled)
        {
            return;
        }

        var width = Width;
        var height = Height;

        var targetsX = new List<double>
        {
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
        };

        var targetsY = new List<double>
        {
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight,
        };

        foreach (var other in ZoneManager.Instance.AllZoneConfigs)
        {
            if (other.Id == _zone.Id || !other.Visible)
            {
                continue;
            }

            targetsX.Add(other.X);
            targetsX.Add(other.X + other.Width);

            targetsY.Add(other.Y);
            targetsY.Add(other.Y + (other.Collapsed ? CollapsedHeight : other.Height));
        }

        var bestX = double.NaN;
        var bestXDistance = SnapThreshold;

        foreach (var target in targetsX)
        {
            var d1 = Math.Abs(left - target);
            if (d1 < bestXDistance)
            {
                bestXDistance = d1;
                bestX = target;
            }

            var d2 = Math.Abs(left + width - target);
            if (d2 < bestXDistance)
            {
                bestXDistance = d2;
                bestX = target - width;
            }
        }

        if (!double.IsNaN(bestX))
        {
            left = bestX;
        }

        var bestY = double.NaN;
        var bestYDistance = SnapThreshold;

        foreach (var target in targetsY)
        {
            var d1 = Math.Abs(top - target);
            if (d1 < bestYDistance)
            {
                bestYDistance = d1;
                bestY = target;
            }

            var d2 = Math.Abs(top + height - target);
            if (d2 < bestYDistance)
            {
                bestYDistance = d2;
                bestY = target - height;
            }
        }

        if (!double.IsNaN(bestY))
        {
            top = bestY;
        }
    }

    /// <summary>取光标位置并换算成 WPF 的 DIP 坐标。</summary>
    private Point GetCursorInDip()
    {
        NativeMethods.GetCursorPos(out var point);

        double scaleX = 1;
        double scaleY = 1;

        try
        {
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformFromDevice;
            if (transform.HasValue)
            {
                scaleX = transform.Value.M11;
                scaleY = transform.Value.M22;
            }
        }
        catch
        {
            // ignore
        }

        return new Point(point.X * scaleX, point.Y * scaleY);
    }

    // ================================================================ 缩放

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (ZoneManager.Instance.LayoutLocked || _zone.Collapsed)
        {
            return;
        }

        if (sender is not FrameworkElement thumb)
        {
            return;
        }

        var left = Left;
        var top = Top;
        var width = Width;
        var height = Height;

        var wantLeft = thumb.Name is "ThumbW" or "ThumbNW" or "ThumbSW";
        var wantRight = thumb.Name is "ThumbE" or "ThumbNE" or "ThumbSE";
        var wantTop = thumb.Name is "ThumbN" or "ThumbNW" or "ThumbNE";
        var wantBottom = thumb.Name is "ThumbS" or "ThumbSW" or "ThumbSE";

        if (wantLeft)
        {
            var delta = Math.Min(e.HorizontalChange, width - MinZoneWidth);
            left += delta;
            width -= delta;
        }
        else if (wantRight)
        {
            width = Math.Max(MinZoneWidth, width + e.HorizontalChange);
        }

        if (wantTop)
        {
            var delta = Math.Min(e.VerticalChange, height - MinZoneHeight);
            top += delta;
            height -= delta;
        }
        else if (wantBottom)
        {
            height = Math.Max(MinZoneHeight, height + e.VerticalChange);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _zone.ExpandedHeight = height;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => PersistLayout();

    // ================================================================ 拖放：接收文件

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDragFeedback(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDragFeedback(e);

    private void UpdateDragFeedback(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        SetDropHighlight(true);
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        // 从自己这里拖出去又拖回自己，什么都不做
        if (ReferenceEquals(DragDropContext.SourceZone, this))
        {
            return;
        }

        if (!Directory.Exists(_zone.FolderPath))
        {
            ZoneManager.ShowError(
                $"分区「{_zone.Name}」的文件夹不存在：\n{_zone.FolderPath}\n\n请先在分区菜单里重新创建文件夹。");
            return;
        }

        var result = ZoneFolderService.MoveInto(paths, _zone.FolderPath);

        RefreshItems();

        if (result.HasError)
        {
            var message = $"有 {result.Failed} 个项目没能移动进来。\n\n{result.FirstError}";
            if (result.Moved == 0)
            {
                message += "\n\n如果文件正被其它程序占用，请关闭后重试。";
            }

            ZoneManager.ShowError(message);
        }
        else if (result.Moved > 0)
        {
            FlashSuccess();
        }
    }

    private void SetDropHighlight(bool active)
    {
        var accent = ParseColor(_zone.Accent, Color.FromRgb(0x4C, 0x8D, 0xFF));

        RootPanel.BorderBrush = active
            ? new SolidColorBrush(accent)
            : new SolidColorBrush(WithAlpha(accent, 0x60));
    }

    /// <summary>成功收纳文件后，边框闪一下表示完成。</summary>
    private void FlashSuccess()
    {
        try
        {
            var accent = ParseColor(_zone.Accent, Color.FromRgb(0x4C, 0x8D, 0xFF));
            var brush = new SolidColorBrush(Colors.White);
            RootPanel.BorderBrush = brush;

            var animation = new ColorAnimation(WithAlpha(accent, 0x60), TimeSpan.FromMilliseconds(650))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
        catch
        {
            // ignore
        }
    }

    // ================================================================ 拖放：拖出文件

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FileItemViewModel item)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _pendingDragItem = null;
            if (!ZoneFolderService.ShellOpen(item.FullPath))
            {
                ZoneManager.ShowError($"无法打开：\n{item.FullPath}");
            }

            e.Handled = true;
            return;
        }

        _pendingDragItem = item;
        _itemDragOrigin = e.GetPosition(this);
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragItem == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _itemDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _itemDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pendingDragItem;
        _pendingDragItem = null;

        if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });

        DragDropContext.SourceZone = this;

        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch
        {
            // ignore
        }
        finally
        {
            DragDropContext.Reset();
        }

        // 文件被拖到别处（桌面 / 其它分区 / 资源管理器）之后刷新自己
        RefreshItems();
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pendingDragItem = null;

    private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FileItemViewModel item)
        {
            return;
        }

        _contextItem = item;
        ShowItemMenu(e.GetPosition(RootPanel));
        e.Handled = true;
    }

    private void Content_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowZoneMenu(e.GetPosition(RootPanel));
        e.Handled = true;
    }

    // ================================================================ 自绘下拉菜单

    private readonly record struct MenuEntry(
        string Text,
        Action? Action,
        bool IsSeparator = false,
        bool IsChecked = false,
        bool IsDanger = false);

    private void OpenMenu(IReadOnlyList<MenuEntry> entries, Point location)
    {
        MenuHost.Children.Clear();

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                MenuHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x45, 0x53)),
                });
                continue;
            }

            var check = new TextBlock
            {
                Text = "\u2713",
                Width = 15,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                Visibility = entry.IsChecked ? Visibility.Visible : Visibility.Hidden,
            };

            var label = new TextBlock
            {
                Text = entry.Text,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(entry.IsDanger
                    ? Color.FromRgb(0xFF, 0x7A, 0x7A)
                    : Color.FromRgb(0xE4, 0xE9, 0xF2)),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(check);
            row.Children.Add(label);

            var host = new Border
            {
                Padding = new Thickness(4, 7, 16, 7),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = row,
            };

            var action = entry.Action;
            if (action == null)
            {
                host.Opacity = 0.45;
                host.Cursor = Cursors.Arrow;
            }
            else
            {
                host.MouseEnter += (_, _) =>
                    host.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0x8D, 0xFF));

                host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;

                host.MouseLeftButtonUp += (_, args) =>
                {
                    args.Handled = true;
                    MenuPopup.IsOpen = false;

                    // 延后到下一次消息循环，避免弹窗在关闭动画中被打断
                    Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
                };
            }

            MenuHost.Children.Add(host);
        }

        MenuPopup.PlacementTarget = RootPanel;
        MenuPopup.Placement = PlacementMode.RelativePoint;
        MenuPopup.HorizontalOffset = location.X;
        MenuPopup.VerticalOffset = location.Y;
        MenuPopup.IsOpen = true;
    }

    private void ShowZoneMenu(Point location)
    {
        var locked = ZoneManager.Instance.LayoutLocked;

        var entries = new List<MenuEntry>
        {
            new("打开分区文件夹", OpenZoneFolder),
            new("在资源管理器中显示", RevealZoneFolder),
            new("刷新", RefreshItems),
            new("", null, IsSeparator: true),
            new("分区设置…", EditZoneSettings),
            new("新建分区…", () => ZoneManager.Instance.AddZoneInteractive(this)),
            new("", null, IsSeparator: true),
            new("按名称排序", () => SetSortMode(ZoneSortMode.Name), IsChecked: _zone.SortMode == ZoneSortMode.Name),
            new("按修改时间排序", () => SetSortMode(ZoneSortMode.DateModified), IsChecked: _zone.SortMode == ZoneSortMode.DateModified),
            new("按大小排序", () => SetSortMode(ZoneSortMode.Size), IsChecked: _zone.SortMode == ZoneSortMode.Size),
            new("按类型排序", () => SetSortMode(ZoneSortMode.Type), IsChecked: _zone.SortMode == ZoneSortMode.Type),
            new(_zone.Descending ? "改为升序" : "改为降序", ToggleSortDirection),
            new("", null, IsSeparator: true),
            new("完整显示名称（不缩略）", ToggleFullItemName, IsChecked: ZoneManager.Instance.Config.FullItemName),
            new("", null, IsSeparator: true),
            new(locked ? "解锁布局" : "锁定布局", ToggleLayoutLock, IsChecked: locked),
            new(_zone.Collapsed ? "展开分区" : "折叠分区", ToggleCollapse),
            new("", null, IsSeparator: true),
            new("删除这个分区…", ConfirmDeleteZone, IsDanger: true),
        };

        OpenMenu(entries, location);
    }

    private void ShowItemMenu(Point location)
    {
        var item = _contextItem;
        if (item == null)
        {
            return;
        }

        var isDirectory = item.IsDirectory;

        var entries = new List<MenuEntry>
        {
            new(isDirectory ? "打开文件夹" : "打开", () => ZoneFolderService.ShellOpen(item.FullPath)),
            new("在资源管理器中显示", () => ZoneFolderService.RevealInExplorer(item.FullPath)),
            new("", null, IsSeparator: true),
            new("重命名…", () => RenameItem(item)),
            new("复制完整路径", () => CopyToClipboard(item.FullPath)),
            new("", null, IsSeparator: true),
            new("移动到回收站", () => DeleteItem(item), IsDanger: true),
        };

        OpenMenu(entries, location);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch
        {
            // 剪贴板被别的程序锁住时会失败，忽略
        }
    }

    // ================================================================ 菜单动作

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenZoneFolder();

    private void OpenZoneFolder()
    {
        if (!Directory.Exists(_zone.FolderPath))
        {
            ZoneFolderService.EnsureFolder(_zone.FolderPath);
            SetupWatcher();
        }

        ZoneFolderService.ShellOpen(_zone.FolderPath);
        RefreshItems();
    }

    private void RevealZoneFolder() => ZoneFolderService.RevealInExplorer(_zone.FolderPath);

    /// <summary>切换「完整显示名称」。这是全局设置，切换后刷新所有分区。</summary>
    private void ToggleFullItemName()
    {
        var config = ZoneManager.Instance.Config;
        config.FullItemName = !config.FullItemName;
        ZoneManager.Instance.SaveNow();
        ZoneManager.Instance.RefreshAll();
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void ToggleCollapse()
    {
        if (_zone.Collapsed)
        {
            _zone.Collapsed = false;
            Height = Math.Max(MinZoneHeight, _zone.ExpandedHeight);
        }
        else
        {
            _zone.ExpandedHeight = Height;
            _zone.Collapsed = true;
            Height = CollapsedHeight;
        }

        UpdateCollapseVisual();
        PersistLayout();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        // 菜单显示在按钮下方
        var position = BtnMore.TranslatePoint(new Point(0, BtnMore.ActualHeight + 2), RootPanel);
        ShowZoneMenu(position);
    }

    private void SetSortMode(ZoneSortMode mode)
    {
        _zone.SortMode = mode;
        RefreshItems();
        PersistLayout();
    }

    private void ToggleSortDirection()
    {
        _zone.Descending = !_zone.Descending;
        RefreshItems();
        PersistLayout();
    }

    private void ToggleLayoutLock()
    {
        var config = ZoneManager.Instance.Config;
        config.LayoutLocked = !config.LayoutLocked;
        ZoneManager.Instance.SaveNow();
    }

    private void EditZoneSettings()
    {
        var dialog = new ZoneEditorWindow(_zone, ZoneManager.Instance.Config.DefaultSortMode) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result == null)
        {
            return;
        }

        var folderChanged = !ZoneFolderService.IsSameFolder(dialog.Result.FolderPath, _zone.FolderPath);

        _zone.Name = dialog.Result.Name;
        _zone.Accent = dialog.Result.Accent;

        if (folderChanged)
        {
            _zone.FolderPath = dialog.Result.FolderPath;
            ZoneFolderService.EnsureFolder(_zone.FolderPath);
            SetupWatcher();
        }

        ApplyAppearance();
        UpdateCollapseVisual();
        PersistLayout();
    }

    private void RecreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ZoneFolderService.EnsureFolder(_zone.FolderPath))
        {
            SetupWatcher();
            RefreshItems();
        }
        else
        {
            ZoneManager.ShowError($"无法创建文件夹：\n{_zone.FolderPath}\n\n请检查路径是否合法、磁盘是否可写。");
        }
    }

    private void RenameItem(FileItemViewModel item)
    {
        var prompt = new TextPromptWindow("重命名", "新名称", item.DisplayName)
        {
            Owner = this,
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var newName = prompt.Value.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.DisplayName)
        {
            return;
        }

        try
        {
            var parent = Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            var target = Path.Combine(parent, newName);

            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, target);
            }
            else
            {
                File.Move(item.FullPath, target);
            }
        }
        catch (Exception ex)
        {
            ZoneManager.ShowError($"重命名失败：\n{ex.Message}");
        }

        RefreshItems();
    }

    private void DeleteItem(FileItemViewModel item)
    {
        var confirmed = ZoneManager.Confirm(
            $"确定把下面的项目移到回收站吗？\n\n{item.DisplayName}\n\n（可以从回收站还原）");

        if (!confirmed)
        {
            return;
        }

        if (!ZoneFolderService.MoveToRecycleBin(item.FullPath))
        {
            ZoneManager.ShowError($"无法移动到回收站：\n{item.FullPath}");
        }

        RefreshItems();
    }

    private void ConfirmDeleteZone()
    {
        var folderExists = Directory.Exists(_zone.FolderPath);
        var hasFiles = false;

        try
        {
            hasFiles = folderExists && Directory.EnumerateFileSystemEntries(_zone.FolderPath).Any();
        }
        catch
        {
            // ignore
        }

        var message = $"确定删除分区「{_zone.Name}」吗？\n\n" +
                      (hasFiles
                          ? "分区里的文件会原样保留在磁盘上，只是不再显示这个分区框。"
                          : "这个分区文件夹是空的。");

        if (!ZoneManager.Confirm(message, "删除分区"))
        {
            return;
        }

        ZoneManager.Instance.RemoveZone(_zone, deleteFolder: true);
    }

    // ================================================================ 生命周期

    /// <summary>由 ZoneManager 在关闭前调用，避免关闭过程中触发保存。</summary>
    public void PrepareForClose()
    {
        _isClosing = true;
        TeardownWatcher();
        _watcherDebounce.Stop();
    }

    public void HideZone()
    {
        Hide();

        // 隐藏后释放列表。界面上看不见，没必要继续挂载几百个条目和它们的图标引用；
        // 下次 ShowZone 会重新加载（图标有缓存，重建很快，用户无感）。
        ItemsHost.ItemsSource = null;
        ShellIconService.ReleaseMemory();
    }

    public void ShowZone()
    {
        if (!IsVisible)
        {
            Show();
        }
        else
        {
            Activate();
        }

        RefreshItems();
    }

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        _isClosing = true;
        TeardownWatcher();
        _watcherDebounce.Stop();
    }
}
