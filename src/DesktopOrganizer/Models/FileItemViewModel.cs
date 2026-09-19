using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.Services;

namespace DesktopOrganizer.Models;

/// <summary>
/// 分区里的一个条目（文件或文件夹）。为了刷新简单，做成不可变对象，重建即可。
/// </summary>
public sealed class FileItemViewModel
{
    /// <summary>名称字号，与 ZoneWindow 里显示名称的 TextBlock 保持一致。</summary>
    public const double NameFontSize = 11;

    /// <summary>名称行高，与 ZoneWindow 里显示名称的 TextBlock 保持一致。</summary>
    public const double NameLineHeight = 13;

    /// <summary>名称字体，与 ZoneWindow 里显示名称的 TextBlock 保持一致（测量宽度时必须一致）。</summary>
    public const string NameFontFamily = "Microsoft YaHei UI, Segoe UI";

    public required string FullPath { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsDirectory { get; init; }

    public required ImageSource Icon { get; init; }

    public required DateTime Modified { get; init; }

    public required long Size { get; init; }

    // ---- 由全局设置注入的显示参数 ----

    public double IconSize { get; init; } = 48;

    public double ItemWidth { get; init; } = 94;

    public double ItemHeight { get; init; } = 88;

    /// <summary>名称显示区的高度。整个分区共用同一份，保证各条目行高对齐。</summary>
    public double NameBoxHeight { get; init; } = NameLineHeight * 2 + 2;

    /// <summary>名称超出行数上限时怎么处理。完整模式不做省略号截断。</summary>
    public TextTrimming NameTrimming { get; init; } = TextTrimming.None;

    public Brush NameBrush { get; init; } = Brushes.White;

    public string SizeText => IsDirectory ? "文件夹" : FormatSize(Size);

    public string TooltipText => IsDirectory
        ? $"{DisplayName}\n文件夹\n修改时间：{Modified:yyyy-MM-dd HH:mm}"
        : $"{DisplayName}\n{SizeText}\n修改时间：{Modified:yyyy-MM-dd HH:mm}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unitIndex = -1;

        do
        {
            value /= 1024;
            unitIndex++;
        }
        while (value >= 1024 && unitIndex < units.Length - 1);

        return value >= 100 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }

    /// <summary>
    /// 读取文件夹内容并排序。文件夹始终排在文件之前。
    /// 条目的宽度和高度会按当前列表中**最长的那个名称**自动调整，
    /// 这样完整模式下的名称能整行显示，不会被截成省略号。
    /// </summary>
    public static List<FileItemViewModel> LoadFrom(
        string folder,
        ZoneSortMode sortMode,
        bool descending,
        bool showHidden,
        double iconSize,
        Brush nameBrush,
        bool fullItemName = true,
        int nameMaxLines = 3,
        int widthMode = 1)
    {
        if (!Directory.Exists(folder))
        {
            return new List<FileItemViewModel>();
        }

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(folder).GetFileSystemInfos();
        }
        catch
        {
            return new List<FileItemViewModel>();
        }

        // ---- 第一遍：收集条目并量出每个名称实际需要多宽 ----
        var raw = new List<(FileSystemInfo Entry, bool IsDirectory, long Size, double NameWidth)>();

