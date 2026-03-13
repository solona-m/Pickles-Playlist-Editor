Pickles Playlist  Editor Guide<img width="1299" height="1083" alt="preview" src="https://github.com/user-attachments/assets/348accc9-0067-4893-be72-68a28b20a5bf" />

Created by Solona 
Guide written by Cat with Keyboard 
(Who the heck thought that was a good idea?) 

BACKUP YOUR DJ MOD BEFORE USING THIS EDITOR!  I AM NOT RESPONSIBLE FOR ANY CORRUPTION OF FILES OR LOSS OF DATA!

Tired of hearing ads and talking over the music at your venues? Become a mare dj! You'll need a dj mod first. Here are the two publically available ones. The original dj mod by pickles is available to friends upon request:
* https://www.xivmodarchive.com/modid/128697
* https://www.glamourdresser.com/mods/thunderdome-exe

This program makes it easy to drag and drop your favorite songs into your mod of choice.

Setup
1) Download and run the exe in the releases link. https://github.com/solona-m/Pickles-Playlist-Editor/releases/latest/download/PicklesPlaylistEditor-win-Setup.exe
a.	NOTE: Windows is pissy about non-commercial software. You may need to click more info and run anyway on the smartscreen filter.

2) Install any dependencies when prompted. Then after setup completes, the program should launch. You can also launch from your start menu.

3)	(optional) if you don't want to reload the mod after every change, open penumbra. Click the settings tab at top left. Expand Advanced. Check Enable HTTP API.

Getting started
The program will detect and automatically configure itself for DJ Pickles dj mod (the original one!), thunderdome and yue's dj mod. Using another dj mod, or if you aren't seeing any songs, open settings and browse to your dj mod directory then set your replaced filename. 
DJ Pickles Mod uses sound/bpmloop.scd as the baseline scd. Thunderdome uses sound/dam.scd. [Yue's + Lu's] Dj uses sound/lolo.scd. The program may hang for a little bit after you change these settings, depending on how many songs you have. Be patient.
The default songs that come with thunderdome are recorded in an unsupported encoding, so bpm detection and playback on them are not supported, but will work for new songs you add. If the program is taking a long time to load, you can delete the default thunderdome playlists.

Understanding the Top Buttons

0) Add Songs - lets you add songs from your computer or youtube (you can also drag/drop)
1)	NEW Playlist - Allows you to add new playlists
2)	DELETE:   will allow you to delete any checked marked songs or playlists
WARNING THIS CAN NOT BE UNDONE!!!
3)	Shuffle: as it says, it will shuffle selected songs or playlists around more randomly
4)	Sort: will sort the songs by beats per minute or name
5)	PLAY:   This will allow you to play the selected songs back to hear them.
6)	PAUSE: Pause the selected song
7)	STOP: will stop playing song,
8)	PREVIOUS: will go back 1 song
9)	NEXT: will skip ahead one song
10)	SETTINGS: will allow you to locate the directory of your mod if you aren't using pickles dj mod, set the scd being replaced, disable normalization of songs on import and optionally, reorganize your scds so they're all stored in folders named for each playlist with the file name as the song name (backup your mod before you press this!)

Adding Playlists
1)	Click on “NEW” on the top left of the screen. 
This will open a new pop-up window 
2)	On the top line, type the name of the Playlist you wish to create or add songs to.
3)	The Editor keeps track of all the playlists, songs, and the length of time for each set so you know exactly how long each run is.  Simply click on the PLUS icon on the side to expand or collapse the playlist
a.	When importing songs, the “OFF” setting is already added automatically so no need to add one in manually
4)	Deleting songs or playlists are easy. Simply click the box to the side of the playlist or song, then click delete. 
WARNING THIS CAN NOT BE UNDONE!!!

Adding Songs only
1) Click the add songs button and choose from my computer. Select the song or songs to import. It is recommended to do about 20 at a time.
a)	Songs will be volume normalized by default on import, but you can disable this in settings.
2)	Another way to add songs is to select and drag the song MP3 or .ogg files and drop them into the playlist of your choice. Remember to either put the songs 1st into the file folder you store you songs for the mod, or to use .ogg songs so it doesn’t create a new folder /.ogg wherever you imported from. The songs also DO NOT loop!
a.	NOTE: Any changes to the songs or playlists in game with effect the songs in the playlist. (Example: changing the names of the playlist/song in the mod in game or deleting a song will show up in the editor after a refresh.  A forced refresh can be done by clicking “NEW” and then backing out again.
How to Shuffle Song Order
3)	To Shuffle the songs order as they will appear in the mod pack, start by checking the box next to the playlist. 
 
Once the desired playlist is selected, simply click the “SHUFFLE” button at the top, and the songs with shuffle around.  
a.	NOTE: at this time, the Playlist will be deselected after every shuffle and will need to be selected again for another shuffle.
b. WARNING: If your playlist disappears after shuffle or any operation, don't panic. Just reboot the app. They will be back.

Sort
Sort by BPM (Beats per minute) will shift all the songs between either highest to lowest BPM or from lowest to highest BPM.  This allows for a smoother transition between songs without a sudden shift in tempo, if so desired.

Thanks to 0ceal0t for VFXEditor: https://github.com/0ceal0t/Dalamud-VFXEditor

If you need help, please join https://discord.gg/solona and post in the #help channel. If you get an error with a callstack a screenshot would be very helpful.
