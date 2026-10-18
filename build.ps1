$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = 'D:\CodexCache\LinguaFlow\dotnet-home'
$env:NUGET_PACKAGES = 'D:\CodexCache\LinguaFlow\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

dotnet restore (Join-Path $PSScriptRoot 'LinguaFlow.slnx') --nologo -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build (Join-Path $PSScriptRoot 'LinguaFlow.slnx') -c Release --no-restore --nologo -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test (Join-Path $PSScriptRoot 'LinguaFlow.slnx') -c Release --no-restore --no-build --nologo -m:1
exit $LASTEXITCODE
