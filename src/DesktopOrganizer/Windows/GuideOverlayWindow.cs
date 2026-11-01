using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopOrganizer.Windows;

/// <summary>
/// 全屏透明覆盖窗口，用于在拖动 / 缩放分区时，跨桌面绘制与其它分区或屏幕边缘对齐的参考线。
/// 它本身不接收鼠标（IsHitTestVisible=false），只负责显示一条（或多条）高亮线。
/// </summary>
public sealed class GuideOverlayWindow : Window
{
    private readonly Canvas _canvas = new();

    public GuideOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        ShowActivated = false;

        // 覆盖全部虚拟屏幕（多显示器场景）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = _canvas;
    }

    /// <summary>显示参考线。verticalX / horizontalY 分别是需要对齐的「屏幕坐标」（DIP）。</summary>
    public void ShowGuides(IReadOnlyList<double> verticalX, IReadOnlyList<double> horizontalY)
    {
        _canvas.Children.Clear();

        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var width = SystemParameters.VirtualScreenWidth;
        var height = SystemParameters.VirtualScreenHeight;

        var stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

        foreach (var x in verticalX)
        {
            var line = new Line
            {
                X1 = x - left,
                Y1 = 0,
                X2 = x - left,
                Y2 = height,
                Stroke = stroke,
                StrokeThickness = 1.5,
                Opacity = 0.9,
                SnapsToDevicePixels = true,
            };
            _canvas.Children.Add(line);
        }

        foreach (var y in horizontalY)
        {
            var line = new Line
            {
                X1 = 0,
                Y1 = y - top,
                X2 = width,
                Y2 = y - top,
                Stroke = stroke,
                StrokeThickness = 1.5,
                Opacity = 0.9,
                SnapsToDevicePixels = true,
            };
            _canvas.Children.Add(line);
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    public new void Hide()
    {
        _canvas.Children.Clear();
        if (IsVisible)
        {
            base.Hide();
        }
    }
}

/// <summary>对齐参考线的全局控制器（线程单例，懒创建于 UI 线程）。</summary>
public static class AlignmentGuides
{
    private static GuideOverlayWindow? _window;

    public static void Show(IReadOnlyList<double> verticalX, IReadOnlyList<double> horizontalY)
    {
        try
        {
            if (_window == null)
            {
                _window = new GuideOverlayWindow();
            }

            _window.ShowGuides(verticalX, horizontalY);
        }
        catch
        {
            // 覆盖窗口创建/显示失败不应影响拖拽本身
        }
    }

    public static void Hide()
    {
        try
        {
            _window?.Hide();
        }
        catch
        {
            // ignore
        }
    }
}
