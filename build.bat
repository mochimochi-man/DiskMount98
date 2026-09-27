@echo off
rem DiskMount98.exe ‚ğƒrƒ‹ƒh‚·‚é (Windows•W€‚Ì .NET Framework 4 ‚Ì‚İ‚Å‰Â)
cd /d "%~dp0"
if not exist DiskMount98.ico powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:DiskMount98.ico /out:DiskMount98.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll DiskMount98.cs
