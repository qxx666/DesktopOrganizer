using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopOrganizer.Models;
using DesktopOrganizer.Windows;

namespace DesktopOrganizer.Services;

/// <summary>
/// 全局协调器：持有配置、管理所有分区窗口、统一应用设置。
/// </summary>
public sealed class ZoneManager
{
    public static ZoneManager Instance { get; } = new();

    private readonly Dictionary<string, ZoneWindow> _windows = new();
    private readonly DispatcherTimer _saveTimer;

    private ZoneManager()
    {
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public AppConfig Config { get; private set; } = new();

    public string RootFolder =>
        string.IsNullOrWhiteSpace(Config.RootFolder) ? ConfigService.DefaultRootFolder : Config.RootFolder;

    public bool LayoutLocked => Config.LayoutLocked;

    public IReadOnlyCollection<ZoneWindow> Windows => _windows.Values;

    /// <summary>供拖动吸附查询的其它分区矩形。</summary>
    public IEnumerable<ZoneConfig> AllZoneConfigs => Config.Zones;

    // ---------------------------------------------------------------- 初始化

    public void Initialize()
    {
        Config = ConfigService.Load();

        if (Config.Zones.Count == 0)
        {
            CreateFirstRunZones();
        }

        // 清理掉指向空路径的损坏条目
        Config.Zones.RemoveAll(z => string.IsNullOrWhiteSpace(z.FolderPath));

        foreach (var zone in Config.Zones.ToList())
        {
            OpenZoneWindow(zone);
        }

        SaveNow();
    }

    /// <summary>首次运行时给一组开箱即用的分区。</summary>
    private void CreateFirstRunZones()
    {
        var presets = new (string Name, string Accent)[]
        {
            ("工作", "#4C8DFF"),
            ("资料", "#2ECC9B"),
            ("待整理", "#F5A623"),
        };

        ZoneFolderService.EnsureFolder(RootFolder);

        var offsetX = 80.0;
        var offsetY = 120.0;

        foreach (var (name, accent) in presets)
        {
            var folder = ZoneFolderService.BuildZoneFolder(RootFolder, name);
            ZoneFolderService.EnsureFolder(folder);

            Config.Zones.Add(new ZoneConfig
            {
                Name = name,
                Accent = accent,
                FolderPath = folder,
                X = offsetX,
                Y = offsetY,
                Width = 360,
                Height = 300,
                ExpandedHeight = 300,
            });

            offsetX += 392;
        }
    }

    // ---------------------------------------------------------------- 分区窗口

    public ZoneWindow OpenZoneWindow(ZoneConfig zone)
    {
        if (_windows.TryGetValue(zone.Id, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var window = new ZoneWindow(zone);
        _windows[zone.Id] = window;

        if (zone.Visible && !IsHiddenByUser)
        {
            window.Show();
        }

        return window;
    }

    public ZoneWindow? FindWindow(string zoneId) =>
        _windows.TryGetValue(zoneId, out var window) ? window : null;

    /// <summary>新建分区。返回新分区窗口；用户取消时返回 null。</summary>
    public ZoneWindow? AddZoneInteractive(Window? owner)
    {
        var dialog = new ZoneEditorWindow(null, Config.DefaultSortMode) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var zone = dialog.Result!;

        if (!ZoneFolderService.EnsureFolder(zone.FolderPath))
        {
            ShowError($"无法创建或访问分区文件夹：\n{zone.FolderPath}");
            return null;
        }

        // 找一个不压住已有分区的落点
        var position = FindFreePosition(zone.Width, zone.Height);
        zone.X = position.X;
        zone.Y = position.Y;

        Config.Zones.Add(zone);
        var window = OpenZoneWindow(zone);
        SaveNow();

        return window;
    }

    /// <summary>在虚拟屏幕上找一个不重叠的落点，找不到就阶梯错开。</summary>
    private Point FindFreePosition(double width, double height)
    {
        const double margin = 40;
        const double gap = 16;

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;

        var maxRight = virtualLeft + virtualWidth - margin;

        var x = virtualLeft + margin;
        var y = virtualTop + margin;

        for (var attempt = 0; attempt < 400; attempt++)
        {
            var candidate = new Rect(x, y, width, height);
            var overlaps = Config.Zones
                .Where(z => z.Visible)
                .Select(z => new Rect(z.X, z.Y, z.Width, z.Collapsed ? 36 : z.Height))
                .Any(r => r.IntersectsWith(candidate));

            if (!overlaps)
            {
                return new Point(x, y);
            }

            if (x + width + gap + 40 < maxRight)
            {
                x += width + gap;
            }
            else
            {
                x = virtualLeft + margin;
                y += 72;
            }
        }

        // 实在找不到就错开叠放，用户自己拖开即可
        var count = Config.Zones.Count;
        return new Point(virtualLeft + margin + count * 28 % 400, virtualTop + margin + count * 28 % 320);
    }

    public void RemoveZone(ZoneConfig zone, bool deleteFolder)
    {
        if (_windows.Remove(zone.Id, out var window))
        {
            window.PrepareForClose();
            window.Close();
        }

        Config.Zones.RemoveAll(z => z.Id == zone.Id);

        if (deleteFolder)
        {
            // 只删空目录，里面还有文件就保留，绝不动用户文件
            try
            {
                if (Directory.Exists(zone.FolderPath) &&
                    !Directory.EnumerateFileSystemEntries(zone.FolderPath).Any())
                {
                    Directory.Delete(zone.FolderPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        SaveNow();
    }

    public void RefreshAll()
    {
        foreach (var window in _windows.Values)
        {
            window.RefreshItems();
        }
    }

    public void ApplyAppearance()
    {
        foreach (var window in _windows.Values)
        {
            window.ApplyAppearance();
        }
    }

    public void ApplyTopmost()
    {
        foreach (var window in _windows.Values)
        {
            window.ApplyTopmost();
        }
    }

    // ---------------------------------------------------------------- 显示 / 隐藏

    /// <summary>用户主动隐藏（用快捷键），此时新分区也不应该自动弹出来。</summary>
    public bool IsHiddenByUser { get; private set; }

    public bool IsAnyVisible => _windows.Values.Any(w => w.IsVisible);

    public void HideAll()
    {
        IsHiddenByUser = true;

        foreach (var window in _windows.Values)
        {
            window.HideZone();
        }
    }

    public void ShowAll()
    {
        IsHiddenByUser = false;

        foreach (var zone in Config.Zones)
        {
            if (!zone.Visible)
            {
                continue;
            }

            var window = OpenZoneWindow(zone);
            window.ShowZone();
        }
    }

    public void ToggleAll()
    {
        if (IsAnyVisible)
        {
            HideAll();
        }
        else
        {
            ShowAll();
        }
    }

    // ---------------------------------------------------------------- 持久化

    /// <summary>合并短时间内的多次保存请求（拖动分区时会连续触发）。</summary>
    public void SaveDebounced()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        ConfigService.Save(Config);
    }

    // ---------------------------------------------------------------- 工具

    public static void ShowError(string message) =>
        System.Windows.MessageBox.Show(
            message, "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string message, string title = "桌面分区管家") =>
        System.Windows.MessageBox.Show(
            message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>生成托盘 / 窗口用的图标。</summary>
    public static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            // 落到下面的绘制版本
        }

        return BuildFallbackIcon();
    }

    private static System.Drawing.Icon BuildFallbackIcon()
    {
        const int size = 64;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x30)), null,
                new Rect(0, 0, size, size), 14, 14);

            var colors = new[]
            {
                Color.FromRgb(0x4C, 0x8D, 0xFF),
                Color.FromRgb(0x2E, 0xCC, 0x9B),
                Color.FromRgb(0xF5, 0xA6, 0x23),
                Color.FromRgb(0xA0, 0x7B, 0xFF),
            };

            var rects = new[]
            {
                new Rect(10, 10, 20, 20),
                new Rect(34, 10, 20, 20),
                new Rect(10, 34, 20, 20),
                new Rect(34, 34, 20, 20),
            };

            for (var i = 0; i < 4; i++)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null, rects[i], 5, 5);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var memory = new MemoryStream();
        encoder.Save(memory);
        memory.Position = 0;

        using var drawingBitmap = new System.Drawing.Bitmap(memory);
        var handle = drawingBitmap.GetHicon();

        try
        {
            using var temp = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
