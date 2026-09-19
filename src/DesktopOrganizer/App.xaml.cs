using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Services;
using DesktopOrganizer.Windows;

namespace DesktopOrganizer;

public partial class App : Application
{
    private const string MutexName = "Global\\DesktopOrganizer.SingleInstance.9F2A";

    private Mutex? _singleInstanceMutex;
    private HotkeyService? _hotkey;
    private TrayService? _tray;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 只允许运行一个实例，避免两套分区框同时管理同一批文件
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show(
                "桌面分区管家已经在运行了，请查看系统托盘图标。",
                "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 让 WinForms 的文件夹选择对话框用上现代视觉样式
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        }
        catch
        {
            // ignore
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 未捕获异常不应该让整个程序直接崩掉
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogFatal(args.ExceptionObject as Exception);
        };

        // 命令行 --silent 表示由开机自启拉起，不弹任何提示
        var silent = Array.Exists(e.Args, a =>
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        try
        {
            ZoneManager.Instance.Initialize();
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            System.Windows.MessageBox.Show(
                "初始化失败：\n" + ex.Message + "\n\n详情见日志：\n" + LogPath,
                "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        SetupHotkey(notifyOnFailure: !silent);
        SetupTray();

        if (!silent)
        {
            _tray?.ShowBalloon("桌面分区管家已启动",
                $"把文件拖进分区框即可归类。按 {ZoneManager.Instance.Config.Hotkey} 显示 / 隐藏全部分区。");
        }
    }

    private void SetupHotkey(bool notifyOnFailure)
    {
        _hotkey = new HotkeyService();
        _hotkey.Pressed += () =>
        {
            ZoneManager.Instance.ToggleAll();
            _tray?.SyncMenuState();
        };

        var gesture = ZoneManager.Instance.Config.Hotkey;
        if (!_hotkey.Register(gesture) && notifyOnFailure)
        {
            _tray?.ShowBalloon("快捷键注册失败",
                $"{gesture} 可能已被其它程序占用，可在「整理设置」里换一个组合。");
        }
    }

    private void SetupTray()
    {
        _tray = new TrayService();

        _tray.NewZoneRequested += () =>
        {
            ZoneManager.Instance.AddZoneInteractive(null);
            _tray.SyncMenuState();
        };

        _tray.SettingsRequested += () =>
        {
            var window = new SettingsWindow();
            window.ShowDialog();
            ApplySettingsAfterChange();
        };

        _tray.ToggleVisibilityRequested += () =>
        {
            ZoneManager.Instance.ToggleAll();
            _tray.SyncMenuState();
        };

        _tray.LayoutLockToggled += () =>
        {
            var config = ZoneManager.Instance.Config;
            config.LayoutLocked = !config.LayoutLocked;
            ZoneManager.Instance.SaveNow();
            _tray.SyncMenuState();
        };

        _tray.TopmostToggled += () =>
        {
            var config = ZoneManager.Instance.Config;
            config.AlwaysOnTop = !config.AlwaysOnTop;
            ZoneManager.Instance.ApplyTopmost();
            ZoneManager.Instance.SaveNow();
            _tray.SyncMenuState();
        };

        _tray.OpenRootFolderRequested += () =>
        {
            var root = ZoneManager.Instance.RootFolder;
            ZoneFolderService.EnsureFolder(root);
            ZoneFolderService.ShellOpen(root);
        };

        _tray.ExitRequested += ExitApplication;
    }

    /// <summary>供设置窗口在保存后回调，刷新快捷键 / 托盘 / 全部分区。</summary>
    internal void ApplySettingsAfterChange()
    {
        var config = ZoneManager.Instance.Config;

        var hotkeyOk = _hotkey?.Register(config.Hotkey) ?? false;

        ZoneManager.Instance.ApplyAppearance();
        ZoneManager.Instance.ApplyTopmost();
        ZoneManager.Instance.RefreshAll();

        if (!AutostartService.Set(config.StartWithWindows) && config.StartWithWindows)
        {
            _tray?.ShowBalloon("开机自启设置失败", "写入启动项时被系统拒绝了，可以手动把程序快捷方式放进「启动」文件夹。");
        }

        _tray?.SyncMenuState();

        if (!hotkeyOk && !string.IsNullOrWhiteSpace(config.Hotkey))
        {
            _tray?.ShowBalloon("快捷键没能注册", $"{config.Hotkey} 可能已被别的程序占用，换一个组合试试。");
        }
    }

    internal void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        try
        {
            ZoneManager.Instance.SaveNow();
        }
        catch
        {
            // ignore
        }

        _tray?.Dispose();
        _hotkey?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ZoneManager.Instance.SaveNow();
        }
        catch
        {
            // ignore
        }

        _tray?.Dispose();
        _hotkey?.Dispose();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        e.Handled = true;

        System.Windows.MessageBox.Show(
            "发生了未预期的错误，但程序会继续运行：\n\n" + e.Exception.Message,
            "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static string LogPath =>
        System.IO.Path.Combine(ConfigService.ConfigDirectory, "error.log");

    private static void LogFatal(Exception? ex)
    {
        if (ex == null)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(ConfigService.ConfigDirectory);
            System.IO.File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
