# Runs the tests of this folder with the compiler and compares the result with the saved expected one.
#
#   .\run-tests.ps1                  run all the tests, show the ones that differ
#   .\run-tests.ps1 -Build           build the compiler first
#   .\run-tests.ps1 -Only a,b        run only these tests (names without .df)
#   .\run-tests.ps1 -Update          save what the tests give now as the expected result (check the diff in git before you trust it)
#   .\run-tests.ps1 -Verbose         also print the first differing lines
#
# Two kinds of test:
#  * tests\*.df are programs. The expected output is expected\<name>.txt: everything the program printed after the line "---- run ----"
#    of the compiler (so it ends with the exit code line). They are compiled and run with no input (an empty standard input).
#  * tests\errors\*.df are programs that must NOT compile. The expected result is errors\expected\<name>.txt: the error lines of the
#    compiler ("name.df(line): error: message", with no folder). A program that compiles is a failure.
# A test is "new" when it has no expected file yet: it is run and reported, and saved only with -Update.
# Exit code of this script: 0 when everything matches, 1 otherwise, 2 when the compiler is missing.

param(
  [switch]$Update,
  [switch]$Build,
  [switch]$Verbose,
  [string[]]$Only = @(),
  [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$testsFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Split-Path -Parent $testsFolder
$compiler = Join-Path $project 'bin\Debug\net10.0\dfcompiler.exe'

if ($Build) {
  & dotnet build $project -nologo -v q
  if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
}
if (-not (Test-Path $compiler)) {
  Write-Host "compiler not found: $compiler (run with -Build)" -ForegroundColor Red
  exit 2
}

# runs the compiler on a file; returns the standard output, the standard error, or $null on a timeout
function Start-Compiler([string]$file, [string]$option) {
  $info = New-Object System.Diagnostics.ProcessStartInfo
  $info.FileName = $compiler
  $info.Arguments = '"' + $file + '" ' + $option
  $info.UseShellExecute = $false
  $info.RedirectStandardInput = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
  $info.StandardErrorEncoding = [System.Text.Encoding]::UTF8
  $info.CreateNoWindow = $true
  $process = [System.Diagnostics.Process]::Start($info)
  $process.StandardInput.Close()
  $outTask = $process.StandardOutput.ReadToEndAsync()
  $errTask = $process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    try { $process.Kill() } catch { }
    return $null
  }
  return @{ Out = $outTask.Result; Err = $errTask.Result }
}

# a program: what it printed
function Invoke-Program([string]$file) {
  $run = Start-Compiler $file '--run'
  if ($null -eq $run) { return @{ Ok = $false; Text = "TIMEOUT after $TimeoutSeconds seconds" } }
  $text = $run.Out
  $marker = $text.IndexOf('---- run ----')
  if ($marker -lt 0) {
    $all = $text + "`n" + $run.Err
    $errors = ($all -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 5) -join "`n"
    return @{ Ok = $false; Text = "COMPILE ERROR`n$errors" }
  }
  # the lines of the standard output after the marker (the warnings of the compilers are not part of it)
  $printed = $text.Substring($marker + '---- run ----'.Length)
  return @{ Ok = $true; Text = ($printed -replace "`r`n", "`n").Trim("`n") }
}

# a program that must not compile: the error lines, without the folder of the file
function Invoke-ErrorProgram([string]$file) {
  $run = Start-Compiler $file '--emit-c'
  if ($null -eq $run) { return @{ Ok = $false; Text = "TIMEOUT after $TimeoutSeconds seconds" } }
  $all = $run.Out + "`n" + $run.Err
  $lines = @()
  foreach ($line in ($all -split "`r?`n")) {
    if ($line -match '([^\\/]+\.df\(\d+(?:,\d+)?\): error: .*)$') { $lines += $Matches[1] }
  }
  if ($lines.Count -eq 0) { return @{ Ok = $false; Text = 'NO ERROR (the program compiled)' } }
  return @{ Ok = $true; Text = ($lines -join "`n") }
}

$passed = 0; $failed = @(); $new = @(); $saved = 0

function Test-One([string]$name, [hashtable]$result, [string]$expectedFile) {
  if ($Update) {
    if (-not $result.Ok) {
      Write-Host ("SKIP  {0}: {1}" -f $name, ($result.Text -split "`n")[0]) -ForegroundColor Yellow
      $script:failed += $name
      return
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $expectedFile) | Out-Null
    [System.IO.File]::WriteAllText($expectedFile, $result.Text + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $script:saved++
    Write-Host ("SAVED {0}" -f $name) -ForegroundColor DarkGray
    return
  }
  if (-not (Test-Path $expectedFile)) {
    $script:new += $name
    Write-Host ("NEW   {0}  (no expected file: run with -Update after checking the result)" -f $name) -ForegroundColor Yellow
    return
  }
  $expected = ([System.IO.File]::ReadAllText($expectedFile, [System.Text.Encoding]::UTF8) -replace "`r`n", "`n").Trim("`n")
  if ($result.Ok -and $result.Text -ceq $expected) {
    $script:passed++
    Write-Host ("pass  {0}" -f $name) -ForegroundColor DarkGreen
    return
  }
  $script:failed += $name
  Write-Host ("FAIL  {0}" -f $name) -ForegroundColor Red
  if ($Verbose -or -not $result.Ok) {
    $got = $result.Text -split "`n"
    $want = $expected -split "`n"
    $shown = 0
    for ($i = 0; $i -lt [Math]::Max($got.Count, $want.Count) -and $shown -lt 4; $i++) {
      $g = if ($i -lt $got.Count) { $got[$i] } else { '<missing>' }
      $w = if ($i -lt $want.Count) { $want[$i] } else { '<missing>' }
      if ($g -cne $w) {
        Write-Host ("      line {0}: expected [{1}]" -f ($i + 1), $w)
        Write-Host ("      line {0}: got      [{1}]" -f ($i + 1), $g)
        $shown++
      }
    }
  }
}

$programs = Get-ChildItem $testsFolder -Filter '*.df' | Sort-Object Name
$errorPrograms = @()
if (Test-Path (Join-Path $testsFolder 'errors')) {
  $errorPrograms = Get-ChildItem (Join-Path $testsFolder 'errors') -Filter '*.df' | Sort-Object Name
}
if ($Only.Count -gt 0) {
  $programs = $programs | Where-Object { $Only -contains $_.BaseName }
  $errorPrograms = $errorPrograms | Where-Object { $Only -contains $_.BaseName }
}

foreach ($test in $programs) {
  Test-One $test.BaseName (Invoke-Program $test.FullName) (Join-Path $testsFolder ('expected\' + $test.BaseName + '.txt'))
}
foreach ($test in $errorPrograms) {
  Test-One ('errors/' + $test.BaseName) (Invoke-ErrorProgram $test.FullName) (Join-Path $testsFolder ('errors\expected\' + $test.BaseName + '.txt'))
}

Write-Host ''
if ($Update) {
  Write-Host ("saved {0} expected results, skipped {1}" -f $saved, $failed.Count)
  exit 0
}
Write-Host ("{0} passed, {1} failed, {2} new" -f $passed, $failed.Count, $new.Count)
if ($failed.Count -gt 0) { Write-Host ('failed: ' + ($failed -join ', ')) -ForegroundColor Red }
if ($failed.Count -gt 0 -or $new.Count -gt 0) { exit 1 }
exit 0
