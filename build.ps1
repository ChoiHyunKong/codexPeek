$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\tests\CodexPeek.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet publish "$PSScriptRoot\src\CodexPeek.csproj" -c Release --self-contained false -o "$PSScriptRoot\app" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output "Ready: $PSScriptRoot\app\CodexPeek.exe"
