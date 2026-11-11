# Compiles Shaders\*.hlsl to Shaders\*.bin with the Windows SDK's fxc. The .bin files are committed and embedded,
# so a build does not need the SDK; run this after changing a shader.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$ver = Get-ChildItem (Join-Path $kits 'bin') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'x64\fxc.exe') } | Sort-Object Name | Select-Object -Last 1
if (-not $ver) { throw 'fxc.exe not found under Windows Kits\10\bin' }
$fxc = Join-Path $ver.FullName 'x64\fxc.exe'
$inc = Join-Path $kits ('Include\' + $ver.Name + '\um')
Get-ChildItem (Join-Path $root 'Shaders') -Filter *.hlsl | ForEach-Object {
    $out = [IO.Path]::ChangeExtension($_.FullName, '.bin')
    & $fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I $inc /Fo $out $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "fxc failed on $($_.Name)" }
    Write-Output ("{0} -> {1} bytes" -f $_.Name, (Get-Item $out).Length)
}
