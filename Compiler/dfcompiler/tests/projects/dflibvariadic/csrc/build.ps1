# Builds the static libraries of dfvariadic.c for the tests (run from this folder): libs/win-x64/dfvariadic.lib and libs/linux-x64/libdfvariadic.a
$bin = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin'
$tmp = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'dfvariadic-build')
& "$bin\clang.exe" --target=x86_64-pc-windows-msvc -O2 -c dfvariadic.c -o "$tmp\win.o"
& "$bin\llvm-lib.exe" "/out:..\libs\win-x64\dfvariadic.lib" "$tmp\win.o"
& "$bin\clang.exe" --target=x86_64-unknown-linux-gnu -O2 -fPIC -c dfvariadic.c -o "$tmp\linux.o"
& "$bin\llvm-ar.exe" rcs "..\libs\linux-x64\libdfvariadic.a" "$tmp\linux.o"
