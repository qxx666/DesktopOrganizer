using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using DesktopOrganizer.Services;

namespace DesktopOrganizer.Models;

/// <summary>
/// 分区里的一个条目（文件或文件夹）。为了刷新简单，做成不可变对象，重建即可。
/// </summary>
public sealed class FileItemViewModel
{
    public required string FullPath { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsDirectory { get; init; }

    public required ImageSource Icon { get; init; }

    public required DateTime Modified { get; init; }

    public required long Size { get; init; }

    // ---- 由全局设置注入的显示参数 ----

    public double IconSize { get; init; } = 48;

    public double ItemWidth { get; init; } = 74;

    public double ItemHeight { get; init; } = 88;

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

    /// <summary>读取文件夹内容并排序。文件夹始终排在文件之前。</summary>
    public static List<FileItemViewModel> LoadFrom(
        string folder,
        ZoneSortMode sortMode,
        bool descending,
        bool showHidden,
        double iconSize,
        Brush nameBrush)
    {
        var collected = new List<FileItemViewModel>();

        if (!Directory.Exists(folder))
        {
            return collected;
        }

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(folder).GetFileSystemInfos();
        }
        catch
        {
            return collected;
        }

        var itemWidth = Math.Max(64, iconSize + 26);
        var itemHeight = Math.Max(72, iconSize + 40);

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
