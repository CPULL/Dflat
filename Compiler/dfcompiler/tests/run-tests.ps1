# Runs the tests of this folder with the compiler and compares the result with the saved expected one.
#
#   .\run-tests.ps1                  run all the tests, show the ones that differ
#   .\run-tests.ps1 -Build           build the compiler first
#   .\run-tests.ps1 -Only a,b        run only these tests (names without .df)
#   .\run-tests.ps1 -Update          save what the tests give now as the expected result (check the diff in git before you trust it)
#   .\run-tests.ps1 -Verbose         accepted for old scripts: the first differing lines of a failed test are always printed
#   .\run-tests.ps1 -Jobs 2          how many compilers run at the same time (default 4); the tests that use files or processes
#                                    (Root.IO, ExecProcess) always run one at a time, after the others
#
# Two kinds of test:
#  * tests\*.df are programs. The expected output is expected\<name>.txt: everything the program printed after the line "---- run ----"
#    of the compiler (so it ends with the exit code line). They are compiled and run with no input (an empty standard input).
#  * tests\errors\*.df are programs that must NOT compile. The expected result is errors\expected\<name>.txt: the error lines of the
#    compiler ("name.df(line): error: message", with no folder). A program that compiles is a failure.
#  * tests\projects\<name>\ are programs of several files (spec 16), each folder with its .dsprj: the expected output is
#    projects\expected\<name>.txt, as for the programs.
#  * tests\projects\errors\<name>\ are projects that must NOT compile: projects\errors\expected\<name>.txt holds the error lines,
#    with the paths relative to the folder of the project and / as the separator.
# A test is "new" when it has no expected file yet: it is run and reported, and saved only with -Update.
# Exit code of this script: 0 when everything matches, 1 otherwise, 2 when the compiler is missing.

param(
  [switch]$Update,
  [switch]$Build,
  [switch]$Verbose,
  [string[]]$Only = @(),
  [int]$TimeoutSeconds = 180,
  [int]$Jobs = 4
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

# starts the compiler on a file; its output is read while it runs
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
  return @{
    Process = $process
    OutTask = $process.StandardOutput.ReadToEndAsync()
    ErrTask = $process.StandardError.ReadToEndAsync()
    Started = [DateTime]::Now
  }
}

# the standard output and error of a finished compiler, or $null after a timeout (the process is killed)
function Get-CompilerResult($running) {
  if ($running.TimedOut) { return $null }
  return @{ Out = $running.OutTask.Result; Err = $running.ErrTask.Result }
}

# runs the compilers of the tests, at most $limit at the same time; each test gets its finished run in .Run
function Invoke-Pool($tests, [int]$limit) {
  $queue = New-Object System.Collections.Queue
  foreach ($test in $tests) { $queue.Enqueue($test) }
  $active = New-Object System.Collections.ArrayList
  while ($queue.Count -gt 0 -or $active.Count -gt 0) {
    while ($queue.Count -gt 0 -and $active.Count -lt $limit) {
      $test = $queue.Dequeue()
      $test.Running = Start-Compiler $test.Path $test.Option
      [void]$active.Add($test)
    }
    $finished = @()
    foreach ($test in $active) {
      $running = $test.Running
      if ($running.Process.WaitForExit(50)) {
        $finished += $test
      } elseif (([DateTime]::Now - $running.Started).TotalSeconds -gt $TimeoutSeconds) {
        try { $running.Process.Kill() } catch { }
        $running.TimedOut = $true
        $finished += $test
      }
    }
    foreach ($test in $finished) {
      if (-not $test.Running.TimedOut) { $test.Running.Process.WaitForExit() }   # the output has been read to the end
      $test.Run = Get-CompilerResult $test.Running
      [void]$active.Remove($test)
    }
  }
}