        foreach (var entry in entries)
        {
            try
            {
                // 跳过 desktop.ini、Thumbs.db 之类的系统文件
                if ((entry.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                var isHidden = (entry.Attributes & FileAttributes.Hidden) != 0;
                if (isHidden && !showHidden)
                {
                    continue;
                }

                var isDirectory = entry is DirectoryInfo;
                var size = !isDirectory && entry is FileInfo fileInfo ? fileInfo.Length : 0;

                raw.Add((entry, isDirectory, size, MeasureNameWidth(entry.Name)));
            }
            catch
            {
                // 单个条目出错不影响整体列表
            }
        }

        // ---- 第二遍：按最长名称决定统一的条目宽度与行数 ----
        var linesAllowed = fullItemName ? Math.Clamp(nameMaxLines, 1, 5) : 2;
        var (baseWidth, maxWidth) = WidthRange(widthMode, iconSize);

        var itemWidth = baseWidth;
        if (fullItemName && raw.Count > 0)
        {
            var widest = raw.Max(r => r.NameWidth);
            // 名称再长也不把条目撑到离谱的宽度，超过上限的部分靠换行消化
            itemWidth = Math.Min(Math.Max(baseWidth, widest + 10), maxWidth);
        }

        // 名称可用宽度：条目左右各留 1 的边距，再留 2 的抗测量误差余量
        var usableWidth = Math.Max(24, itemWidth - 4);

        var linesNeeded = 1;
        foreach (var item in raw)
        {
            var need = (int)Math.Ceiling(item.NameWidth / usableWidth);
            linesNeeded = Math.Max(linesNeeded, Math.Min(Math.Max(need, 1), linesAllowed));
        }

        var nameBoxHeight = linesNeeded * NameLineHeight + 2;
        var itemHeight = Math.Max(72, iconSize + 14 + nameBoxHeight);
        var trimming = fullItemName ? TextTrimming.None : TextTrimming.CharacterEllipsis;

        // ---- 第三遍：生成显示模型 ----
        var collected = new List<FileItemViewModel>(raw.Count);

        foreach (var (entry, isDirectory, size, _) in raw)
        {
            try
            {
                collected.Add(new FileItemViewModel
                {
                    FullPath = entry.FullName,
                    DisplayName = entry.Name,
                    IsDirectory = isDirectory,
                    Icon = ShellIconService.GetIcon(entry.FullName, isDirectory),
                    Modified = entry.LastWriteTime,
                    Size = size,
                    IconSize = iconSize,
                    ItemWidth = itemWidth,
                    ItemHeight = itemHeight,
                    NameBoxHeight = nameBoxHeight,
                    NameTrimming = trimming,
                    NameBrush = nameBrush,
                });
            }
            catch
            {
                // 单个条目出错不影响整体列表
            }
        }

        return SortGroup(collected.Where(i => i.IsDirectory), sortMode, descending)
            .Concat(SortGroup(collected.Where(i => !i.IsDirectory), sortMode, descending))
            .ToList();
    }

    /// <summary>量出一个名称在当前字号下需要多少宽度（DIP）。</summary>
    private static double MeasureNameWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    new FontFamily(NameFontFamily),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal),
                NameFontSize,
                Brushes.Black,
                pixelsPerDip: 1.0);

            return formatted.Width;
        }
        catch
        {
            // 测量失败时按「一个字符一个字号」粗略估算，不至于让布局塌掉
            return text.Length * NameFontSize;
        }
    }

    /// <summary>不同宽度档位下的条目宽度下限与上限。</summary>
    private static (double Base, double Max) WidthRange(int widthMode, double iconSize)
    {
        return widthMode switch
        {
            0 => (Math.Max(68, iconSize + 24), 108),   // 紧凑
            2 => (Math.Max(112, iconSize + 70), 232),  // 宽松
            _ => (Math.Max(92, iconSize + 46), 168),   // 标准
        };
    }

    private static IEnumerable<FileItemViewModel> SortGroup(
        IEnumerable<FileItemViewModel> source, ZoneSortMode mode, bool descending)
    {
        return mode switch
        {
            ZoneSortMode.DateModified => descending
                ? source.OrderByDescending(i => i.Modified)
                : source.OrderBy(i => i.Modified),

            ZoneSortMode.Size => descending
                ? source.OrderByDescending(i => i.Size)
                : source.OrderBy(i => i.Size),

            ZoneSortMode.Type => descending
                ? source.OrderByDescending(i => Path.GetExtension(i.FullPath), StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(i => Path.GetExtension(i.FullPath), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),

            _ => descending
                ? source.OrderByDescending(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
    }
}
