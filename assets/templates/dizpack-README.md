# Vendored asset tools (generated)

These files are COPIED IN BY DiztinGUIsh on every assembly export. Anything you
change here will be overwritten.

They live here so this repo can rebuild the ROM without DiztinGUIsh installed --
the build depends on python and the vendored assembler, never on Diz.

## Game-specific codecs

Some assets are packed in a format that belongs to one game -- a custom
compression, say. Those codecs are not in this directory: they are copied to
`tools/vendor/game/`, and only when this repo actually has an asset that needs
one. That directory is generated too, on the same terms as this one.

Editing a codec means editing it upstream and re-exporting. A copy left in this
directory, or in `tools/vendor/game/`, is overwritten without warning; the build
runs whatever export last put there.