@echo off
setlocal

REM ============================================================================
REM  ModernImageViewer - command-line equivalent of the VS Publish button.
REM
REM  This invokes the SAME publish profile Visual Studio uses, so the two can
REM  never drift apart. All settings live in the .pubxml, not in this file.
REM
REM  NOTE ON THE PROFILE NAME: Visual Studio is configured to use
REM  "win-arm64.pubxml", but that file actually contains x64 settings
REM  (Platform=x64, RuntimeIdentifier=win-x64). The name is misleading but
REM  it IS the profile your VS Publish button uses. Renaming it would break
REM  the VS profile selection, so it is left alone deliberately.
REM
REM  Settings it applies: self-contained, win-x64, loose files (not single
REM  file), no trimming, no ReadyToRun, output to Apps\ModernImageViewer.
REM
REM  Usage:
REM    publish.bat            publish, then smoke-test the result
REM    publish.bat nosmoke    publish only
REM ============================================================================

set "PROFILE=win-arm64"
set "PROJ=%~dp0ModernImageViewer.csproj"

REM Must match <PublishDir> in Properties\PublishProfiles\%PROFILE%.pubxml
set "OUTDIR=C:\Users\chan_\OneDrive\Apps\ModernImageViewer"

echo ============================================================
echo  Publishing ModernImageViewer
echo  Profile: %PROFILE%.pubxml  (same one Visual Studio uses)
echo  Output : %OUTDIR%
echo ============================================================
echo.

dotnet publish "%PROJ%" -p:PublishProfile=%PROFILE% -p:Platform=x64 --nologo

if errorlevel 1 goto :failed
if not exist "%OUTDIR%\ModernImageViewer.exe" goto :failed

echo.
echo Publish reported success. Verifying output...

REM --- sanity check -------------------------------------------------------
REM A part-installed .NET SDK can produce a build that compiles cleanly but
REM crashes on startup, with framework assemblies published stripped.
REM System.Private.Xml.dll is a reliable canary: ~7.6 MB healthy, ~3 MB bad.
for %%F in ("%OUTDIR%\System.Private.Xml.dll") do set "XMLSIZE=%%~zF"
if not defined XMLSIZE goto :suspect
if %XMLSIZE% LSS 5000000 goto :suspect
echo   [ok] Framework assemblies look intact.

if /i "%~1"=="nosmoke" goto :done

REM --- smoke test ---------------------------------------------------------
REM Skipped if the app is already running, so an open window is never killed.
tasklist /fi "imagename eq ModernImageViewer.exe" 2>nul | find /i "ModernImageViewer.exe" >nul
if not errorlevel 1 (
  echo   [skip] ModernImageViewer already running - smoke test skipped.
  goto :done
)

echo   Launching to confirm it starts...
start "" "%OUTDIR%\ModernImageViewer.exe"
REM ping, not "timeout" - timeout fails if stdin is redirected (publish.bat > log.txt)
ping -n 7 127.0.0.1 >nul 2>&1
tasklist /fi "imagename eq ModernImageViewer.exe" 2>nul | find /i "ModernImageViewer.exe" >nul
if errorlevel 1 goto :crashed
taskkill /im ModernImageViewer.exe /f >nul 2>&1
echo   [ok] App started and stayed running.

:done
echo.
echo ============================================================
echo  PUBLISH COMPLETE
echo  %OUTDIR%
echo ============================================================
if /i not "%~1"=="nopause" if /i not "%~2"=="nopause" pause
exit /b 0

:suspect
echo.
echo ============================================================
echo  WARNING - output looks wrong
echo.
echo  System.Private.Xml.dll is smaller than expected, meaning the
echo  framework assemblies were published stripped. This build will
echo  most likely crash on startup.
echo.
echo  Usual cause: a .NET SDK or Visual Studio update part-way
echo  through installing. Check this returns the same answer twice,
echo  then publish again:
echo.
echo      dotnet --list-sdks
echo ============================================================
pause
exit /b 2

:crashed
echo.
echo ============================================================
echo  SMOKE TEST FAILED - the app exited immediately.
echo  The published output is not usable. See the note above about
echo  checking "dotnet --list-sdks" for an in-progress SDK update.
echo ============================================================
pause
exit /b 3

:failed
echo.
echo ============================================================
echo  PUBLISH FAILED - see errors above.
echo  Output directory may be incomplete.
echo ============================================================
pause
exit /b 1
