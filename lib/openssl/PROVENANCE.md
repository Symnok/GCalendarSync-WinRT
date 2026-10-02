# Where the vendored OpenSSL came from

The DLLs, import libs and headers in `wp81-arm-v120/` and `wp81-x86-v120/` are **build output**.
This file records how to regenerate them; nothing here is needed to build OKLumessenger itself.

## Source

    repo:    https://github.com/infodimus/OpenSSLWinRT.git
    branch:  main
    commit:  5092129e8e479c3ccacdb2f621b0d82f60d6cd17
    version: OpenSSL 1.0.2r
    origin:  a fork of github.com/microsoft/openssl, branch OpenSSL_1_0_2_WinRT-stable,
             which is what adds the VC-WINPHONE / VC-WINSTORE / VC-WINUNIVERSAL targets

## Rebuilding

Clone with **LF** endings - CRLF breaks OpenSSL's Perl build scripts
(`mk1mf.pl` dies with `no rule for ssl\`):

    git -c core.autocrlf=false clone -b main https://github.com/infodimus/OpenSSLWinRT.git

Then, from the clone (copies of both scripts are in `build-scripts/` here):

    ms\build-wp81-arm.bat      ->  out32dll\  (ARM, devices)
    ms\build-wp81-x86.bat      ->  out32dll\  (x86, emulator)

Copy `inc32\openssl\*.h`, `out32dll\*.lib` and `out32dll\*.dll` into the matching
`wp81-<arch>-v120\{include\openssl,lib,bin}` here. PDBs are deliberately not vendored.

## Why v120 and not v140

Forced by the platform, not chosen:

* `Microsoft.Cpp\v4.0\V140` has **no `Application Type\Windows Phone`** folder, so v140 cannot
  target WP8.1 natively at all.
* The only VCLibs Extension SDK for WindowsPhoneApp 8.1 is **12.0** - there is no VCLibs 14.0
  for WP8.1. A v140 build resolves the *UAP* VC runtime and fails with MSB3843/MSB3844.

So the native component is `v120_wp81` and the OpenSSL it links must match. The managed
projects are unaffected and still build on VS2015.

## Two traps in the OpenSSL build itself

1. `HKLM\...\Microsoft SDKs\Windows Phone\v8.1` points at the **Silverlight** kit. The WinRT
   build needs `Windows Phone Kits\8.1`; the scripts set `_WPKITS81` explicitly rather than
   trusting `ms\setVSVars.bat`.
2. Build with VS2013's `vcvarsall x86_arm` / `x86`, with `LIB` pointed at the WP8.1 kit and
   `VC\lib\store\ARM` (or `\store` for x86).
