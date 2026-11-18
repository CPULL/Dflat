# Builds the static libraries of dfcallbacks.c for the tests (run from this folder): libs/win-x64/dfcallbacks.lib and libs/linux-x64/libdfcallbacks.a
$bin = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin'
$tmp = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'dfcallbacks-build')
& "$bin\clang.exe" --target=x86_64-pc-windows-msvc -O2 -c dfcallbacks.c -o "$tmp\win.o"
& "$bin\llvm-lib.exe" "/out:..\libs\win-x64\dfcallbacks.lib" "$tmp\win.o"
& "$bin\clang.exe" --target=x86_64-unknown-linux-gnu -O2 -fPIC -c dfcallbacks.c -o "$tmp\linux.o"
& "$bin\llvm-ar.exe" rcs "..\libs\linux-x64\libdfcallbacks.a" "$tmp\linux.o"
