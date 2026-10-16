$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = 'D:\CodexCache\LinguaFlow\dotnet-home'
$env:NUGET_PACKAGES = 'D:\CodexCache\LinguaFlow\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$project = Join-Path $PSScriptRoot 'src\LinguaFlow.Desktop\LinguaFlow.Desktop.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $output --nologo
