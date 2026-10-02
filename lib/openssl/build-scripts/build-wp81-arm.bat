@echo off
setlocal
rem ==========================================================================
rem  OpenSSL 1.0.2r - Windows Phone 8.1 (WinRT / app container) - ARM
rem
rem  NOTE: HKLM\...\Microsoft SDKs\Windows Phone\v8.1 points at the
rem  *Silverlight* kit. The WinRT build needs "Windows Phone Kits\8.1",
rem  so _WPKITS81 is set explicitly here instead of via ms\setVSVars.bat.
rem ==========================================================================

set "_VS12VC=C:\Program Files (x86)\Microsoft Visual Studio 12.0\VC"
set "_WPKITS81=C:\Program Files (x86)\Windows Phone Kits\8.1"

if not exist "%_VS12VC%\vcvarsall.bat" echo ERROR: VS2013 VC not found & exit /b 1
if not exist "%_WPKITS81%\lib\ARM\WindowsPhoneCore.lib" echo ERROR: WP8.1 WinRT kit not found & exit /b 1

call "%_VS12VC%\vcvarsall.bat" x86_arm
if errorlevel 1 exit /b 1

set "INCLUDE=%_WPKITS81%\Include;%_WPKITS81%\Include\abi;%_WPKITS81%\Include\mincore;%_WPKITS81%\Include\minwin;%_WPKITS81%\Include\wrl;%INCLUDE%"
set "LIB=%_WPKITS81%\lib\ARM;%_VS12VC%\lib\store\ARM"

pushd "%~dp0\.."

perl Configure no-asm no-hw no-dso VC-WINPHONE -DUNICODE -D_UNICODE
if errorlevel 1 goto err
perl util\mkfiles.pl > MINFO
if errorlevel 1 goto err
perl util\mk1mf.pl dll no-asm VC-WINPHONE > ms\ntdll-arm.mak
if errorlevel 1 goto err

perl util\mkdef.pl crypto ssl update
perl util\mkdef.pl 32 libeay > "%TEMP%\libeay32.def"
perl -ne "print unless /ENGINE_load_rsax/" "%TEMP%\libeay32.def" > ms\libeay32.def
perl util\mkdef.pl 32 ssleay > ms\ssleay32.def

nmake -f ms\ntdll-arm.mak init
if errorlevel 1 goto err
nmake -f ms\ntdll-arm.mak lib
if errorlevel 1 goto err

popd
echo ================ BUILD OK ================
exit /b 0
:err
popd
echo ================ BUILD FAILED ================
exit /b 1
