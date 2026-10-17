@echo off
rem Builds a single WaveSync.exe into .\publish (needs the .NET 8 SDK or newer: https://dot.net)
dotnet publish "%~dp0WaveSync\WaveSync.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
  echo.
  echo Build failed. Copy the errors above and send them over.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\WaveSync.exe
pause
