-- Minimal private compositor for screenshots: no autostart, no session env export.
hl.config({
  misc = { disable_hyprland_logo = true, disable_splash_rendering = true, disable_autoreload = true, disable_watchdog_warning = true },
  general = { gaps_in = 0, gaps_out = 0, border_size = 0 },
  decoration = { rounding = 0, shadow = { enabled = false }, blur = { enabled = false } },
  animations = { enabled = false },
  xwayland = { enabled = false },
  cursor = { invisible = true }, -- keep the pointer out of screenshots
})
-- Windows tile, as on Omarchy: one window fills the screen. shot.sh adds a float rule for --float.
