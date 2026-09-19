using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopOrganizer.Services;

/// <summary>
/// 通过 Windows Shell 取文件 / 文件夹的真实图标（48x48），并做缓存。
/// 取不到时逐级降级，绝不抛异常。
/// </summary>
public static class ShellIconService
{
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>缓存条目上限。一张 48x48 的位图约 9 KB，600 张约 5 MB，够用且可控。</summary>
    private const int CacheLimit = 600;

    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static NativeMethods.IImageList? _imageListExLarge;
    private static bool _imageListProbed;

    private static readonly object FallbackIconGate = new();
    private static BitmapSource? _genericFileIcon;
    private static BitmapSource? _genericFolderIcon;

    /// <summary>取图标。同一个扩展名只会真正查询一次 Shell。</summary>
    public static BitmapSource GetIcon(string path, bool isDirectory)
    {
        var key = BuildCacheKey(path, isDirectory);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = CreateIcon(path, isDirectory);
        Cache[key] = icon;
        TrimIfNeeded();
        return icon;
    }

    /// <summary>
    /// .lnk / .exe / .url / .ico 这类文件的图标和具体文件绑定，
    /// 其余类型按扩展名缓存即可，避免几百个文件都去问一次 Shell。
    /// </summary>
    private static string BuildCacheKey(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "dir:" + path;
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return "file:" + path;
        }

        return "ext:" + ext.ToLowerInvariant();
    }

    private static BitmapSource CreateIcon(string path, bool isDirectory)
    {
        var existsOnDisk = isDirectory ? Directory.Exists(path) : File.Exists(path);

        // 1) 首选：系统 ImageList 的 ExtraLarge（48x48）
        var fromList = TryGetFromImageList(path, isDirectory, existsOnDisk);
        if (fromList != null)
        {
            return fromList;
        }

        // 2) 退化：SHGetFileInfo 的大图标（32x32）
        var fromShell = TryGetFromShellInfo(path, isDirectory, existsOnDisk);
        if (fromShell != null)
        {
            return fromShell;
        }

        // 3) 兜底：内置的通用图标
        return GetGenericIcon(isDirectory);
    }

    private static NativeMethods.IImageList? ImageListExLarge
    {
        get
        {
            if (_imageListProbed)
            {
                return _imageListExLarge;
            }

            _imageListProbed = true;
            try
            {
                var iid = IID_IImageList;
                if (NativeMethods.SHGetImageList(
                        NativeMethods.SHIL_EXTRALARGE, ref iid, out var list) == 0 && list != null)
                {
                    _imageListExLarge = list;
                }
            }
            catch
            {
                _imageListExLarge = null;
            }

            return _imageListExLarge;
        }
    }

    private static BitmapSource? TryGetFromImageList(string path, bool isDirectory, bool existsOnDisk)
    {
        var list = ImageListExLarge;
        if (list == null)
        {
            return null;
        }

        try
        {
            var info = new NativeMethods.SHFILEINFO();
            var flags = NativeMethods.SHGFI_SYSICONINDEX;
            var attributes = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;

            if (!existsOnDisk)
            {
                // 文件已被删除 / 不存在时，只按扩展名问图标，不要真的去访问路径
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            }

            var result = NativeMethods.SHGetFileInfo(
                path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

            if (result == IntPtr.Zero)
            {
                return null;
            }

            var hIcon = IntPtr.Zero;
            var hr = list.GetIcon(info.iIcon, NativeMethods.ILD_TRANSPARENT, ref hIcon);
            if (hr != 0 || hIcon == IntPtr.Zero)
            {
                return null;
            }

            return FromHIcon(hIcon, ownsHandle: true);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryGetFromShellInfo(string path, bool isDirectory, bool existsOnDisk)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
            var attributes = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;

            if (!existsOnDisk)
            {
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            }

            var result = NativeMethods.SHGetFileInfo(
                path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            return FromHIcon(info.hIcon, ownsHandle: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 HICON 转成 WPF 位图。ownsHandle 为 true 时负责销毁句柄。</summary>
    private static BitmapSource? FromHIcon(IntPtr hIcon, bool ownsHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ownsHandle && hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }

    /// <summary>完全不依赖 Shell 的内置兜底图标，用 WPF 直接画。</summary>
    private static BitmapSource GetGenericIcon(bool isDirectory)
    {
        lock (FallbackIconGate)
        {
            if (isDirectory && _genericFolderIcon != null)
            {
                return _genericFolderIcon;
            }

            if (!isDirectory && _genericFileIcon != null)
            {
                return _genericFileIcon;
            }

            var icon = DrawGenericIcon(isDirectory);
            if (isDirectory)
            {
                _genericFolderIcon = icon;
            }
            else
            {
                _genericFileIcon = icon;
            }

            return icon;
        }
    }

    private static BitmapSource DrawGenericIcon(bool isDirectory)
    {
        const int size = 48;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                isDirectory
                    ? System.Windows.Media.Color.FromRgb(0xF5, 0xC0, 0x42)
                    : System.Windows.Media.Color.FromRgb(0x9A, 0xA4, 0xB5));

            var rect = isDirectory
                ? new Rect(6, 14, 36, 26)
                : new Rect(11, 5, 26, 38);

            dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 丢掉「按具体路径缓存」的图标（文件夹、快捷方式、exe 等）。
    /// 按扩展名缓存的图标复用率高，留着更划算。
    /// </summary>
    private static void DropPathKeyedIcons()
    {
        foreach (var key in Cache.Keys)
        {
            if (key.StartsWith("dir:", StringComparison.Ordinal) ||
                key.StartsWith("file:", StringComparison.Ordinal))
            {
                Cache.TryRemove(key, out _);
            }
        }
    }

    private static void TrimIfNeeded()
    {
        if (Cache.Count <= CacheLimit)
        {
            return;
        }

        DropPathKeyedIcons();

        // 极端情况下（扩展名种类本身就超上限）才整体清空
        if (Cache.Count > CacheLimit)
        {
            Cache.Clear();
        }
    }

    /// <summary>
    /// 释放图标占用的内存。分区全部隐藏时调用——此时界面上看不到图标，
    /// 下次显示时从 Shell 重新取一次即可（很快，用户无感）。
    /// </summary>
    public static void ReleaseMemory()
    {
        DropPathKeyedIcons();
    }

    /// <summary>清空缓存（例如用户切换了默认应用之后）。</summary>
    public static void ClearCache() => Cache.Clear();
}
