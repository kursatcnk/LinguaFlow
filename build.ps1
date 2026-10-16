$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = 'D:\CodexCache\LinguaFlow\dotnet-home'
$env:NUGET_PACKAGES = 'D:\CodexCache\LinguaFlow\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

dotnet build (Join-Path $PSScriptRoot 'LinguaFlow.slnx') -c Release --nologo
dotnet test (Join-Path $PSScriptRoot 'LinguaFlow.slnx') -c Release --no-restore --nologo
