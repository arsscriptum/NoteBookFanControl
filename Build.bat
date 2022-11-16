@echo off
setlocal EnableDelayedExpansion

:: ==============================================================================
:: 
::      Build.bat
::
::      Build different configuration of the app
::
:: ==============================================================================
::   arsccriptum - made in quebec 2020 <guillaumeplante.qc@gmail.com>
:: ==============================================================================

goto :init

:init
    set "__scripts_root=%AutomationScriptsRoot%"
    call :read_script_root development\build-automation  BuildAutomation
    set "__script_file=%~0"
    set "__target=%~1"
    set "__configuration=%~2"
    set "__script_path=%~dp0"
    set "__makefile=%__scripts_root%\make\run_compiler.bat"
    set "__lib_date=%__scripts_root%\batlibs\date.bat"
    set "__lib_out=%__scripts_root%\batlibs\out.bat"
    set "__build_cancelled=0"
    set "__nuget=C:\ProgramData\chocolatey\bin\nuget.exe"
    goto :setdefaults


:header
    echo. %__script_name% v%__script_version%
    echo.    This script is part of arsscriptum build wrappers.
    echo.
    goto :eof

:header_err
    echo.**************************************************
    echo.This script is part of arsscriptum build wrappers.
    echo.**************************************************
    echo.
    echo. YOU NEED TO HAVE THE BuildAutomation Scripts setup on you system...
    echo. https://github.com/arsscriptum/BuildAutomation
    goto :eof


:read_script_root
    set regpath=%OrganizationHKCU::=%
    for /f "tokens=2,*" %%A in ('REG.exe query %regpath%\%1 /v %2') do (
            set "__scripts_root=%%B"
        )
    goto :eof



:checklibs
     if not exist %__lib_out%  call :error_missing_lib %__lib_out% & goto :end
     if not exist %__lib_date% call :error_missing_lib %__lib_date% & goto :end


:setdefaults
    if "%__target%" == "" set "__target=Build"
    if "%__configuration%" == "" set "__configuration=ReleaseWindows"
    if not defined __target set "__target=Build"
    if not defined __configuration set "__configuration=ReleaseWindows"

:validate
    if not defined __scripts_root          call :header_err && call :error_missing_path __scripts_root & goto :eof
    if not exist %__makefile%  call :error_missing_script "%__makefile%" & goto :eof
    if not exist %__lib_date%  call :error_missing_script "%__lib_date%" & goto :eof
    if not exist %__lib_out%  call :error_missing_script "%__lib_out%" & goto :eof
    goto :prebuild_header


:prebuild_header
    call %__lib_date% :getbuilddate
    call %__lib_out% :__out_d_red " ======================================================================="
    call %__lib_out% :__out_l_red " Compilation started for %cd%  %__target%"  
    call %__lib_out% :__out_d_red " ======================================================================="
    call :installpackages
    goto :eof



:: ==============================================================================
::   clean all
:: ==============================================================================
:clean
    call %__makefile% /t:Clean
    goto :eof


:installpackages
    call %__nuget% restore
    call :build
    goto :eof

:: ==============================================================================
::   Build
:: ==============================================================================
:build
    ::set BUILD_VERBOSITY=normal
    call %__makefile% /t:%__target% /p:Configuration=%__configuration%
    goto :finished


:error_missing_path
    echo.
    echo   Error
    echo    Missing path: %~1
    echo.
    goto :eof



:error_missing_script
    echo.
    echo    Error
    echo    Missing bat script: %~1
    echo.
    goto :eof



:finished
    call %__lib_out% :__out_d_grn "Build complete"
