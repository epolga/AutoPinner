@echo off
REM Fired every 30 minutes by the Windows scheduled task "AutoPinner".
REM Switches CWD into the repo root so DotNetEnv.TraversePath finds .env,
REM then runs the Release build with --once. Output is appended to
REM autopinner-run.log alongside this batch file for morning review.

cd /d D:\ann\Git\AutoPinner

set LOGFILE=D:\ann\Git\AutoPinner\autopinner-run.log
echo. >> "%LOGFILE%"
echo [%date% %time%] === AutoPinner cron tick === >> "%LOGFILE%"

dotnet src\AutoPinner\bin\Release\net8.0\AutoPinner.dll --once >> "%LOGFILE%" 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [%date% %time%] ERROR: AutoPinner exited with code %ERRORLEVEL% >> "%LOGFILE%"
)
