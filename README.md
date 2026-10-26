A CS2 CounterStrikeSharp plugin that forces a Zeus Only Mode!

Zeus Only Mode creates a new gaming experience for those looking to ZAP their friends in multiplayer.

When enabled, this plugin strips players of their weapons, and only allows the Zeus to be used, 
as well as utility and knife.

To activate and deactivate the plugin, you must type css_zeus_mode 0 or 1 into the console.
Stripped loadouts are saved to an internal database, and are then restored upon disabling of the
plugin. 

Requires CounterStrikeSharp 1.0.378 or newer (.NET 10).

Super Zeus
----------
Admins (@css/generic) can type superzeus or css_superzeus in console, or !superzeus / /superzeus in chat,
to toggle Super Zeus. The chat message is never shown to players. Add 1 or 0 to force it on or off. Super Zeus stays on across map changes and plugin reloads until it is turned off.

While Super Zeus is on, any player can right-click with the Zeus to hurl a lightning
ball. It costs the same charge as a normal zap, so it shares the Zeus's recharge. Enemies it hits take 34 damage, and bodies it kills are launched across the map; teammates are never hurt, and the ball
dissipates on them like it hit a wall. It explodes on walls and fizzles out after 2.5 seconds.
Super Zeus works with or without Zeus Only Mode.

Hyper Zeus (hyperzeus / css_hyperzeus in console, !hyperzeus / /hyperzeus in chat, same admin flag, 1 or 0 to force it) is the rapid-fire version:
balls are smaller, hit for 20 damage instead of 34, and fire three times as fast as the normal zap, from a three-ball energy pool that refills over mp_taser_recharge_time, and a normal zap empties the pool.
It works on its own or alongside Super Zeus, and is also remembered across map changes and reloads.

Chicken Zeus (chicken / css_chicken in console, !chicken / /chicken in chat, same admin flag, 1 or 0 to force it) makes right-click hurl live chickens
(one every 0.25 seconds, no charge needed). Each flies at a random angle, tumbling, and lands as a normal chicken where it hits; an enemy it hits takes 15 damage and a shove.
It takes priority over Super and Hyper Zeus while on. Chicken sounds play at ChickenSoundVolume in settings.json (default 0.2).

Zeus Colors
-----------
Any player can pick one color for both their Zeus zap and their lightning ball: Red, Orange,
Yellow, Green, Blue (Default), Purple, Pink, Gold or Silver. Hold the Zeus and press Reload to show
the color menu and step to the next color, or Inspect to step back. Each step takes effect right
away and the menu hides itself a few seconds after the last press, so it never stops you moving or
shooting. Typing zeuscolor in chat also shows the menu. Choices are remembered per player in
addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/zeus_colors.json. Super Zeus does not need
to be on to color your zap.

A colored zap is drawn over the stock zap. A colored lightning ball is drawn with colored rays, a
tail and sparks.

Settings live in addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/settings.json (created on
first load): SoundVolume (0 to 1, default 0.4) sets the volume of every sound the plugin plays.
It is re-read whenever the plugin loads, so edit it and reload the plugin to apply a change.

Note: the lightning ball effects are precached on map load, so change the map once after installing
or hot-loading the plugin.