# a program: what it printed
function Read-Program($run) {
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
function Read-ErrorProgram($run) {
  if ($null -eq $run) { return @{ Ok = $false; Text = "TIMEOUT after $TimeoutSeconds seconds" } }
  $all = $run.Out + "`n" + $run.Err
  $lines = @()
  foreach ($line in ($all -split "`r?`n")) {
    if ($line -match '([^\\/]+\.df\(\d+(?:,\d+)?\): error: .*)$') { $lines += $Matches[1] }
  }
  if ($lines.Count -eq 0) { return @{ Ok = $false; Text = 'NO ERROR (the program compiled)' } }
  return @{ Ok = $true; Text = ($lines -join "`n") }
}

# a project that must not compile: the error lines, with the paths relative to its folder
function Read-ErrorProject($run, [string]$folder) {
  if ($null -eq $run) { return @{ Ok = $false; Text = "TIMEOUT after $TimeoutSeconds seconds" } }
  $all = $run.Out + "`n" + $run.Err
  $lines = @()
  foreach ($line in ($all -split "`r?`n")) {
    if ($line -match 'error: ') { $lines += ($line.Replace($folder + '\', '').Replace($folder, '').Replace('\', '/')).Trim() }
  }
  if ($lines.Count -eq 0) { return @{ Ok = $false; Text = 'NO ERROR (the project compiled)' } }
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
  # always: the log of the automatic build must say which line differs
  if ($true) {
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
$projectsFolder = Join-Path $testsFolder 'projects'
$projects = @()
$errorProjects = @()
if (Test-Path $projectsFolder) {
  $projects = Get-ChildItem $projectsFolder -Directory | Where-Object { $_.Name -notin @('errors', 'expected') } | Sort-Object Name
  if (Test-Path (Join-Path $projectsFolder 'errors')) {
    $errorProjects = Get-ChildItem (Join-Path $projectsFolder 'errors') -Directory | Where-Object { $_.Name -ne 'expected' } | Sort-Object Name
  }
}
if ($Only.Count -gt 0) {
  $programs = $programs | Where-Object { $Only -contains $_.BaseName }
  $errorPrograms = $errorPrograms | Where-Object { $Only -contains $_.BaseName }
  $projects = $projects | Where-Object { $Only -contains $_.Name }
  $errorProjects = $errorProjects | Where-Object { $Only -contains $_.Name }
}

# every test: its name, what the compiler runs on, how its output is read and where its expected result is
$all = New-Object System.Collections.ArrayList
foreach ($test in $programs) {
  [void]$all.Add(@{ Name = $test.BaseName; Path = $test.FullName; Option = '--run'; Kind = 'program'
    Expected = (Join-Path $testsFolder ('expected\' + $test.BaseName + '.txt')) })
}
foreach ($test in $errorPrograms) {
  [void]$all.Add(@{ Name = 'errors/' + $test.BaseName; Path = $test.FullName; Option = '--emit-c'; Kind = 'error'
    Expected = (Join-Path $testsFolder ('errors\expected\' + $test.BaseName + '.txt')) })
}
foreach ($test in $projects) {
  [void]$all.Add(@{ Name = 'projects/' + $test.Name; Path = $test.FullName; Option = '--run'; Kind = 'program'
    Expected = (Join-Path $projectsFolder ('expected\' + $test.Name + '.txt')) })
}
foreach ($test in $errorProjects) {
  [void]$all.Add(@{ Name = 'projects/errors/' + $test.Name; Path = $test.FullName; Option = '--emit-c'; Kind = 'errorProject'
    Expected = (Join-Path $projectsFolder ('errors\expected\' + $test.Name + '.txt')) })
}

# a program that uses files or processes may clash with another one that runs at the same time: those run one at a time
function Test-Serial($test) {
  if ($test.Kind -ne 'program') { return $false }
  $sources = if (Test-Path $test.Path -PathType Container) { Get-ChildItem $test.Path -Filter '*.df' -Recurse } else { Get-Item $test.Path }
  foreach ($source in $sources) {
    if ((Get-Content -Raw $source.FullName) -match 'Root\.IO|ExecProcess') { return $true }
  }
  return $false
}
$parallel = @($all | Where-Object { -not (Test-Serial $_) })
$serial = @($all | Where-Object { Test-Serial $_ })
Invoke-Pool $parallel ([Math]::Max(1, $Jobs))
Invoke-Pool $serial 1

foreach ($test in $all) {
  $result = switch ($test.Kind) {
    'program' { Read-Program $test.Run }
    'error' { Read-ErrorProgram $test.Run }
    'errorProject' { Read-ErrorProject $test.Run $test.Path }
  }
  Test-One $test.Name $result $test.Expected
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
