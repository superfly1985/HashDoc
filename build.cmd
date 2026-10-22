@echo off
rem Dev build: outputs a portable folder to .\out (includes data\HashDoc.db if it exists)
cd /d "%~dp0"
echo [HashDoc] Building Release ...
dotnet build src\HashDoc.csproj -c Release -o out
if errorlevel 1 (
  echo.
  echo [HashDoc] Build FAILED - see errors above.
  exit /b 1
)
echo.
echo [HashDoc] Build OK. Portable folder: %~dp0out
echo   Self test : out\HashDoc.exe --selftest   (result: out\selftest_result.txt)
echo   DB info   : out\HashDoc.exe --dbinfo
echo   Release   : release.cmd  (makes dist\HashDoc_vX.Y.Z.zip, program files only)
