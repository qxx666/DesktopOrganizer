using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopOrganizer.Models;

/// <summary>全局配置，持久化到 %APPDATA%\DesktopOrganizer\config.json。</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public List<ZoneConfig> Zones { get; set; } = new();

    /// <summary>新建分区时，默认在该目录下按分区名创建子文件夹。空表示使用默认值。</summary>
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>布局锁定：锁定后分区不可拖动 / 缩放 / 删除，防止误操作。</summary>
    public bool LayoutLocked { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>显示 / 隐藏全部分区的全局快捷键，例如 Ctrl+Alt+D。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>图标显示尺寸（DIP）。</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>
    /// 完整显示文件 / 文件夹名称：条目会自动加宽、换行把名称显示全，不做省略号截断。
    /// 关掉则退回紧凑模式，名称最多两行、超出用省略号。
    /// </summary>
    public bool FullItemName { get; set; } = true;

    /// <summary>完整显示时，名称最多占用几行（1 ~ 5）。只有名称特别长时才会用到后几行。</summary>
    public int ItemNameMaxLines { get; set; } = 3;

    /// <summary>条目宽度档位：0 = 紧凑，1 = 标准，2 = 宽松。</summary>
    public int ItemWidthMode { get; set; } = 1;

    /// <summary>分区背景不透明度 0.3 ~ 1.0。</summary>
    public double PanelOpacity { get; set; } = 0.78;

    public ZoneAppearance Appearance { get; set; } = ZoneAppearance.Dark;

    /// <summary>拖动分区时自动吸附对齐。</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>是否显示隐藏文件。</summary>
    public bool ShowHiddenFiles { get; set; }

    public ZoneSortMode DefaultSortMode { get; set; } = ZoneSortMode.Name;

    [JsonIgnore]
    public bool HasBeenInitialized { get; set; }
}
