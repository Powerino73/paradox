@echo off
setlocal
rmdir /S /Q "%LOCALAPPDATA%\temp\SiliconStudio"
REM -------------------------------------------------------------------
REM Global config
REM -------------------------------------------------------------------
set PARADOX_SOLUTION=Paradox.iOS.sln
set PARADOX_PLATFORM=Configuration=Release;Platform=iPhone
set PARADOX_BUILD_LOG=%~n0.log
set PARADOX_OUTPUT_DIR=%~dp0\Bin\iOS-AnyCPU-OpenGLES
REM -------------------------------------------------------------------
REM Build Paradox
REM -------------------------------------------------------------------
SET ProgFiles86Root=%ProgramFiles(x86)%
IF NOT "%ProgFiles86Root%"=="" GOTO win64
SET ProgFiles86Root=%ProgramFiles%
:win64
set VS_VCVARSALL=%ProgFiles86Root%\Microsoft Visual Studio 11.0\vc\vcvarsall.bat
IF EXIST "%VS_VCVARSALL%" GOTO :vcvarok
echo Error, unable to find path Visual Studio 2012 Path: [%VS_VCVARSALL%]
exit /b
:vcvarok
call "%VS_VCVARSALL%" x86
echo Building Paradox, Please wait...
REM Explicitly remove the Bin directory to make a full cleanup
IF EXIST "%PARADOX_OUTPUT_DIR%" rmdir /S /Q %PARADOX_OUTPUT_DIR%
REM msbuild Clean
IF EXIST "%PARADOX_BUILD_LOG%" del /F "%PARADOX_BUILD_LOG%"
msbuild /nologo /tv:4.0 /t:Clean /verbosity:minimal /fl "/flp:Summary;Verbosity=minimal;logfile=%PARADOX_BUILD_LOG%" "/p:%PARADOX_PLATFORM%" %PARADOX_SOLUTION%
if %ERRORLEVEL% neq 0 GOTO :error_pause
REM msbuild Build
IF EXIST "%PARADOX_BUILD_LOG%" del /F "%PARADOX_BUILD_LOG%"
msbuild /nologo /tv:4.0 /t:Build /verbosity:minimal /fl "/flp:Summary;Verbosity=minimal;logfile=%PARADOX_BUILD_LOG%" "/p:%PARADOX_PLATFORM%" %PARADOX_SOLUTION%
if %ERRORLEVEL% neq 0 GOTO :error_pause
GOTO :end
:error_pause
echo Check full error log in %PARADOX_BUILD_LOG%
pause
:end

