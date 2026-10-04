@echo off
rem Builds NamazBar (taskbar utility) and NamazWidget (lock screen widget) into pkg\
rem with the C# compiler that ships with Windows (.NET Framework 4.x). No Visual Studio needed.
rem Needs: Windows SDK (Windows.winmd); for the widget also Windows App Runtime 1.8.
rem Usage: build.bat            - build, register pkg\ as an installed app (Developer Mode) and start it
rem        build.bat /nostart   - build only (used by CI)
rem        build.bat /pack      - build release\NamazBar.exe: one file that installs/updates and starts NamazBar
rem                               (bump Version in AppxManifest.xml first)
setlocal
cd /d "%~dp0"
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%NET%\csc.exe
if not exist "%CSC%" (echo csc.exe not found - .NET Framework 4.x is required & exit /b 1)

set WINMD=
for /d %%d in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\10.*") do if exist "%%d\Windows.winmd" set WINMD=%%d\Windows.winmd
if not defined WINMD (echo Windows.winmd not found - install the Windows SDK & exit /b 1)

set WAR=
for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "(Get-AppxPackage -Name Microsoft.WindowsAppRuntime.1.8 | Where-Object Architecture -eq 'X64' | Sort-Object Version | Select-Object -Last 1).InstallLocation"`) do set WAR=%%p

rem stop every running copy (including test builds) so the exe files can be replaced
taskkill /f /im "NamazBar*" >nul 2>&1
taskkill /f /im NamazWidget.exe >nul 2>&1

if not exist pkg\Public mkdir pkg\Public
rem makeappx skips empty folders, but the manifest's PublicFolder must exist in the package
echo NamazBar widget> pkg\Public\readme.txt

set COMMON=/nologo /codepage:65001 /optimize+ /target:winexe /win32manifest:app.manifest ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /r:"%NET%\System.Runtime.dll" /r:"%NET%\System.Runtime.WindowsRuntime.dll" /r:"%WINMD%" ^
  /resource:..\fonts\GoogleSans-400.ttf,GS400.ttf /resource:..\fonts\GoogleSans-500.ttf,GS500.ttf ^
  /resource:..\fonts\GoogleSans-700.ttf,GS700.ttf /resource:..\fonts\GoogleSansFlex-900.ttf,GSF900.ttf

rem 1. widget provider; it also draws the package images and NamazBar.ico
set ICON=
if exist "%WAR%\Microsoft.Windows.Widgets.winmd" (
  "%CSC%" %COMMON% /platform:x64 /main:NamazBar.WidgetProgram /out:pkg\NamazWidget.exe ^
    /r:"%WAR%\Microsoft.Windows.Widgets.winmd" NamazBar.cs NamazWidget.cs Chat.cs
  if errorlevel 1 (echo Build FAILED: NamazWidget.exe & exit /b 1)
  pkg\NamazWidget.exe /assets pkg\Assets
  if errorlevel 1 (echo Build FAILED: widget images & exit /b 1)
  set ICON=/win32icon:pkg\Assets\NamazBar.ico
) else echo WARNING: Windows App Runtime 1.8 x64 not found - widget skipped

rem 2. taskbar utility (works both from the package and as a portable exe)
"%CSC%" %COMMON% /platform:anycpu %ICON% /out:pkg\NamazBar.exe NamazBar.cs Chat.cs
if errorlevel 1 (echo Build FAILED: NamazBar.exe & if /i not "%~1"=="/nostart" pause & exit /b 1)
copy /y AppxManifest.xml pkg\AppxManifest.xml >nul
echo Build OK: pkg\
if /i "%~1"=="/nostart" exit /b 0
if /i "%~1"=="/pack" goto pack

rem an installed signed copy (from release\NamazBar.exe) blocks registering the build folder - remove it first
powershell -NoProfile -Command "$p = Get-AppxPackage NamazBar.Widget; if ($p -and -not $p.IsDevelopmentMode) { Remove-AppxPackage -Package $p.PackageFullName }"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-AppxPackage -Register '%~dp0pkg\AppxManifest.xml' -ForceApplicationShutdown -ForceUpdateFromAnyVersion"
if errorlevel 1 (echo Registration FAILED - is Developer Mode on? Settings - System - Advanced - For developers & pause & exit /b 1)
rem the widget service caches providers; the installed app now starts with Windows instead of the old registry entry
taskkill /f /im WidgetService.exe >nul 2>&1
taskkill /f /im WidgetBoard.exe >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v NamazBar /f >nul 2>&1
powershell -NoProfile -Command "$p = Get-AppxPackage NamazBar.Widget; Start-Process ('shell:AppsFolder\' + $p.PackageFamilyName + '!NamazBar')"
echo Started. Widget: Settings - Personalization - Lock screen - Widgets - Add widget - NamazBar
exit /b 0

:pack
rem signed package -> embedded into one exe together with the certificate and the manifest (for its version)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1"
if errorlevel 1 (echo Pack FAILED & exit /b 1)
if not exist release mkdir release
"%CSC%" %COMMON% /platform:anycpu %ICON% /out:release\NamazBar.exe ^
  /resource:dist\NamazBar.msix,NamazBar.msix /resource:dist\NamazBar.cer,NamazBar.cer ^
  /resource:AppxManifest.xml,NamazBar.AppxManifest.xml NamazBar.cs Chat.cs
if errorlevel 1 (echo Build FAILED: release\NamazBar.exe & exit /b 1)
echo Release OK: release\NamazBar.exe
