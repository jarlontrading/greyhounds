@echo off
set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://127.0.0.1:5287
set RacingTrader__Url=http://127.0.0.1:5287
set GREYHOUNDTRADER_SETTINGS_FILE=D:\Dropbox\SportunoPF\projectvariables\GreyhoundTrader.env
if exist "%~dp0GreyhoundTrader.Monitor\bin\Debug\net8.0-windows\GreyhoundTrader Monitor.exe" (
  start "GreyhoundTrader Monitor" "%~dp0GreyhoundTrader.Monitor\bin\Debug\net8.0-windows\GreyhoundTrader Monitor.exe"
)
cd /d "%~dp0GreyhoundTrader.Web"
if exist "D:\Temp\greyhoundtrader-startup.log" for %%A in ("D:\Temp\greyhoundtrader-startup.log") do if %%~zA GEQ 26214400 (
  del /q "D:\Temp\greyhoundtrader-startup.previous.log" 2>nul
  move /y "D:\Temp\greyhoundtrader-startup.log" "D:\Temp\greyhoundtrader-startup.previous.log" >nul
)
"%~dp0GreyhoundTrader.Web\bin\Debug\net8.0\GreyhoundTrader.Web.exe" >> "D:\Temp\greyhoundtrader-startup.log" 2>&1
