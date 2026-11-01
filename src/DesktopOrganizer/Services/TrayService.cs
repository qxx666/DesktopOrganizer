using System;
using System.Windows;
using System.Windows.Controls;
using Wf = System.Windows.Forms;

namespace DesktopOrganizer.Services;

/// <summary>系统托盘图标与菜单。</summary>
public sealed class TrayService : IDisposable
{
    private readonly Wf.NotifyIcon _notifyIcon;
    private readonly Wf.ToolStripMenuItem _toggleItem;
    private readonly Wf.ToolStripMenuItem _lockItem;
    private readonly Wf.ToolStripMenuItem _topmostItem;

    public event Action? NewZoneRequested;
    public event Action? SettingsRequested;
    public event Action? ToggleVisibilityRequested;
    /// <summary>单击 / 双击托盘图标触发：只显示分区、绝不隐藏（除非退出程序）。</summary>
    public event Action? ShowRequested;
    public event Action? LayoutLockToggled;
    public event Action? TopmostToggled;
    public event Action? OpenRootFolderRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _toggleItem = new Wf.ToolStripMenuItem("隐藏全部分区", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        _lockItem = new Wf.ToolStripMenuItem("锁定布局", null, (_, _) => LayoutLockToggled?.Invoke());
        _topmostItem = new Wf.ToolStripMenuItem("分区总在最前", null, (_, _) => TopmostToggled?.Invoke());

        var menu = new Wf.ContextMenuStrip();
        menu.Items.Add(new Wf.ToolStripMenuItem("新建分区…", null, (_, _) => NewZoneRequested?.Invoke()));
        menu.Items.Add(new Wf.ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_lockItem);
        menu.Items.Add(_topmostItem);
        menu.Items.Add(new Wf.ToolStripSeparator());
        menu.Items.Add(new Wf.ToolStripMenuItem("整理设置…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new Wf.ToolStripMenuItem("打开分区文件夹根目录", null, (_, _) => OpenRootFolderRequested?.Invoke()));
        menu.Items.Add(new Wf.ToolStripSeparator());
        menu.Items.Add(new Wf.ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new Wf.NotifyIcon
        {
            Icon = ZoneManager.LoadAppIcon(),
            Text = "桌面分区管家",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 单击 / 双击托盘图标都只负责「显示分区」，绝不隐藏（隐藏只发生在用户从菜单
        // 主动选择，或按热键，或退出程序时）。WinForms 的 NotifyIcon 在双击时也会先触发一次
        // Click，所以两者都指向 ShowRequested，幂等、不会误隐藏。
        _notifyIcon.Click += (_, _) => ShowRequested?.Invoke();
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();

        SyncMenuState();
    }

    /// <summary>把菜单勾选状态与当前配置同步。</summary>
    public void SyncMenuState()
    {
        try
        {
            var config = ZoneManager.Instance.Config;
            _lockItem.Checked = config.LayoutLocked;
            _topmostItem.Checked = config.AlwaysOnTop;
            _toggleItem.Text = ZoneManager.Instance.IsAnyVisible ? "隐藏全部分区" : "显示全部分区";

            var hotkey = string.IsNullOrWhiteSpace(config.Hotkey) ? "未设置" : config.Hotkey;
            _notifyIcon.Text = $"桌面分区管家\n单击 / 双击图标显示分区\n{hotkey} 显示 / 隐藏";
        }
        catch
        {
            // ignore
        }
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = Wf.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
