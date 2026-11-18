# Builds the static libraries of dfout.c for the tests (run from this folder): libs/win-x64/dfout.lib and libs/linux-x64/libdfout.a
$bin = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin'
$tmp = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'dfout-build')
& "$bin\clang.exe" --target=x86_64-pc-windows-msvc -O2 -c dfout.c -o "$tmp\win.o"
& "$bin\llvm-lib.exe" "/out:..\libs\win-x64\dfout.lib" "$tmp\win.o"
& "$bin\clang.exe" --target=x86_64-unknown-linux-gnu -O2 -fPIC -c dfout.c -o "$tmp\linux.o"
& "$bin\llvm-ar.exe" rcs "..\libs\linux-x64\libdfout.a" "$tmp\linux.o"
