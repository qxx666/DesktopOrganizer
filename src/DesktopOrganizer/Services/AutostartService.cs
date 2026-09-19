using System;
using System.IO;
using Microsoft.Win32;

namespace DesktopOrganizer.Services;

/// <summary>开机自启（写入当前用户的 Run 注册表项，不需要管理员权限）。</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopOrganizer";

    private static string? ExecutablePath
    {
        get
        {
            try
            {
                // 单文件发布、普通发布都能拿到正确的 exe 路径
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                // 兜底：从进程主模块取。注意 Assembly.Location 在单文件模式下返回空串，不能用。
                var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
                var fileName = module?.FileName;
                if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                {
                    return fileName;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key == null)
            {
                return false;
            }

            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\" --silent");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
