@echo off
setlocal
cd /d "%~dp0"

echo [1/2] Compiling CharacterType.exe...
set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

%CSC% /target:winexe /optimize+ /platform:anycpu /out:CharacterType.exe Program.cs

if %ERRORLEVEL% EQU 0 (
    echo [2/2] Build Successful! CharacterType.exe created.
) else (
    echo [!] Build Failed with Error Code %ERRORLEVEL%
)
pause
