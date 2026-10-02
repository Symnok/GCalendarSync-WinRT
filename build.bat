@echo off
setlocal
rem ==========================================================================
rem  GCalendarSync - full build
rem
rem  Usage:  build.bat [ARM|x86|both] [Debug|Release] [nobump]
rem          default: ARM Release, version bumped
rem
rem  The package version is bumped (third digit, 1.0.xx.0) on every build.
rem  WP8.1 will NOT replace an installed app with a package of the SAME version:
rem  the deploy reports success and the phone keeps running the OLD binary, so a
rem  fix looks like it did nothing. Pass "nobump" only when rebuilding something
rem  you are not going to deploy.
rem
rem  Use "both" to build ARM and x86 at the SAME version - bumping once per
rem  invocation, not once per architecture.
rem
rem  Order matters: the native TLS component is built first. It is a
rem  ProjectReference, so the app build would drive it anyway, but building it
rem  explicitly keeps the failure message close to the cause - and Core needs its
rem  .winmd present to compile against.
rem ==========================================================================

set "PLAT=%~1"

rem  build.bat clean  - wipe every build artefact. Do this when deployment reports
rem  "Deployment optimization failed due to an assembly that's not valid": that comes from the
rem  MDIL step and stale incremental output is the usual cause.
rem
rem  AppPackages is deliberately NOT removed: previously built packages are kept.
if /i "%PLAT%"=="clean" (
  echo Cleaning build output...
  for %%D in (
    "src\GCalendarSync.Core\obj"            "src\GCalendarSync.Core\bin"
    "src\GCalendarSync.BackgroundTasks\obj" "src\GCalendarSync.BackgroundTasks\bin"
    "src\GCalendarSync\obj"            "src\GCalendarSync\bin"
    "src\GCalendarSync.Tls\ARM"             "src\GCalendarSync.Tls\Win32"
  ) do if exist "%~dp0%%~D" rd /s /q "%~dp0%%~D"
  echo Clean. AppPackages left alone.
  exit /b 0
)

if "%PLAT%"=="" set "PLAT=ARM"
set "CFG=%~2"
if "%CFG%"=="" set "CFG=Release"
set "BUMP=%~3"

set "MSB=C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe"
if not exist "%MSB%" echo ERROR: MSBuild 14 not found & exit /b 1

rem The signing key is a private key and is gitignored, so a fresh clone has none and the
rem packaging step fails with APPX0104. Generate one on demand.
if not exist "%~dp0src\GCalendarSync\GCalendarSync_TemporaryKey.pfx" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-cert.ps1"
  if errorlevel 1 exit /b 1
)

if /i not "%BUMP%"=="nobump" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1"
  if errorlevel 1 exit /b 1
)
findstr /c:"<Identity" "%~dp0src\GCalendarSync\Package.appxmanifest"

rem Capture the version just stamped into the manifest, so the post-build step can find the exact
rem AppPackages folder to make self-contained (the folder name embeds the version).
set "VER="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "([xml](Get-Content -LiteralPath '%~dp0src\GCalendarSync\Package.appxmanifest')).Package.Identity.Version"`) do set "VER=%%V"

if /i "%PLAT%"=="both" (
  call :one ARM %CFG%
  if errorlevel 1 exit /b 1
  call :one x86 %CFG%
  if errorlevel 1 exit /b 1
  goto :done
)

call :one %PLAT% %CFG%
if errorlevel 1 exit /b 1

:done
echo ================ BUILD OK ================
echo Packages: src\GCalendarSync\AppPackages\
exit /b 0

:one
setlocal
set "P=%~1"
set "C=%~2"
rem The C++ component uses Win32 where the app uses x86.
set "TLSPLAT=%P%"
if /i "%P%"=="x86" set "TLSPLAT=Win32"

echo.
echo === %P% %C% : native TLS component (%TLSPLAT%) ===
call "%~dp0src\GCalendarSync.Tls\build-tls.bat" %TLSPLAT% %C%
if errorlevel 1 endlocal & exit /b 1

echo === %P% %C% : app, core and background task ===
"%MSB%" "%~dp0src\GCalendarSync\GCalendarSync.csproj" ^
  /p:Configuration=%C% /p:Platform=%P% ^
  /p:VisualStudioVersion=14.0 ^
  /v:minimal /nologo
if errorlevel 1 (
  echo ================ BUILD FAILED ^(%P% %C%^) ================
  endlocal & exit /b 1
)

rem Make the per-arch package folder a self-contained deploy folder. The packager already emits the
rem signing certificate (GCalendarSync_<ver>_%P%.cer) next to the .appx, but the VCLibs
rem dependency the native TLS component needs is buried under Dependencies\%P%\. Lift it up so a
rem fresh phone can be set up from one folder. Order on the phone stays: .cer, VCLibs, then app.
set "PKGDIR=%~dp0src\GCalendarSync\AppPackages\GCalendarSync_%VER%_%P%_Test"
if exist "%PKGDIR%\Dependencies\%P%\Microsoft.VCLibs.%P%.12.00.Phone.appx" (
  copy /y "%PKGDIR%\Dependencies\%P%\Microsoft.VCLibs.%P%.12.00.Phone.appx" "%PKGDIR%\" >nul
  echo Placed VCLibs %P% dependency and signing .cer alongside the .appx in:
  echo   %PKGDIR%
) else (
  echo WARNING: VCLibs %P% dependency not found under "%PKGDIR%\Dependencies\%P%".
)

endlocal & exit /b 0
