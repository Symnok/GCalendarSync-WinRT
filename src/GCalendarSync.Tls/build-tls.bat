@echo off
setlocal
rem ==========================================================================
rem  GCalendarSync.Tls - WP8.1 WinRT component (C++/CX) over the ported OpenSSL
rem
rem  Usage:  build-tls.bat [ARM|Win32] [Debug|Release]
rem  Default: ARM Release
rem
rem  This build needs three non-obvious overrides. Without them it fails with
rem  errors that point in the wrong direction entirely:
rem
rem  1. PlatformToolset is v120_wp81, NOT v120. Plain "v120" gives
rem     MSB8020 "build tools for v120 cannot be found" even though VS2013 and
rem     its ARM compiler are installed. The WP8.1 toolset lives at
rem     ...\Microsoft.Cpp\v4.0\V120\Application Type\Windows Phone\8.1\
rem     Platforms\ARM\PlatformToolsets\v120_wp81.
rem
rem  2. VCTargetsPath must be forced to V120. We drive the build with MSBuild 14
rem     (VS2015), whose V140 tree has NO "Application Type" folder at all, so it
rem     cannot target WP8.1 natively. v140 also resolves the *UAP* VC runtime SDK
rem     and dies with MSB3843/MSB3844. Only VCLibs 12.0 exists for WP8.1 here.
rem
rem  3. _CppCommonExtensionTargets must be redirected to the v14.0 XAML C++
rem     targets. V120's Microsoft.Cpp.AppContainerApplication.props hardcodes
rem     MSBuild\Microsoft\WindowsXaml\v12.0\..., which is not installed on this
rem     machine (VS2013's phone tooling is incomplete); v14.0 is.
rem
rem  Net effect: VS2015 MSBuild + VS2013 v120_wp81 compiler + VS2015 XAML targets.
rem  The managed head is unaffected and builds normally on MSBuild 14.
rem ==========================================================================

set "PLAT=%~1"
if "%PLAT%"=="" set "PLAT=ARM"
set "CFG=%~2"
if "%CFG%"=="" set "CFG=Release"

set "MSB=C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe"
set "VCT=C:\Program Files (x86)\MSBuild\Microsoft.Cpp\v4.0\V120"
set "XAMLT=C:\Program Files (x86)\MSBuild\Microsoft\WindowsXaml\v14.0\Microsoft.Windows.UI.Xaml.Cpp.targets"

if not exist "%MSB%" echo ERROR: MSBuild 14 not found & exit /b 1
if not exist "%VCT%\Application Type\Windows Phone\8.1" echo ERROR: v120 WP8.1 application type not found & exit /b 1
if not exist "%XAMLT%" echo ERROR: v14.0 XAML C++ targets not found & exit /b 1

echo Building GCalendarSync.Tls  %PLAT% %CFG%  (v120_wp81)

"%MSB%" "%~dp0GCalendarSync.Tls.vcxproj" ^
  /p:Configuration=%CFG% /p:Platform=%PLAT% ^
  /p:VisualStudioVersion=14.0 ^
  /p:VCTargetsPath="%VCT%\\" ^
  /p:_CppCommonExtensionTargets="%XAMLT%" ^
  /v:minimal /nologo
if errorlevel 1 (
  echo ================ BUILD FAILED ================
  exit /b 1
)
echo ================ BUILD OK ================
exit /b 0
