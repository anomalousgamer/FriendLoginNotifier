# Friend Login Notifier

A small Dalamud plugin for Final Fantasy XIV that prints a bright green chat message when a friend changes from offline to online.

## How it works

The plugin checks friend statuses whenever FFXIV updates the normal Friends List. It does not automatically request or poll the game's servers.

Open the Friends List once to establish the initial status of your friends. When you open or refresh it again later, the plugin will report friends who changed from offline to online.

## Commands

- `/friendlogin` — Shows the plugin's current tracking status.
- `/friendlogin test` — Prints a test notification.

## Important limitations

- Notifications are not immediate.
- The Friends List must be opened or refreshed normally for the plugin to receive updated information.
- A friend must be observed offline during one update and online during a later update.
- If someone logs out and back in between two updates, the plugin cannot detect that change.

## Installation

Installation instructions will be added with the first packaged release.

## Disclaimer

This is an unofficial third-party plugin. It is not affiliated with or endorsed by Square Enix, XIVLauncher, or the Dalamud project.

## Development disclosure

This plugin was created with substantial AI assistance and manually tested in-game as someone learning how the process works.
