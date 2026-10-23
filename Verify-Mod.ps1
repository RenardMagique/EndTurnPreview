param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$GameData
)
$ErrorActionPreference = 'Stop'
$data = $GameData
if ([string]::IsNullOrWhiteSpace($data)) {
    $data = @('data_sts2_windows_x86_64', 'data_sts2_linuxbsd_x86_64') |
        ForEach-Object { Join-Path $GameDirectory $_ } |
        Where-Object { Test-Path (Join-Path $_ 'sts2.dll') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($data)) { throw 'Could not find a supported game data directory' }
$data = [System.IO.Path]::GetFullPath($data)
$sourceWorker = Join-Path $PSScriptRoot 'EndTurnSimulator/bin/Release/net9.0/EndTurnSimulator.dll'
$packagedWorker = Join-Path $PSScriptRoot 'simulator/EndTurnSimulator.dll'
if (Test-Path (Join-Path $PSScriptRoot 'EndTurnSimulator/EndTurnSimulator.csproj')) {
    foreach ($project in @('EndTurnDamagePreview','EndTurnSimulator')) {
        dotnet build (Join-Path $PSScriptRoot "$project/$project.csproj") -c Release "-p:GameData=$data" -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    $worker = $sourceWorker
} else {
    $worker = $packagedWorker
}
if (!(Test-Path $worker)) { throw 'Could not find EndTurnSimulator.dll' }
$diagnostic = & dotnet $worker $data --diagnostic --baselib
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic failed' }
Write-Output "DIAGNOSTIC $diagnostic"
foreach ($scenario in @('turns','burn','block-expiry','barricade','regen','poison','choice','lethal-burn','poison-victory','osty','osty-lethal')) {
    dotnet $worker $data --selftest "--case=$scenario"
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $scenario" }
}
$fixture = & dotnet $worker $data --fixture --baselib
if ($LASTEXITCODE -ne 0) { throw 'Fixture failed' }
$result = ($fixture | dotnet $worker $data) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$result.Verified -or $result.Hp -ne 65 -or $result.Block -ne 0) { throw 'Replay mismatch' }
Write-Output 'PASS independent replay: 65 HP, 0 block'
$psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$null = $psi.ArgumentList.Add($worker)
$null = $psi.ArgumentList.Add($data)
$null = $psi.ArgumentList.Add('--server')
$null = $psi.ArgumentList.Add('--baselib')
$server = [System.Diagnostics.Process]::Start($psi)
if ($server.StandardOutput.ReadLine() -ne 'READY') { throw 'Warm simulator did not start' }
foreach ($iteration in 1..5) {
    $server.StandardInput.WriteLine($fixture)
    $warm = $server.StandardOutput.ReadLine() | ConvertFrom-Json
    if (!$warm.Result.Verified -or $warm.Result.Hp -ne 65 -or $warm.ElapsedMs -gt 100) {
        throw "Warm simulation failed or exceeded 100 ms: $($warm | ConvertTo-Json -Compress)"
    }
}
$server.StandardInput.Close()
$server.WaitForExit()
if ($server.ExitCode -ne 0) { throw $server.StandardError.ReadToEnd() }
Write-Output 'PASS persistent warm engine: 5 verified runs under 100 ms'
$request = $fixture | ConvertFrom-Json
$request.Baseline = 'intentionally-invalid'
$result = (($request | ConvertTo-Json -Compress) | dotnet $worker $data) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $result.Verified) { throw 'Divergent state was accepted' }
Write-Output 'PASS divergent baseline rejected'
$request.GameModule = 'unsupported-build'
$result = (($request | ConvertTo-Json -Compress) | dotnet $worker $data) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $result.Verified) { throw 'Unsupported version was accepted' }
Write-Output 'PASS unsupported engine version rejected'
