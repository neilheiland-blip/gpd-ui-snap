$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet publish (Join-Path $Root 'GpdUiSnap.csproj') -c Release -r win-x64 --self-contained false
