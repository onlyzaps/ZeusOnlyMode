A CS2 CounterStrikeSharp plugin that forces a Zeus Only Mode!

Zeus Only Mode creates a new gaming experience for those looking to ZAP their friends in multiplayer.

When enabled, this plugin strips players of their weapons, and only allows the Zeus to be used, 
as well as utility and knife.

To activate and deactivate the plugin, you must type css_zeus_mode 0 or 1 into the console.
Stripped loadouts are saved to an internal database, and are then restored upon disabling of the
plugin. 

Super Zeus
----------
Admins can type superzeus (with or without ! or /) in chat, or css_superzeus in console, to toggle
Super Zeus. The chat message is never shown to players. Add 1 or 0 to force it on or off.

While Super Zeus is on, the Zeus reaches 3x as far, and any player can right-click with the Zeus to
hurl an insta-kill lightning ball. It costs the same charge as a normal zap, so it shares the Zeus's
recharge. Enemies it hits are killed and their ragdolls launched (LightningBallLaunchSpeed in the
config); teammates are never hurt, and the ball dissipates on them like it hit a wall. It explodes
on walls and fizzles out after 2.5 seconds.

Note: the lightning ball effects are precached on map load, so change the map once after installing
or hot-loading the plugin.
