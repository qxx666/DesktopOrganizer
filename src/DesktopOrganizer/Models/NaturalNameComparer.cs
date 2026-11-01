using System;
using System.Collections.Generic;

namespace DesktopOrganizer.Models;

/// <summary>
/// 自然排序比较器：数字段按数值比较（"2" &lt; "10"），文本段按不区分大小写比较。
/// 行为与 Windows 资源管理器「按名称」基本一致，解决 "10、xx" 被排到 "1、xx" 前面的问题。
/// </summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    /// <summary>全局单例。</summary>
    public static readonly NaturalNameComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x == null)
        {
            return y == null ? 0 : -1;
        }

        if (y == null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i];
            char cy = y[j];

            bool xDigit = char.IsDigit(cx);
            bool yDigit = char.IsDigit(cy);

            if (xDigit && yDigit)
            {
                // 两边都从数字开始：整段提取数字块，按数值比较（带溢出保护）
                bool xOverflow = false, yOverflow = false;
                ulong nx = 0, ny = 0;
                int xLen = 0, yLen = 0;

                while (i < x.Length && char.IsDigit(x[i]))
                {
                    if (!xOverflow)
                    {
                        if (nx > (ulong.MaxValue - 9) / 10)
                        {
                            xOverflow = true;
                        }
                        else
                        {
                            nx = nx * 10 + (ulong)(x[i] - '0');
                        }
                    }

                    xLen++;
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    if (!yOverflow)
                    {
                        if (ny > (ulong.MaxValue - 9) / 10)
                        {
                            yOverflow = true;
                        }
                        else
                        {
                            ny = ny * 10 + (ulong)(y[j] - '0');
                        }
                    }

                    yLen++;
                    j++;
                }

                if (xOverflow != yOverflow)
                {
                    // 一方溢出（极长数字段）：溢出方视为更大，排到后面
                    return xOverflow ? 1 : -1;
                }

                if (nx != ny)
                {
                    return nx < ny ? -1 : 1;
                }

                if (xLen != yLen)
                {
                    // 数值相等但长度不同（前导零数量不同）：更短（前导零更少）的在前
                    return xLen < yLen ? -1 : 1;
                }

                continue;
            }

            if (xDigit != yDigit)
            {
                // 一边是数字块、一边是文本：数字块整体排在非数字之前（与资源管理器一致）
                return xDigit ? -1 : 1;
            }

            // 两边都非数字（含中文）：整块提取后按不区分大小写比较
            int xStart = i, yStart = j;
            while (i < x.Length && !char.IsDigit(x[i]))
            {
                i++;
            }

            while (j < y.Length && !char.IsDigit(y[j]))
            {
                j++;
            }

            int cmp = string.Compare(
                x.Substring(xStart, i - xStart),
                y.Substring(yStart, j - yStart),
                StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        if (i < x.Length)
        {
            return 1;
        }

        if (j < y.Length)
        {
            return -1;
        }

        return 0;
    }
}
