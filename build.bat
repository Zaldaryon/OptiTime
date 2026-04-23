@echo off
setlocal EnableDelayedExpansion
chcp 65001 >NUL 2>&1

REM REM Check if Obfuscar is available
echo Building OptiTime mod without obfuscation...
set "BUILD_TYPE=DEOBFUSCATED"
REM where obfuscar.console >NUL 2>&1
REM if %ERRORLEVEL% EQU 0 (
REM     echo Building OptiTime mod with obfuscation...
REM     set "BUILD_TYPE=OBFUSCATED"
REM ) else (
REM     echo Building OptiTime mod without obfuscation (Obfuscar not found^)...
REM     set "BUILD_TYPE=DEOBFUSCATED"
REM )
REM 
REM Clean previous builds
if exist bin rmdir /s /q bin

REM Build the project and capture output
dotnet build OptiTime.csproj --configuration Release --verbosity quiet > build_output.txt 2>&1
set BUILD_EXIT=%ERRORLEVEL%

REM Show only warnings and errors
findstr /C:"warning" /C:"error" /C:"Error" /C:"Warning" build_output.txt 2>NUL || echo.
del build_output.txt 2>NUL || echo.

if %BUILD_EXIT% EQU 0 (
    echo Build successful [!BUILD_TYPE!]

    set "MOD_VERSION="
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content 'modinfo.json' | ConvertFrom-Json).version"`) do (
        set "MOD_VERSION=%%v"
    )

    set "ZIP_NAME="

    if defined MOD_VERSION (
        set "ZIP_NAME=OptiTime-!MOD_VERSION!.zip"

        if exist "bin\OptiTime.zip" (
            ren "bin\OptiTime.zip" "!ZIP_NAME!" >NUL 2>&1 || echo.
        )

        if not exist "bin\!ZIP_NAME!" (
            set "ZIP_NAME="
        )
    )

    if not defined ZIP_NAME (
        for %%f in ("bin\OptiTime-*.zip") do (
            if exist "%%~ff" set "ZIP_NAME=%%~nxf"
        )
    )

    if not defined ZIP_NAME (
        if exist "bin\OptiTime.zip" set "ZIP_NAME=OptiTime.zip"
    )

    if defined ZIP_NAME (
        REM Remove old OptiTime versions from Mods folder
        del "%APPDATA%\VintagestoryData\Mods\OptiTime*.zip" 2>NUL || echo.
        
        REM Copy new version to VintagestoryData Mods folder
        copy "bin\!ZIP_NAME!" "%APPDATA%\VintagestoryData\Mods\" >NUL 2>&1 || echo.
        echo Mod packaged successfully: !ZIP_NAME! [!BUILD_TYPE!]
        echo Saved to: %APPDATA%\VintagestoryData\Mods\!ZIP_NAME!
    ) else (
        echo Warning: Zip package not found
        exit /b 1
    )
) else (
    echo Build failed!
    exit /b 1
)
