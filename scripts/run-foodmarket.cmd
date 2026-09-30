@echo off
cd /d "%~dp0"
set /a wait_count=0
:wait
if exist "%~dp0.foodmarket-update\pending" (
    set /a wait_count+=1
    if %wait_count% GEQ 60 (
        echo Update helper did not finish. Check .foodmarket-update\update.log. 1>&2
        exit /b 1
    )
    ping -n 3 127.0.0.1 >nul
    goto wait
)
"%~dp0FoodMarket.exe"
if exist "%~dp0.foodmarket-update\pending" goto wait
exit /b %errorlevel%
