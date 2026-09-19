# 项目：桌面分区管家 DesktopOrganizer

## 定位
Windows 桌面分区工具（Fences 类）。用户把文件拖进桌面上的分区框 → 文件真实移动到该分区绑定的文件夹。

## 技术栈与铁律
- C# / WPF，net8.0-windows，`UseWPF` + `UseWindowsForms` 同时开
- **必须** `<ImplicitUsings>disable</ImplicitUsings>`：WPF 与 WinForms 混用时 Button/Application 等类型会歧义
- **必须** `<EnableWindowsTargeting>true</EnableWindowsTargeting>`：否则 Mac 上无法交叉编译 Windows 目标
- **必须** `<ApplicationHighDpiMode>SystemAware</ApplicationHighDpiMode>`：保证窗口坐标与光标坐标换算一致
- 删除文件只走 SHFileOperation + FOF_ALLOWUNDO（系统回收站），绝不永久删除
- 拖入同名文件自动改名 `xx (2).ext`，不覆盖不弹窗

## 构建
- `.\build.ps1` → 自包含单文件 63MB（默认，目标机器免装 .NET）
- `.\build.ps1 -Slim` → 0.4MB，需目标机器装 .NET 8 桌面运行时
- 体积三档实测：未压缩 138MB / 压缩 63MB / 压缩+R2R 146MB
- 本机构建环境：`~/.dotnet/dotnet`（无 brew，官方脚本装），配 `DOTNET_CLI_HOME=/tmp/dotnethome`
- Mac 只能验证编译通过，**无法验证运行时行为**

## 路径约定
- 配置：`%APPDATA%\DesktopOrganizer\config.json`
- 分区文件夹根：默认 `%USERPROFILE%\DesktopZones`，可在全局设置改
- 产物：`dist/DesktopOrganizer-独立版.exe`（dist/ 已 gitignore）

## 图标处理（重要，别改成直接传二进制）
`Assets/app.ico` 是二进制，不入库。仓库只存 `tools/app-icon.b64.txt`（base64 文本，34KB），
`build.ps1` 与两个 workflow 在编译前还原成 .ico。csproj 里图标引用带 `Condition="Exists(...)"`，
即使图标缺失也能编译（已实测 0 error）。**改图标要重新生成 b64，别只换本地 ico。**

## GitHub 推送注意
- 已连接的 GitHub 集成**没有创建仓库权限**（403），仓库必须由用户在 github.com/new 手动建
- 集成也**不能创建 tag / release**，只能推文件；发版走 `.github/workflows/release.yml`
  的 workflow_dispatch，由用户在 Actions 页面点「Run workflow」
- MCP 的 `push_files` 只接受明文（会自己 base64），二进制文件传过去会损坏
- 本机无 gh CLI、无 SSH key，git 只有 user.name=qxxhjy / qxxhjy@qq.com；GitHub 账号是 **qxx666**
