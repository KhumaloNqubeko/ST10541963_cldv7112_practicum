$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$credentialDirectory = Join-Path $repoPath '.local'
$credentialPath = Join-Path $credentialDirectory 'demo-credentials.json'
New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $credentialPath)) {
    $password = 'Agri!9aA' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [ordered]@{
        Note = 'Local demo credentials. Do not commit or share publicly. Existing database passwords are never reset by the seed.'
        Password = $password
        Accounts = @('admin@agristream.example', 'farmer1@agristream.example', 'farmer2@agristream.example', 'agronomist1@agristream.example', 'agronomist2@agristream.example')
    } | ConvertTo-Json | Set-Content -LiteralPath $credentialPath
}
$credentials = Get-Content -LiteralPath $credentialPath -Raw | ConvertFrom-Json
$previousPassword = $env:DemoSeed__Password
try {
    $env:DemoSeed__Password = $credentials.Password
    & dotnet run --project (Join-Path $repoPath 'AgriStream') --no-launch-profile -- --seed-demo-data
    if ($LASTEXITCODE -ne 0) { throw 'Database seeding failed. Resolve the reported connection or database error before retrying.' }
    Write-Host "Demo login credentials: $credentialPath"
} finally {
    $env:DemoSeed__Password = $previousPassword
}
