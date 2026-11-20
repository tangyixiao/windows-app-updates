# Windows 应用更新

一个基于 WinGet 的中文 Windows 桌面工具。打开即可查看应用的已安装版本和可更新版本，按名称或包 ID 搜索，勾选后逐项更新。安装前可以核对完整清单，安装过程中可以在当前应用完成后停止。最近 100 条更新记录保存在本机。

## 运行

在 Windows x64 上准备 .NET 10 桌面运行时和带有 WinGet 的 Windows 应用安装程序。在 GitHub Release 下载并双击 `WindowsAppUpdates-win-x64.exe`。[下载最新版](https://github.com/tangyixiao/windows-app-updates/releases/latest) · [.NET 10 桌面运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) · [WinGet](https://learn.microsoft.com/zh-cn/windows/package-manager/winget/)

数据来自 WinGet 社区源。首版支持英文、简体中文和繁体中文的 WinGet 表格格式。Microsoft Store 专属应用和 Windows 系统补丁由各自的更新渠道处理。[使用说明](使用说明.md)

## 从源码构建

在仓库根目录使用 .NET 10 SDK 执行以下命令，生成的程序位于 `package` 目录。

```powershell
dotnet publish .\Source\WindowsAppUpdates.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\package
```

项目采用 [Apache License 2.0](LICENSE)。
