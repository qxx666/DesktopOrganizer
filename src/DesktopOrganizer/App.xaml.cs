using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Services;
using DesktopOrganizer.Windows;

namespace DesktopOrganizer;

public partial class App : Application
{
    // 不加 Global\ 前缀：创建全局内核对象需要 SeCreateGlobalPrivilege，
    // 普通用户进程（未提权、非服务）在部分机器上会被拒绝，直接抛
    // UnauthorizedAccessException，程序还没起来就崩了。桌面程序用本会话范围即可。
    private const string MutexName = "Local\\DesktopOrganizer.SingleInstance.9F2A";

    private Mutex? _singleInstanceMutex;
    private HotkeyService? _hotkey;
    private TrayService? _tray;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        LogStartup(e.Args);

        // 自检模式：给 CI 用的冒烟测试。跑一遍核心路径后用退出码反映结果
        // （0 = 全通过），不弹窗、不建托盘、也不去抢单实例锁。
        var selfTest = Array.Exists(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        // 只允许运行一个实例，避免两套分区框同时管理同一批文件
        if (!selfTest)
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isNewInstance);
                if (!isNewInstance)
                {
                    System.Windows.MessageBox.Show(
                        "桌面分区管家已经在运行了，请查看系统托盘图标。",
                        "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                // 拿不到单实例锁不该让程序起不来：记录一下，照常启动。
                LogFatal(ex, "单实例检测失败（忽略，继续启动）");
                _singleInstanceMutex = null;
            }
        }

        base.OnStartup(e);

        if (selfTest)
        {
            var failures = RunSelfTest();
            Shutdown(failures == 0 ? 0 : 1);
            return;
        }

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
            LogFatal(args.ExceptionObject as Exception, "非 UI 线程未捕获异常");
        };

        // 后台任务里被忽略的异常默认会直接终止进程，这里兜住
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal(args.Exception, "后台任务未观察异常");
            args.SetObserved();
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
            LogFatal(ex, "ZoneManager 初始化失败");
            System.Windows.MessageBox.Show(
                Describe(ex) + "\n\n详情见日志：\n" + LogPath,
                "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        try
        {
            SetupHotkey(notifyOnFailure: !silent);
        }
        catch (Exception ex)
        {
            // 热键注册失败不影响主功能，记下来继续
            LogFatal(ex, "热键初始化失败（忽略）");
        }

        try
        {
            SetupTray();
        }
        catch (Exception ex)
        {
            // 托盘起不来等于没法操作，只能退出
            LogFatal(ex, "托盘初始化失败");
            System.Windows.MessageBox.Show(
                Describe(ex) + "\n\n详情见日志：\n" + LogPath,
                "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

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
        LogFatal(e.Exception, "UI 线程未捕获异常");
        e.Handled = true;

        System.Windows.MessageBox.Show(
            "发生了未预期的错误，但程序会继续运行：\n\n" + Describe(e.Exception),
            "桌面分区管家", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static string LogPath =>
        System.IO.Path.Combine(ConfigService.ConfigDirectory, "error.log");

    /// <summary>把异常压缩成一句人能看懂、也便于回报的话（带上真实的异常类型）。</summary>
    private static string Describe(Exception ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            // 真正的根因往往裹在好几层里，直接展示最内层那一条更有用
            if (inner.InnerException == null)
            {
                break;
            }

            inner = inner.InnerException;
        }

        var root = inner ?? ex;
        return $"{root.GetType().Name}：{root.Message}" +
               (ReferenceEquals(root, ex) ? string.Empty : $"\n（外层：{ex.GetType().Name}）");
    }

    /// <summary>
    /// 冒烟自检：把「编译期完全看不出来、只在真机运行时才炸」的路径各跑一遍。
    /// 返回失败项数量；CI 用退出码判断结果。
    /// </summary>
    private static int RunSelfTest()
    {
        var failures = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                LogLine("  [通过] " + name);
            }
            catch (Exception ex)
            {
                failures++;
                LogLine($"  [失败] {name} —— {ex.GetType().Name}: {ex.Message}");
                LogFatal(ex, "自检：" + name);
            }
        }

        LogLine("=== 自检开始 ===");

        Check("区域性与语言标记", () =>
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            LogLine($"      区域={culture.Name}，显示名={culture.DisplayName}");
            if (string.IsNullOrEmpty(culture.Name))
            {
                throw new InvalidOperationException(
                    "语言标记为空（多半是开了 InvariantGlobalization），WPF 文本排版会拿不到字体回退信息");
            }
        });

        Check("WPF 文本排版（FormattedText）", () =>
        {
            var formatted = new FormattedText(
                "测试文件名 test 123",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11, System.Windows.Media.Brushes.Black, pixelsPerDip: 1.0);

            if (formatted.Width <= 0)
            {
                throw new InvalidOperationException("测量宽度为 0");
            }
        });

        Check("WPF 文本块布局（TextBlock 测量）", () =>
        {
            var block = new System.Windows.Controls.TextBlock
            {
                Text = "测试文件名 test 123",
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
            };

            block.Measure(new System.Windows.Size(160, 200));
            if (block.DesiredSize.Width <= 0 || block.DesiredSize.Height <= 0)
            {
                throw new InvalidOperationException($"布局尺寸异常：{block.DesiredSize}");
            }
        });

        Check("配置读写", () =>
        {
            var config = ConfigService.Load();
            ConfigService.Save(config);
        });

        Check("分区根目录", () => ZoneFolderService.EnsureFolder(ConfigService.DefaultRootFolder));

        Check("Shell 图标提取", () =>
        {
            var icon = ShellIconService.GetIcon(ConfigService.DefaultRootFolder, true);
            if (icon == null)
            {
                throw new InvalidOperationException("返回 null");
            }
        });

        Check("托盘图标", () =>
        {
            using var icon = ZoneManager.LoadAppIcon();
            if (icon == null)
            {
                throw new InvalidOperationException("返回 null");
            }
        });

        Check("文件列表加载", () => DesktopOrganizer.Models.FileItemViewModel.LoadFrom(
            ConfigService.DefaultRootFolder,
            DesktopOrganizer.Models.ZoneSortMode.Name,
            descending: false, showHidden: false, iconSize: 48,
            nameBrush: System.Windows.Media.Brushes.White,
            fullItemName: true, nameMaxLines: 3, widthMode: 1));

        LogLine($"=== 自检结束，失败 {failures} 项 ===");
        return failures;
    }

    private static void LogLine(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigService.ConfigDirectory);
            System.IO.File.AppendAllText(
                LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>启动时记一笔环境信息，出问题时能对照着看。</summary>
    private static void LogStartup(string[] args)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigService.ConfigDirectory);
            System.IO.File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动" +
                $" | .NET {Environment.Version}" +
                $" | OS {Environment.OSVersion}" +
                $" | 64位进程={Environment.Is64BitProcess}" +
                $" | DPI缩放={System.Windows.SystemParameters.PrimaryScreenWidth}x{System.Windows.SystemParameters.PrimaryScreenHeight}" +
                $" | 参数={(args.Length == 0 ? "(无)" : string.Join(" ", args))}" +
                $" | 区域={System.Globalization.CultureInfo.CurrentUICulture.Name}" +
                Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    private static void LogFatal(Exception? ex, string? context = null)
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
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context ?? "异常"}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
