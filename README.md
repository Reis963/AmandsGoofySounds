# Amands Goofy Sounds

Amands Goofy Sounds adds spatial meme sound effects to non-player characters in SPT. Sounds can play randomly or when a bot is hit, dies, or spots the player.

## Compatibility

- SPT 4.1.3
- Client-side BepInEx plugin

## Installation

1. Download the release ZIP.
2. Extract it into your SPT installation folder.
3. Confirm that `AmandsGoofySounds.dll` is located at `BepInEx/plugins/GoofySounds/AmandsGoofySounds.dll`.

## Configuration

The configuration file is created at `BepInEx/config/com.Amanda.GoofySounds.cfg` after the game starts with the plugin installed.

The available settings include:

- master and per-category enable switches;
- master and per-category volume controls;
- Random, Hit, Death, and Spotted chances;
- Random timer range and event cooldowns;
- playback distance, rolloff, and simultaneous-sound limit;
- `Character` or `VoipMixer` acoustic routing;
- sound-pack selection;
- optional detailed debug logging.

`Character` is the default audio route. `VoipMixer` uses Tarkov's VOIP mixer acoustics without transmitting voice data and falls back to `Character` if that mixer is unavailable.

Changes to `SoundPack` apply when the next raid loads. Packs added or removed while the game is running require a restart before they appear in the selection list.

## Custom sounds and sound packs

WAV, OGG, and MP3 files are supported. WAV files must use 16-bit PCM encoding.

The `Default` pack reads sounds from:

```text
BepInEx/plugins/GoofySounds/
|-- Random/
|-- Hit/
|-- Death/
`-- Spotted/
```

Additional packs use the following structure:

```text
BepInEx/plugins/GoofySounds/Packs/
`-- MySoundPack/
    |-- Random/
    |-- Hit/
    |-- Death/
    `-- Spotted/
```

A custom pack may omit categories. Missing categories remain empty and are not filled with sounds from `Default`.

## Troubleshooting

If sounds do not play:

1. Confirm that the DLL and audio folders use the paths shown above.
2. Confirm that at least one supported audio file exists in the expected category.
3. Enable `DebugLogs` in the configuration.
4. Review `BepInEx/LogOutput.log` for GoofySounds warnings and loading or playback messages.

## Credits and license

Originally created by Amands2Mello.

The source code is available under the [MIT License](LICENSE).

Audio files distributed in release archives may contain third-party meme sounds. They are not covered by the source-code MIT License; rights remain with their respective owners.
