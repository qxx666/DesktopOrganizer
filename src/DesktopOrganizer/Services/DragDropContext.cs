using System.Windows;

namespace DesktopOrganizer.Services;

/// <summary>
/// 记录当前由哪个分区发起的拖拽。
/// 用于区分「分区之间互拖」和「从资源管理器拖进来」，避免自己拖回自己时误触发文件移动。
/// </summary>
public static class DragDropContext
{
    public static object? SourceZone { get; set; }

    public static bool IsInternalDrag => SourceZone != null;

    public static void Reset() => SourceZone = null;
}
