A CS2 CounterStrikeSharp plugin that forces a Zeus Only Mode!

Zeus Only Mode creates a new gaming experience for those looking to ZAP their friends in multiplayer.

When enabled, this plugin strips players of their weapons, and only allows the Zeus to be used, 
as well as utility and knife.

To activate and deactivate the plugin, you must type css_zeus_mode 0 or 1 into the console.
Stripped loadouts are saved to an internal database, and are then restored upon disabling of the
plugin. 

Super Zeus
----------
Admins (@css/generic) can type superzeus in console, or superzeus / !superzeus / /superzeus in chat,
to toggle Super Zeus. The chat message is never shown to players. Add 1 or 0 to force it on or off.

While Super Zeus is on, any player can right-click with the Zeus to hurl an insta-kill lightning
ball. It costs the same charge as a normal zap, so it shares the Zeus's recharge. Enemies it hits
are killed and their ragdolls launched across the map; teammates are never hurt, and the ball
dissipates on them like it hit a wall. It explodes on walls and fizzles out after 2.5 seconds.
Super Zeus works with or without Zeus Only Mode.

Zeus Colors
-----------
Any player can press Reload while holding the Zeus, or type zeuscolor (with or without ! or /) in
chat, to open or close a menu and pick one color for both their Zeus zap and their lightning ball:
Red, Orange, Yellow, Green, Blue (Default), Purple, Pink, Gold or Silver. Chat the number of an
option to choose it, 8 / 9 to page up / down, or 0 to close. Choices are remembered per player in
addons/counterstrikesharp/configs/plugins/ZeusOnlyMode/zeus_colors.json. Super Zeus does not need
to be on to color your zap.

A colored zap is drawn over the stock zap. A colored lightning ball is drawn with colored rays, a
tail and sparks.

Note: the lightning ball effects are precached on map load, so change the map once after installing
or hot-loading the plugin.
