@echo off
rem Builds qwen-terminal.exe with the .NET Framework 4.0 compiler (no other tools needed).
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe from .NET Framework 4.0 not found.
    exit /b 1
)
"%CSC%" /nologo /optimize+ /target:exe /out:qwen-terminal.exe Program.cs
if errorlevel 1 exit /b 1
echo.
echo Built qwen-terminal.exe
echo Run it with: qwen-terminal.exe
