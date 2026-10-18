$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = 'D:\CodexCache\LinguaFlow\dotnet-home'
$env:NUGET_PACKAGES = 'D:\CodexCache\LinguaFlow\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$project = Join-Path $PSScriptRoot 'src\LinguaFlow.Desktop\LinguaFlow.Desktop.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'
dotnet restore $project -r win-x64 --nologo -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $output --nologo -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Semboller geliştirici derlemesinde kalır; dağıtım klasörü kullanıcı için tek EXE içerir.
Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File | Remove-Item -Force
exit 0
