@echo off
rem DeadBand build script.
rem Needs the csc.exe shipped with .NET Framework 4.x (present on Win10/11).
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe

set SRC=%~dp0src
set OUT=%~dp0dist
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /win32icon:"%~dp0assets\deadband.ico" ^
  /out:"%OUT%\DeadBand.exe" ^
  "%SRC%\Interop.cs" "%SRC%\Model.cs" "%SRC%\ZoneHost.cs" "%SRC%\Calibrator.cs" "%SRC%\ColorPicker.cs" "%SRC%\AudioSpectrum.cs" "%SRC%\Spectrum.cs" "%SRC%\Clock.cs" "%SRC%\ClockSettings.cs" "%SRC%\Caption.cs" "%SRC%\Pomodoro.cs" "%SRC%\SettingsForm.cs" "%SRC%\Autostart.cs" "%SRC%\Hotkey.cs" "%SRC%\Onboarding.cs" "%SRC%\AssemblyInfo.cs" "%SRC%\Program.cs"

if errorlevel 1 goto fail

rem Ship the userscript next to the exe. DeadBand serves it over the local
rem port, so the browser can install / update it without any copy-paste.
if not exist "%OUT%\tools" mkdir "%OUT%\tools"
if exist "%~dp0tools\*.user.js" copy /Y "%~dp0tools\*.user.js" "%OUT%\tools\" >nul
if exist "%~dp0assets\deadband.ico" copy /Y "%~dp0assets\deadband.ico" "%OUT%\" >nul

echo BUILD OK -^> %OUT%\DeadBand.exe
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
