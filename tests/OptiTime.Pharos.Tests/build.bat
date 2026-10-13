@echo off
setlocal
if not defined VINTAGE_STORY (
    echo Set VINTAGE_STORY to an installed Vintage Story directory.
    exit /b 1
)
if not exist "%VINTAGE_STORY%\VintagestoryAPI.dll" (
    echo Set VINTAGE_STORY to an installed Vintage Story directory.
    exit /b 1
)
pushd "%~dp0"
dotnet build OptiTime.Pharos.Tests.csproj -c Release --nologo
set "BUILD_EXIT=%ERRORLEVEL%"
popd
exit /b %BUILD_EXIT%
