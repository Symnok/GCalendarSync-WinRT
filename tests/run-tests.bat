@echo off
setlocal
rem ==========================================================================
rem  GCalendarSync - tests for the platform-neutral half of the sync engine
rem
rem  Usage:  run-tests.bat
rem
rem  Compiles the REAL Core sources - no copies - with the desktop C# compiler and
rem  runs them. That works because everything except opening the contact store,
rem  saving a StoredContact and the display picture is ordinary C# over an
rem  IDictionary<string, object>, so the vCard parser, the writer/patcher, the slot
rem  mapping and the change fingerprint can all be exercised without a phone.
rem
rem  What still needs a device is listed in docs\TESTING.md.
rem
rem  Exit code is 0 when every check passed.
rem ==========================================================================

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" echo ERROR: no .NET Framework 4 C# compiler found & exit /b 1

set "CORE=%~dp0..\src\GCalendarSync.Core"
set "OUT=%~dp0bin"
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /warn:4 /out:"%OUT%\ICalTests.exe" ^
  "%~dp0ICalTests.cs" ^
  "%CORE%\Models\VEvent.cs" ^
  "%CORE%\Services\ICalText.cs" ^
  "%CORE%\Services\ICalParser.cs" ^
  "%CORE%\Services\CalendarKinds.cs" ^
  "%CORE%\Services\AppointmentMapping.cs"
if errorlevel 1 (
  echo ================ COMPILE FAILED ================
  exit /b 1
)

"%OUT%\ICalTests.exe"
exit /b %errorlevel%
