# Nintendo Library Integration

Keep the games in your Nintendo Account's Virtual Game Cards collection in your Playnite library.

This branch targets Playnite 11 Alpha 2 (11.0.11.0), using Playnite SDK 11.0.0-alpha14.

## Features

- Imports your Nintendo Virtual Game Cards library, including Nintendo Switch and Nintendo Switch 2 titles.
- Adds the Nintendo source, the appropriate platform, and each game's Nintendo icon.
- Uses your Nintendo Account's country and language automatically, so the imported collection follows the account you use to sign in.
- Checks Nintendo's language mappings and remembers a working fallback separately for each account locale when needed.
- Shows Nintendo request errors in import notifications, with full details available by opening the notification.
- Includes an in-app Nintendo Account sign-in and keeps its browser session separate from other Playnite extensions.
- Can exclude entries that contain add-on content only, keeping DLC out of the game library.
- De-duplicates titles by their Nintendo application ID and normalizes game titles to reduce duplicate imports.

## Getting started

Open the add-on's **Settings** tab, select **Authenticate**, complete the Nintendo Account sign-in window, and save the settings. Run a library update to import the collection.

## Notes

Virtual Game Cards cover the titles registered to your Nintendo Account, which is a digital entitlement list. Games you own only on a cartridge do not appear in it, and so cannot be imported from this source.
