@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe at %CSC%
    exit /b 1
)
if not exist build mkdir build
"%CSC%" /nologo /target:winexe /out:build\DirStat.exe /optimize+ /platform:x64 /langversion:5 /unsafe ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    src\Program.cs src\Win32.cs src\Scanner.cs src\TreeNode.cs src\TreemapRenderer.cs src\OpenDialog.cs src\MainForm.cs src\AssemblyInfo.cs
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built build\DirStat.exe
