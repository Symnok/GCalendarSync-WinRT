# Vendored OpenSSL 1.0.2r (WP8.1, v120)

Prebuilt so the app can be built from a clone without building OpenSSL first.

    wp81-arm-v120/   ARM  - ships to devices
    wp81-x86-v120/   x86  - WP8.1 emulator

Each holds `include/openssl` (75 headers), `lib` (import libs) and `bin` (the DLLs that go
into the package). PDBs are deliberately excluded - ~10 MB each and irrelevant to building.

**v120, not v140, and that is forced:** `Microsoft.Cpp\v4.0\V140` has no
`Application Type\Windows Phone`, so v140 cannot target WP8.1 natively, and the only VCLibs
Extension SDK for WindowsPhoneApp 8.1 is 12.0. The native component is therefore v120_wp81 and
the OpenSSL it links must match.

Rebuilt from `D:\GitHub\OpenSSLWinRT` via `ms\build-wp81-arm.bat` / `ms\build-wp81-x86.bat`.
Nothing here needs regenerating unless OpenSSL itself is changed.
