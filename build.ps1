$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /warnaserror /target:winexe /platform:anycpu /optimize+ /out:QuickShelf.exe /win32icon:QuickShelf.ico /resource:QuickShelf.ico,QuickShelf.Icon /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll QuickShelf.cs TrayMenus.cs
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
} finally { Pop-Location }
