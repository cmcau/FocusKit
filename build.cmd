@echo off
rem Builds FocusKit.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /unsafe ^
  /out:FocusKit.exe /win32manifest:app.manifest ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  FocusKit.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built FocusKit.exe
