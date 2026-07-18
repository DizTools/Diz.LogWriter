# Vendored asset tools (generated)

These files are COPIED IN BY DiztinGUIsh on every assembly export. Anything you
change here will be overwritten.

They live here so this repo can rebuild the ROM without DiztinGUIsh installed --
the build depends on python and the vendored assembler, never on Diz.

## Overriding a codec

Put your own version in `tools/game/`, which is searched first and is never
touched by export. That's the place for game-specific codecs (custom compression,
odd tile layouts) that shouldn't live in Diz.