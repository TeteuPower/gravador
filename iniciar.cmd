@echo off
setlocal
cd /d "%~dp0"
title Gravador

rem Herdada de terminais do VS Code; com ela o launcher do Claude Code sobe como Node puro
rem e o resumo nunca sai. Mesma armadilha documentada no iniciar.cmd do Limpador.
set ELECTRON_RUN_AS_NODE=

set APP=src\Gravador.App\bin\Release\net10.0-windows\Gravador.exe
set CLI=src\Gravador.Cli\bin\Release\net10.0-windows\gravador-cli.exe

if /i "%~1"=="cli" goto :cli
if /i "%~1"=="telas" goto :telas

if not exist "%APP%" call :compilar || goto :erro
echo Abrindo o Gravador...
start "Gravador" "%APP%"
goto :fim

:cli
if not exist "%CLI%" call :compilar || goto :erro
"%CLI%" %2 %3 %4 %5 %6 %7 %8 %9
goto :fim

rem Desenha as telas em PNG, sem depender da area de trabalho. Ver docs/arquitetura.md.
:telas
if not exist "%APP%" call :compilar || goto :erro
set DESTINO=%~2
if "%DESTINO%"=="" set DESTINO=%TEMP%\gravador-telas
"%APP%" --render "%DESTINO%"
echo Telas em %DESTINO%
goto :fim

:compilar
echo Compilando (.NET 10)...
dotnet build -c Release --nologo
exit /b %errorlevel%

:erro
echo.
echo Algo falhou na compilacao. Veja a mensagem acima.
pause
exit /b 1

:fim
endlocal
exit /b 0
