# Third-party notices

Pickles Playlist Editor ships these components. Full license texts are in the `licenses` folder next to the app.

## SoundTouch.Net 2.3.2
- License: LGPL-2.1-or-later (`licenses/LGPL-2.1.txt`)
- Source: https://github.com/owoudenberg/soundtouch.net, a C# port of SoundTouch by Olli Parviainen
- Used unmodified, as the separate `SoundTouch.Net.dll`. You may replace that file with your own build of the same library.

## NAudio 2.2.1
- License: MIT (`licenses/NAudio-MIT.txt`)
- Source: https://github.com/naudio/NAudio

## NAudio.Vorbis 1.5.0
- License: MIT (`licenses/NAudio.Vorbis-MIT.txt`)
- Source: https://github.com/naudio/Vorbis

## NVorbis 0.10.4
- License: MIT (`licenses/NVorbis-MIT.txt`)
- Source: https://github.com/NVorbis/NVorbis

## FFmpeg (bundled `ffmpeg.exe`)
- Build: `2025-11-02-git-f5eb11a71d-essentials_build-www.gyan.dev`, from https://www.gyan.dev/ffmpeg/builds/
- License: GPL v3 (`licenses/FFmpeg-GPL-3.0.txt`)
- Source: https://github.com/FFmpeg/FFmpeg/commit/f5eb11a71d . `ffmpeg.exe -buildconf` prints the build configuration.
- Used unmodified, as a separate program the editor runs. You may replace it with your own build.

## FFmpeg full build (downloaded only when you turn on the AI score)
- Build: `9.0.2-full_build-www.gyan.dev`, from https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2
- License: GPL v3 (`licenses/FFmpeg-GPL-3.0.txt`)
- Source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
- Downloaded unmodified from this app's `aiscan-v1` release into `%LOCALAPPDATA%\PicklesPlaylistEditor\aiscan\aiscan-v1\`.
