# Builds the static libraries of dfcalc.c for the tests (run from this folder): libs/win-x64/dfcalc.lib and libs/linux-x64/libdfcalc.a
$bin = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin'
$tmp = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'dfcalc-build')
& "$bin\clang.exe" --target=x86_64-pc-windows-msvc -O2 -c dfcalc.c -o "$tmp\win.o"
& "$bin\llvm-lib.exe" "/out:..\libs\win-x64\dfcalc.lib" "$tmp\win.o"
& "$bin\clang.exe" --target=x86_64-unknown-linux-gnu -O2 -fPIC -c dfcalc.c -o "$tmp\linux.o"
& "$bin\llvm-ar.exe" rcs "..\libs\linux-x64\libdfcalc.a" "$tmp\linux.o"
# the dynamic forms, for the project dflibdynamic: libs/win-x64/dfcalcd.lib + dfcalcd.dll, libs/linux-x64/libdfcalcd.so
$dyn = '..\..\dflibdynamic\libs'
New-Item -ItemType Directory -Force "$dyn\win-x64", "$dyn\linux-x64" | Out-Null
& "$bin\clang.exe" --target=x86_64-pc-windows-msvc -O2 -shared -o "$dyn\win-x64\dfcalcd.dll" dfcalc.c
Remove-Item "$dyn\win-x64\dfcalcd.exp" -ErrorAction SilentlyContinue
& "$bin\clang.exe" --target=x86_64-unknown-linux-gnu -O2 -fPIC -shared -nostdlib -fuse-ld=lld -o "$dyn\linux-x64\libdfcalcd.so" dfcalc.c
