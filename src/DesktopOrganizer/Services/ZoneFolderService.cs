using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopOrganizer.Services;

/// <summary>分区文件夹的创建与文件搬运。</summary>
public static class ZoneFolderService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>把分区名下可能出现的非法字符替换掉。</summary>
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "新分区";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim(' ', '.');

        if (string.IsNullOrEmpty(cleaned))
        {
            return "新分区";
        }

        if (ReservedNames.Contains(cleaned))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>确保目录存在。</summary>
    public static bool EnsureFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            Directory.CreateDirectory(folder);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在指定目录下为分区名分配一个不冲突的文件夹路径。</summary>
    public static string BuildZoneFolder(string rootFolder, string zoneName)
    {
        var baseName = SanitizeFolderName(zoneName);
        var candidate = Path.Combine(rootFolder, baseName);

        var index = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            // 目录已存在时，如果它是空的就直接复用，避免用户看到一堆「工作 (2)」
            if (Directory.Exists(candidate) && !Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                return candidate;
            }

            candidate = Path.Combine(rootFolder, $"{baseName} ({index})");
            index++;
        }

        return candidate;
    }

    /// <summary>判断两个路径是否指向同一个目录（忽略大小写与结尾分隔符差异）。</summary>
    public static bool IsSameFolder(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断某个路径是否就位于指定目录的第一层。</summary>
    public static bool IsDirectChildOf(string path, string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            return parent != null && IsSameFolder(parent, folder);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在目标目录里生成一个不冲突的完整路径（同名时追加 " (2)"）。</summary>
    public static string GetUniqueTargetPath(string targetFolder, string sourcePath)
    {
        var isDirectory = Directory.Exists(sourcePath);
        var rawName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rawName))
        {
            rawName = "未命名";
        }

        var name = Path.GetFileNameWithoutExtension(rawName);
        var extension = isDirectory ? string.Empty : Path.GetExtension(rawName);

        var candidate = Path.Combine(targetFolder, name + extension);
        var index = 2;

        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(targetFolder, $"{name} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    /// <summary>搬运结果。</summary>
    public readonly record struct MoveResult(int Moved, int Skipped, int Failed, string FirstError)
    {
        public bool HasError => Failed > 0;
    }

    /// <summary>
    /// 把一批路径移动进目标目录。已经在目标目录里的会被跳过（而不是报错）。
    /// 不做任何删除操作，移动失败时原文件保持不动。
    /// </summary>
    public static MoveResult MoveInto(IEnumerable<string> sources, string targetFolder)
    {
        if (!EnsureFolder(targetFolder))
        {
            return new MoveResult(0, 0, 0, "分区文件夹不可用");
        }

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        var firstError = string.Empty;

        foreach (var source in sources)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                var isDirectory = Directory.Exists(source);
                var isFile = File.Exists(source);

                if (!isDirectory && !isFile)
                {
                    skipped++;
                    continue;
                }

                var sourceParent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar));
                if (sourceParent != null && IsSameFolder(sourceParent, targetFolder))
                {
                    // 已经在目标分区里了，什么都不用做
                    skipped++;
                    continue;
                }

                // 不允许把分区文件夹本身拖进自己
                if (isDirectory && IsSameFolder(source, targetFolder))
                {
                    skipped++;
                    continue;
                }

                var target = GetUniqueTargetPath(targetFolder, source);

                if (isDirectory)
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target);
                }

                moved++;
            }
            catch (Exception ex)
            {
                failed++;
                if (string.IsNullOrEmpty(firstError))
                {
                    firstError = ex.Message;
                }
            }
        }

        return new MoveResult(moved, skipped, failed, firstError);
    }

    /// <summary>把文件移到回收站。失败时返回 false，绝不直接永久删除。</summary>
    public static bool MoveToRecycleBin(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return false;
            }

            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = NativeMethods.FO_DELETE,

                // SHFileOperation 要求以双 null 结尾的多字符串
                pFrom = path + "\0\0",
                pTo = null!,
                fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO
                                  | NativeMethods.FOF_NOCONFIRMATION
                                  | NativeMethods.FOF_NOERRORUI
                                  | NativeMethods.FOF_SILENT),
                lpszProgressTitle = null!,
            };

            var result = NativeMethods.SHFileOperation(ref op);

            // 返回 0 表示成功；操作被用户取消时也算未删除
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开资源管理器并选中指定文件。</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            var args = File.Exists(path) || Directory.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>用系统默认程序打开。</summary>
    public static bool ShellOpen(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
