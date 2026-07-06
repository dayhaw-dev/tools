# Building setres

Requires .NET 8 SDK.

dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

Output exe lands in bin/Release/net8.0/win-x64/publish/
Note: csproj is set to WinExe (windowless). Output only visible if run
from an existing terminal / with AttachConsole.
