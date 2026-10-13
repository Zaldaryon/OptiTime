@echo off
setlocal
if not defined VINTAGE_STORY (
    echo Set VINTAGE_STORY to an installed Vintage Story directory.
    exit /b 1
)
if not exist "%VINTAGE_STORY%\VintagestoryAPI.dll" (
    echo VINTAGE_STORY does not contain VintagestoryAPI.dll.
    exit /b 1
)
pushd "%~dp0"
dotnet build OptiTime.NativeProbe.csproj -c Release --nologo
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
