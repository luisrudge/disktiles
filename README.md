# disktiles

![A home folder as tiles, with the pointer over ubuntu.iso](docs/screenshots/treemap.png)

See where your disk space went, and get it back.

disktiles scans your home folder (or any folder, or the whole disk) and shows it as tiles, each
one as big as the space it takes. Open folders with the keyboard or the mouse, and mark the
things you want gone as you go. Nothing is removed until you review the list and confirm, and
deleting permanently always asks first.

It follows your Omarchy theme and font, and switches with it when you change themes.

## Install

```sh
curl -fsSL https://github.com/luisrudge/disktiles/releases/latest/download/install.sh | sh
```

This installs it for your user, no sudo needed. You get disktiles in the app launcher and a
`disktiles` command in `~/.local/bin`. Run the same command again to update.

To uninstall:

```sh
curl -fsSL https://github.com/luisrudge/disktiles/releases/latest/download/install.sh | sh -s uninstall
```

It runs on the Qt that comes with Omarchy, so there's nothing else to install. If the script says
your system is too old, update Omarchy and run it again. Releases are x86_64 only.

## Use

```sh
disktiles              # your home folder
disktiles ~/projects   # any folder
disktiles --disk       # the whole disk
disktiles --help       # all the options
```

Colour shows what kind of data a tile is: code, agent scratch, toolchains, synced files, git,
media, documents or caches. Hatched tiles are space you can probably get back, like caches and
build output. Press `t` to size and colour tiles by file count or by age instead.

The side panel shows the selected folder (and what git says about it, if it's a checkout), a
few things worth a look (caches, old worktrees, stale experiments), what you've marked, and how much free space you'll have
once it's gone.

When you're ready, press `c` to review everything you marked. Unmark anything you changed your
mind about, then move the rest to the trash or delete it permanently. Afterwards disktiles scans
again and shows how much space you actually got back.

disktiles won't remove `/`, your home folder, system folders or anything with a mount inside it.

![The review screen](docs/screenshots/review.png)

## Keys

Press `?` in the app to see all of them.

| Key | What it does |
| --- | --- |
| arrows / `hjkl` | move between tiles |
| `tab` | next largest tile |
| `enter` | open that folder |
| `backspace` / `esc` | go up a folder |
| `alt ←` `alt →` | back and forward |
| `space` / `x` | mark or unmark |
| `ctrl`-click | mark without moving the selection |
| `c` | review what you marked |
| `/` | filter by name |
| `t` | size, files or age |
| `d` | disk usage or apparent size |
| `i` | show or hide hidden files |
| `[` `]` | show fewer or more levels |
| `+` `-` `0` | zoom in, zoom out, reset |
| scroll | zoom toward the pointer, then go in |
| `shift`-scroll | pan |
| `r` | scan again |
| `v` | scan another volume |
| `ctrl o` | choose a folder to scan |
| `g` | scan the whole disk |
| `o` / right-click | show it in the file manager |
| `ctrl =` `ctrl -` `ctrl 0` | make everything bigger or smaller |
| `q` | quit |

On the review screen, `m` picks the trash, `p` picks permanent delete, `!` unmarks everything and
`enter` goes ahead. You can also save the list to a file with `s`, or press `a` to copy a prompt
that asks a coding agent to clean it up for you.

## Build it yourself

You need the .NET 10 SDK. Everything else comes with Omarchy.

```sh
git clone https://github.com/luisrudge/disktiles
cd disktiles
dotnet publish -t:Install      # install your build, like the script does
dotnet build -t:Uninstall      # remove it
dotnet run --project tests     # run the tests
```

disktiles is written in C# and QML with [Omasharp](https://github.com/luisrudge/omasharp).
`AGENTS.md` explains how the code is laid out.

## Credits

disktiles started as a port of Tobi's [disktree](https://github.com/tobi/disktree). MIT licensed,
see [LICENSE](LICENSE).
