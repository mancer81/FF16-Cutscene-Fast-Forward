# Changelog

All notable changes to **FF16 Cutscene Fast-Forward** will be documented in this file.

## [1.0.0] - 2026-11-21

### Added
- Contextual Fast-Forward (`1.0x` – `24.0x`, default `4.0x`) for full in-engine cutscenes (`ScenarioCutscene` / `QuestCutscene` / `LevelCutscene`) via Faith Engine's double-buffered `TaskGraph` Channel 1 scaling.
- Pre-rendered Bink2 (`.bk2`) movie Fast-Forward (`1.0x` – `8.0x`, default `4.0x`) for both standalone FMVs and embedded movie segments inside cutscenes, with automatic decode backpressure so releasing the button stops immediately.
- Universal input detection via FFXVI's native unified input buffer (`Confirm` — DualSense Cross, Xbox A, Keyboard/Mouse Confirm) plus optional extra keyboard key (`Space` by default) and optional XInput button.
- Hold and Toggle activation modes, with live configuration hot-reload in Reloaded-II (no game restart needed).
- Minimalist, click-through, resolution-scaled (1080p / 1440p / 4K) Win32 HUD indicator with configurable corner, scale, opacity, and 4-second auto-hide for clean immersion.
- Instant pause protection (Cutscene Pause Menu, System Pause, Active Time Lore) and borderless-fullscreen Alt-Tab + DualSense support.
- Optional opt-in Debug Mode with persistent `debug.log` for troubleshooting.
