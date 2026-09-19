using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopOrganizer.Models;

namespace DesktopOrganizer.Services;

/// <summary>配置文件的读写。</summary>
public static class ConfigService
{
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopOrganizer");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>分区文件夹的默认根目录：%USERPROFILE%\DesktopZones</summary>
    public static string DefaultRootFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DesktopZones");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppConfig();
            }

            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 配置损坏时备份一份，避免用户布局彻底丢失
            TryBackupBrokenConfig(ex);
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, Options);

            // 原子写：先写临时文件再替换，避免写一半崩溃导致配置损坏
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // 保存失败不应打断用户操作
        }
    }

    private static void TryBackupBrokenConfig(Exception ex)
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            var backup = ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(ConfigPath, backup, overwrite: true);
            _ = ex;
        }
        catch
        {
            // ignore
        }
    }
}
