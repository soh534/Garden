@echo off
REM Garden launcher: the one thing this machine starts at logon (Startup shortcut, minimized).
REM Waits for the phone first -- Garden asserts the scrcpy handshake at startup, and a logon
REM before USB settles would kill it -- then runs the engine from src (dotnet run rebuilds if
REM the source changed; config bot.autoStart arms the bot). A crash (non-zero exit) relaunches
REM after 30s; a typed `quit` (exit 0) ends it. CRLF + ASCII only (cmd on a CP932 console
REM eats leading characters on bare-LF lines).
title Garden
cd /d "%~dp0src"
:loop
echo [launch] waiting for the phone (adb wait-for-device)...
adb wait-for-device
dotnet run
if errorlevel 1 (
    echo [launch] Garden exited abnormally -- relaunching in 30s
    timeout /t 30 /nobreak >nul
    goto loop
)
