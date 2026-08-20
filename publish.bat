@echo off
setlocal enabledelayedexpansion

rem ============================================================================
rem  WeaveFXP - produce a SHIPPABLE single-exe build.
rem
rem  bin\Release\ is normal .NET BUILD output and may contain loose DLLs. That is
rem  not what you ship. `dotnet publish` creates the one self-contained file.
rem
rem  Result:  Release\win-x64\WeaveFXP.exe     (one file, no .NET install needed)
rem           Release\linux-x64\WeaveFXP
rem           Release\linux-arm64\WeaveFXP     (ARM64 Linux)
rem
rem  Usage:
rem           publish.bat                      publish all default runtimes
rem           publish.bat win-x64              publish only Windows x64
rem           publish.bat win-x64 linux-x64    publish selected runtimes
rem ============================================================================

set "ROOT=%~dp0"
set "OUT=%ROOT%Release"
set "ZIPOUT=%OUT%\zips"
set "WORK=%TEMP%\WeaveFXP-publish-%RANDOM%-%RANDOM%"
set "PROJ=%ROOT%WeaveFxp.Web\WeaveFxp.Web.csproj"
set "VERSION=1.0.0"

if not exist "%PROJ%" (
    echo.
    echo   Project not found:
    echo   %PROJ%
    echo.
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo   dotnet SDK not found on PATH.
    echo   Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"
if not exist "%ZIPOUT%" mkdir "%ZIPOUT%"
if not exist "%WORK%" mkdir "%WORK%"

rem Clean old one-folder release files that may still sit directly under Release\.
rem This release lives only in Release\<runtime-id>.
del /q "%OUT%\*" 2>nul

echo.
echo === Restoring ===
dotnet restore "%PROJ%" || goto fail

for /f "usebackq delims=" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "[xml]$p=Get-Content '%PROJ%'; $v=$p.Project.PropertyGroup.Version | Select-Object -First 1; if ($v) { $v } else { '1.0.0' }"`) do set "VERSION=%%V"

if "%~1"=="" (
    set "RIDS=win-x64 linux-x64 linux-arm64"
) else (
    set "RIDS=%*"
)

for %%R in (!RIDS!) do (
    echo.
    echo === Publishing %%R ===

    rem Preserve runtime data/state across rebuilds. The executable is replaceable;
    rem data\ belongs to the user and must survive publishing.
    set "PRESERVE_DATA=%WORK%\data-%%R"
    if exist "!PRESERVE_DATA!" rd /s /q "!PRESERVE_DATA!"
    if exist "%OUT%\%%R\data" move "%OUT%\%%R\data" "!PRESERVE_DATA!" >nul

    rem Wiped first: publish does not delete stale files from earlier runs.
    if exist "%OUT%\%%R" rd /s /q "%OUT%\%%R"
    if exist "%WORK%\bin\%%R" rd /s /q "%WORK%\bin\%%R"

    dotnet publish "%PROJ%" ^
        -c Release ^
        -r %%R ^
        --self-contained true ^
        -p:BaseOutputPath="%WORK%\bin\%%R\\" ^
        -p:PublishSingleFile=true ^
        -p:IncludeAllContentForSelfExtract=true ^
        -p:EnableCompressionInSingleFile=true ^
        -p:PublishTrimmed=false ^
        -p:DebugType=none ^
        -o "%OUT%\%%R" || goto fail

    rem Keep the release folder to the executable only. Static assets are embedded
    rem or bundled into the single file. data\ is created on first run next to it.
    del /q "%OUT%\%%R\*.pdb" 2>nul
    del /q "%OUT%\%%R\appsettings*.json" 2>nul
    del /q "%OUT%\%%R\*.staticwebassets*.json" 2>nul
    del /q "%OUT%\%%R\web.config" 2>nul
    if exist "%OUT%\%%R\wwwroot" rd /s /q "%OUT%\%%R\wwwroot"
    if exist "%OUT%\%%R\bin" rd /s /q "%OUT%\%%R\bin"
    if exist "!PRESERVE_DATA!" move "!PRESERVE_DATA!" "%OUT%\%%R\data" >nul

    rem Create the GitHub release asset for this runtime. Runtime data is never
    rem packaged; the app creates data\ next to the executable on first start.
    set "ZIP=%ZIPOUT%\WeaveFXP-v%VERSION%-%%R.zip"
    if exist "!ZIP!" del /q "!ZIP!" 2>nul
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%\%%R\WeaveFXP*' -DestinationPath '!ZIP!' -Force" || goto fail
)

echo.
echo === Done ===
echo.
if exist "%OUT%\win-x64" dir /b "%OUT%\win-x64"
echo.
echo   Windows    : Release\win-x64\WeaveFXP.exe
echo   Linux      : Release\linux-x64\WeaveFXP      (chmod +x it after copying)
echo   Linux ARM64: Release\linux-arm64\WeaveFXP
echo   Zips       : Release\zips\WeaveFXP-v%VERSION%-*.zip
echo.
echo   Ship only the per-platform zip.
echo   The data\ folder is created on first run next to the executable and preserved on republish.
echo.
if exist "%WORK%" rd /s /q "%WORK%"
endlocal
exit /b 0

:fail
echo.
echo === Publish failed ===
for /d %%D in ("%WORK%\data-*") do (
    if exist "%%~fD" (
        set "DATA_DIR=%%~nxD"
        set "DATA_RID=!DATA_DIR:data-=!"
        if not exist "%OUT%\!DATA_RID!" mkdir "%OUT%\!DATA_RID!"
        if not exist "%OUT%\!DATA_RID!\data" move "%%~fD" "%OUT%\!DATA_RID!\data" >nul
    )
)
if exist "%WORK%" rd /s /q "%WORK%"
endlocal
exit /b 1
