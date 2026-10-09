$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
python Domru.Tests/generate_crypto_vectors.py
if ($LASTEXITCODE -ne 0) { throw 'Test fixture generation failed: install Domru.Tests/requirements.txt' }
dotnet run --project Domru.Tests/Domru.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
[xml]$project = Get-Content -LiteralPath Domru.Desktop/Domru.Desktop.csproj
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
$output = "release/DomruDesktop-$version"
dotnet publish Domru.Desktop/Domru.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$PSScriptRoot=/src" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath README.md,THIRD_PARTY.md,CHANGELOG.md -Destination $output -Force
New-Item -ItemType Directory -Force -Path "$output/licenses" | Out-Null
Get-ChildItem -LiteralPath licenses -File | Copy-Item -Destination "$output/licenses" -Force
Compress-Archive -LiteralPath $output -DestinationPath "release/DomruDesktop-win-x64-$version.zip" -Force
Write-Host "Ready: $output/DomruDesktop.exe"
