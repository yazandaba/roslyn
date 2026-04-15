@echo off
setlocal

set REFPATH=C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.5\ref\net10.0
set CSC_DLL=%~dp0..\artifacts\bin\csc\Debug\net10.0\csc.dll
set SRC=%~dp0consteval_test.cs
set OUT=%~dp0consteval_test.dll

echo === Compiling with Roslyn csc ===
dotnet exec "%CSC_DLL%" -r:"%REFPATH%\System.Runtime.dll" -r:"%REFPATH%\System.Console.dll" -nologo -target:exe -out:"%OUT%" "%SRC%"
if %ERRORLEVEL% neq 0 (
    echo *** Compilation FAILED ***
    exit /b %ERRORLEVEL%
)

echo.
echo === Running ===
echo.
dotnet "%OUT%"
