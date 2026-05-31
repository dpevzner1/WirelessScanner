@echo off
echo Building and publishing WirelessScanner native executable...
dotnet publish WirelessScanner.Presentation/WirelessScanner.Presentation.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:PublishTrimmed=false -o ./publish
if %ERRORLEVEL% neq 0 (
    echo Publish FAILED!
    exit /b %ERRORLEVEL%
)
echo Publish succeeded! Executable is located in the ./publish directory.
