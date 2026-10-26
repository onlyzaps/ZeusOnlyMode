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
Admins (@css/generic) can type superzeus or css_superzeus in console, or !superzeus / /superzeus in chat.
The chat message is never shown to players. With no number it toggles Super Zeus off or on; add a number to set it:

- superzeus 0: off.
- superzeus 1: on, and every player's alt-fire is the lightning ball.
- superzeus 2: on with every fire mode; each player picks theirs by pressing Inspect with the Zeus in hand.

Super Zeus stays at its level across map changes and plugin reloads. It works with or without Zeus Only Mode.

Fire modes (right-click with the Zeus):

- Super Zeus: a lightning ball. It costs the same charge as a normal zap and shares the Zeus's recharge. Enemies it hits take 34
  damage, and bodies it kills are launched across the map. It explodes on walls and fizzles out after 2.5 seconds.
- Hyper Zeus: smaller balls for 20 damage, three times as fast as the normal zap, from a three-ball energy pool that refills over
  mp_taser_recharge_time; a normal zap empties the pool.
- Reflect Zeus: a slightly slower lightning ball that bounces once off a wall or floor, angle in equals angle out, then goes off on
  the next surface.
- Grapple: shoots a single line that costs a charge. When its tip catches a wall or floor you are pulled there quickly. If the tip
  itself hits an enemy they are killed, and you are pulled to where they died. The pull lets go if you stop moving, so it never pins
  you to a wall or drags you through one. You take no fall damage during the pull or for 3 seconds after it lets go.
- Camera: shoots a little webcam onto a wall (one per life; a shot that hits nothing can be taken again). After that, right-click
  switches your view to the camera and back; it starts looking back the way it was shot and pans with W/A/S/D (you stand still while
  watching, framed by a green border with a flashing REC). Its light
  turns red while you watch, and it goes away when you die. One bullet, zap or lightning ball breaks it, kicking out whoever
  is watching.
- Sensor Probe: shoots a probe that sticks into the ground (or whatever it hits) and flashes blue. When an enemy comes within
  about 160 units it flashes red, and your team hears an alarm and gets a chat line saying where. One probe per round (a shot that
  hits nothing can be taken again); it lasts until the round ends, even if you die.

A center panel names the mode each time Inspect switches it. Each player's last mode is remembered forever in
addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/fire_modes.json (with superzeus 1 everyone fires the lightning ball,
and saved modes come back with superzeus 2). Teammates are never hurt.

Zeus Colors
-----------
Any player can pick one color for their Zeus zap, lightning ball and grapple: Red, Orange, Yellow, Green, Blue (Default), Purple,
Pink, Gold or Silver. Hold the Zeus and press Reload to step to the next color, or Inspect to step back (with superzeus 2, Inspect picks your fire mode instead); a center panel shows the list. Each step takes
effect right away and the panel hides itself a few seconds after the last press, so it never stops you moving or shooting.
Choices are remembered per player in addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/zeus_colors.json. Super Zeus does
not need to be on to color your zap.

A colored zap is drawn over the stock zap. A colored lightning ball is drawn with colored rays, a tail and sparks.

Settings live in addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/settings.json (created on
first load): SoundVolume (0 to 1, default 0.4) sets the volume of every sound the plugin plays.
It is re-read whenever the plugin loads, so edit it and reload the plugin to apply a change.

Note: the lightning ball effects are precached on map load, so change the map once after installing
or hot-loading the plugin.
