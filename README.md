# QuickShelf

Windows 托盘快捷启动工具：层级弹出菜单、悬停展开文件夹、F4 全局呼出，支持分组收藏与快捷项管理。

## 功能概览

- 左键托盘图标打开快捷列表；悬停文件夹逐层展开
- 左键打开文件/文件夹；复制图标复制绝对路径
- 星形收藏；分组管理；拖放排序与归类
- 全局热键 F4 在鼠标位置弹出菜单
- 开机启动；配置与程序同目录（`shortcuts.json` 等）

## 环境要求

- Windows
- .NET Framework 4.x（系统自带 `csc.exe` 即可）

## 编译

先关闭正在运行的 QuickShelf，再在项目目录执行：

```powershell
.\build.ps1
```

或手动：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /warnaserror /target:winexe /platform:anycpu /optimize+ `
  /out:QuickShelf.exe /win32icon:QuickShelf.ico `
  /resource:QuickShelf.ico,QuickShelf.Icon `
  /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll `
  /reference:System.Web.Extensions.dll `
  QuickShelf.cs TrayMenus.cs
```

`CreateIcon.cs` 仅用于生成图标资源，日常编译不必包含。

## 文档

- [使用说明.txt](使用说明.txt)
- [优化说明.md](优化说明.md)
- [稳定性修复说明.md](稳定性修复说明.md)

## License

MIT
