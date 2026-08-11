# Agent sprite resources

These sprite sheets were moved out of the Web frontend when the Phaser-based
Workspace Studio was retired. They are intentionally kept as dormant Desktop
client resources for a possible future native client experience.

- `manifest.json` uses paths relative to this directory and is not tied to the
  former `/admin/assets` URL space.
- The files are not copied into the current Desktop publish output. Add an
  explicit `Resource` or `Content` item to `PuddingDesktop.csproj` only when a
  native Desktop feature starts consuming them.
- `contact-sheet.png` files are reference sheets; runtime animation should
  prefer each character's `spritesheet.webp` and `pet.json` metadata.
