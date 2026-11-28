# Builds the Json library from this folder and puts what programs use (json.dfi and <target>\json.lib) in the folder above.
# A library build writes them next to its source (spec 15.8); the compiler has no output folder for libraries yet.
param([string]$Compiler = (Join-Path $PSScriptRoot '..\..\..\Compiler\dfcompiler\bin\Debug\net10.0\dfcompiler.exe'))
$code = $PSScriptRoot
$lib = Split-Path $code -Parent
& $Compiler $code
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Move-Item (Join-Path $code 'json.dfi') $lib -Force
Get-ChildItem $code -Directory | Where-Object { $_.Name -match '^(win|linux|osx)-' } | ForEach-Object {
  $dest = Join-Path $lib $_.Name
  New-Item -ItemType Directory -Force $dest | Out-Null
  Move-Item (Join-Path $_.FullName '*') $dest -Force
  Remove-Item $_.FullName -Recurse -Force
}
Write-Host "Library files in $lib"
