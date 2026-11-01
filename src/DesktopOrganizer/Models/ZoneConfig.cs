using System;
using System.Text.Json.Serialization;

namespace DesktopOrganizer.Models;

/// <summary>分区内文件的排序方式。</summary>
public enum ZoneSortMode
{
    Name,
    DateModified,
    Size,
    Type,
}

/// <summary>分区面板的外观主题。</summary>
public enum ZoneAppearance
{
    Dark,
    Light,
}

/// <summary>分区名称（标题）的对齐方式。</summary>
public enum ZoneNameAlignment
{
    Left,
    Center,
}

/// <summary>
/// 一个桌面分区的持久化配置。每个分区绑定磁盘上的一个真实文件夹。
/// </summary>
public sealed class ZoneConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "新分区";

    /// <summary>该分区绑定的真实文件夹（绝对路径）。</summary>
    public string FolderPath { get; set; } = string.Empty;

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 300;

    /// <summary>强调色，#RRGGBB。</summary>
    public string Accent { get; set; } = "#4C8DFF";

    /// <summary>折叠前的展开高度，用于展开时还原。</summary>
    public double ExpandedHeight { get; set; } = 300;

    public bool Collapsed { get; set; }

    public ZoneSortMode SortMode { get; set; } = ZoneSortMode.Name;

    public bool Descending { get; set; }

    /// <summary>是否参与「显示 / 隐藏」的整体切换。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>分区名称（标题）显示字号，单位 DIP。默认 20，建议范围 11~40。</summary>
    public double NameFontSize { get; set; } = 20;

    /// <summary>分区名称对齐方式：居左或居中，默认居左。</summary>
    public ZoneNameAlignment NameAlignment { get; set; } = ZoneNameAlignment.Left;

    /// <summary>外框淡化程度 0~100，越大边框越明显。默认 25（很淡的玻璃高光线）。</summary>
    public double FrameFaint { get; set; } = 25;

    public ZoneConfig Clone() => (ZoneConfig)MemberwiseClone();
}
