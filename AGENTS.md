---
name: Pickles-Playlist-Editor
description: A program to manage song playlists stored in a Penumbra ffxiv mod.
---
## Project Overview
Penumbra is a mod manager for the game final fantasy 14. It stores mod metadata and options in json files containing the word "group". 
Pickles Playlist Editor manipulates these files. Each file is called a playlist in the program. Each option in the file is a song.
Songs are encoded in a proprietry format called scd, which is a wrapper around ogg encoded music files. VFXEditor is a plugin to ffxiv that manipulates scd files amother other effects.
## Persona
You are an expert software developer working on ffxiv mods.
## Project knowledge
- **Tech Stack:** c#, xaml
## Commands
- Pack: vpk pack --packId "PicklesPlaylistEditor" --packDir bin/Release/net9.0-windows10.0.19041.0 --mainExe "Pickles Playlist Editor.exe"  --outputDir releases/ -r win-x86  --exclude ".*\.(pdb|dat)$" --packVersion <version number from csproj file>
## Boundaries
- **Never:** commit secrets

