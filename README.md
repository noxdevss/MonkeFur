# MonkeFur

Adds fur with physics to your gorilla in Gorilla Tag. Needs BepInEx 5.

## Install

Put `MonkeFur.dll` in `Gorilla Tag/BepInEx/plugins/MonkeFur/`.

Building it yourself: `dotnet build -c Release`. It copies the dll into your plugins folder automatically. If the game isn't in the default Steam folder, add `-p:GamePath="path\to\Gorilla Tag"`.

## Usage

F8 turns the fur on and off.

The fur uses your body's material, so it matches your color and changes when you get tagged. Fur on your head only shows up in mirrors and cameras, so it doesn't get in your face.

## Computer screen

If you have NoxsCustomComputer installed, a FUR screen shows up on the computer. You don't need it; without Nox the mod works the same and just skips the screen.

- Option 1 turns the fur on/off
- Number keys pick a setting
- Option 2 / Option 3 lower or raise it
- Enter resets it to the default

## Config

Settings are in `BepInEx/config/com.monkefur.gorillatag.cfg`, which is created the first time you launch. Physics settings update live. Shape and look settings rebuild the fur.

| Setting | Default | What it does |
|---|---|---|
| StrandCount | 10000 | How many strands. Mostly a GPU cost |
| GuideCount | 300 | How many strands get physics. Others copy the nearest one. CPU cost |
| FurLength | 0.045 | Strand length in meters |
| StrandWidth | 0.011 | Strand width in meters |
| Stiffness | 0.22 | How hard strands spring back |
| Damping | 0.9 | Higher is bouncier |
| Inertia | 0.6 | How much fur swings when you move your arms |
| WorldDrag | 0.05 | How much fur trails behind when you move fast |
| GravityScale | 0.35 | How much the fur droops |
| FurSubmeshes | 0 | `0,1` adds fur to the chest and face too |
| HeadFurInFirstPerson | false | Show head fur in first person |
