# Third-party notices

The distributable Windows package will bundle the official
[scrcpy](https://github.com/Genymobile/scrcpy) Windows x64 archive without
rebuilding or modifying its executables.

scrcpy is licensed under Apache License 2.0. Its Windows archive also contains
Android Platform Tools, FFmpeg, SDL, libusb and other components. Their original
license and notice files are preserved in the bundled `vendor/scrcpy` directory.

The preparation script verifies `scrcpy-win64-v4.1.zip` against the SHA-256
digest published by the official GitHub release:

`5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db`
