using System.ComponentModel;
using ff16.qol.cutscenefastforward.Input;

namespace ff16.qol.cutscenefastforward.Configuration;

public enum ActivationMode
{
    Hold,
    Toggle
}

public enum OverlayCorner
{
    BottomRight,
    TopRight,
    BottomLeft,
    TopLeft
}

public class Config : Configurable<Config>
{
    [Category("General")]
    [DisplayName("Enabled")]
    [Description("Master toggle for Cutscene Fast-Forward. Changes apply live while the game is running.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [Category("Fast-Forward")]
    [DisplayName("Fast-Forward Speed")]
    [Description("Time scale multiplier while fast-forwarding an in-engine cutscene (1.0 = normal, 4.0-6.0 recommended, clamped to 1.0 - 24.0).")]
    [DefaultValue(4.0)]
    public double FastForwardSpeed { get; set; } = 4.0;

    [Category("Fast-Forward")]
    [DisplayName("Movie (.bk2) Fast-Forward Speed")]
    [Description("Playback speed multiplier for pre-rendered .bk2 movies (1.0 = normal, 3.0-4.0 recommended, clamped to 1.0 - 8.0). Automatically paces to CPU decode throughput.")]
    [DefaultValue(4.0)]
    public double MovieFastForwardSpeed { get; set; } = 4.0;

    [Category("Fast-Forward")]
    [DisplayName("Activation Mode")]
    [Description("Hold: fast-forwards while the button is held down (recommended). Toggle: press once to start, press again (or wait for cutscene end) to stop.")]
    [DefaultValue(ActivationMode.Hold)]
    public ActivationMode ActivationMode { get; set; } = ActivationMode.Hold;

    [Category("Controls (Universal)")]
    [DisplayName("Use Standard Confirm / Skip Button")]
    [Description("Uses FFXVI's native Confirm action (Cross on DualSense, A on Xbox, Confirm/E/Enter/LMB on Keyboard & Mouse). Works automatically across all controllers and keyboards.")]
    [DefaultValue(true)]
    public bool UseStandardSkipButton { get; set; } = true;

    [Category("Controls (Universal)")]
    [DisplayName("Extra Keyboard Key")]
    [Description("Additional keyboard key that activates Fast-Forward during cutscenes and movies (works alongside the standard button). Set to None to disable.")]
    [DefaultValue(ExtraKeyboardKey.Space)]
    public ExtraKeyboardKey ExtraKeyboardKey { get; set; } = ExtraKeyboardKey.Space;

    [Category("Controls (Universal)")]
    [DisplayName("Extra XInput Gamepad Button")]
    [Description("Optional extra XInput controller button for Fast-Forward (e.g. RightTrigger or RightShoulder). Set to None to use only the standard button.")]
    [DefaultValue(ExtraGamepadButton.None)]
    public ExtraGamepadButton ExtraGamepadButton { get; set; } = ExtraGamepadButton.None;

    [Category("On-Screen Indicator (HUD)")]
    [DisplayName("Show On-Screen Indicator")]
    [Description("Shows a minimal corner indicator when Fast-Forward is available or active. Automatically hidden during gameplay and pause menus.")]
    [DefaultValue(true)]
    public bool ShowOverlayIndicator { get; set; } = true;

    [Category("On-Screen Indicator (HUD)")]
    [DisplayName("Auto-Hide Idle Hint (Seconds)")]
    [Description("Hides the idle button hint after this many seconds so cutscenes stay uncluttered; reappears immediately while fast-forwarding. Set to 0 to keep it always visible during cutscenes.")]
    [DefaultValue(4.0)]
    public double OverlayAutoHideSeconds { get; set; } = 4.0;

    [Category("On-Screen Indicator (HUD)")]
    [DisplayName("Indicator Position")]
    [Description("Screen corner where the minimalist Fast-Forward indicator appears.")]
    [DefaultValue(OverlayCorner.BottomRight)]
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.BottomRight;

    [Category("On-Screen Indicator (HUD)")]
    [DisplayName("Indicator Scale")]
    [Description("Size multiplier for the on-screen indicator pill (0.40 - 1.50). Default 0.75 is compact and readable at 1080p, 1440p, and 4K.")]
    [DefaultValue(0.75)]
    public double OverlayScale { get; set; } = 0.75;

    [Category("On-Screen Indicator (HUD)")]
    [DisplayName("Indicator Opacity")]
    [Description("Transparency of the indicator when idle (0.25 - 1.0). Automatically becomes brighter while Fast-Forward is active.")]
    [DefaultValue(0.78)]
    public double OverlayOpacity { get; set; } = 0.78;

    [Category("Scene Scope")]
    [DisplayName("Apply to In-Engine Cutscenes")]
    [Description("Allow Fast-Forward during full in-engine cutscenes (ScenarioCutscene / QuestCutscene / LevelCutscene). Paused cutscenes are always excluded.")]
    [DefaultValue(true)]
    public bool ApplyToInEngineCutscenes { get; set; } = true;

    [Category("Scene Scope")]
    [DisplayName("Apply to Pre-Rendered Movies (.bk2)")]
    [Description("Allow Fast-Forward during pre-rendered Bink2 (.bk2) movies (both standalone FMVs and movie segments embedded inside cutscenes).")]
    [DefaultValue(true)]
    public bool ApplyToMovies { get; set; } = true;

    [Category("Scene Scope")]
    [DisplayName("Apply to Simple NPC Dialogues (SimpleEvent)")]
    [Description("Optional: also allow Fast-Forward during standard NPC conversations. Disabled by default because vanilla FFXVI already lets you skip these line-by-line.")]
    [DefaultValue(false)]
    public bool ApplyToSimpleEvents { get; set; } = false;

    [Category("Scene Scope")]
    [DisplayName("Apply to Experimental Side-Scenes")]
    [Description("Optional: also trigger on non-standard CutsceneMode / control-locked secondary events. Disabled by default so normal gameplay and dialogues stay 100% vanilla.")]
    [DefaultValue(false)]
    public bool ApplyToSideScenesAndFlyovers { get; set; } = false;

    [Category("Debug & Diagnostics")]
    [DisplayName("Debug Mode")]
    [Description("Enables diagnostic logging (scene transitions, Fast-Forward telemetry, live timeline in HUD, and debug.log file). Leave OFF for normal play.")]
    [DefaultValue(false)]
    public bool DebugLogging { get; set; } = false;

    [Category("Debug & Diagnostics")]
    [DisplayName("Write Debug Log File (debug.log)")]
    [Description("When Debug Mode is ON, writes a crash-safe debug.log in Reloaded-II's User/Mods/ff16.qol.cutscenefastforward folder.")]
    [DefaultValue(true)]
    public bool DebugLogToFile { get; set; } = true;

    [Category("Debug & Diagnostics")]
    [DisplayName("Trace Scene Inputs")]
    [Description("When Debug Mode is ON, logs native FFXVI ActionIds and keyboard/XInput presses during scenes. Useful only for troubleshooting controls.")]
    [DefaultValue(false)]
    public bool DebugTraceSceneInputs { get; set; } = false;

    public double GetClampedSpeed() => Math.Clamp(FastForwardSpeed, 1.0, 24.0);
    public double GetClampedMovieSpeed() => Math.Clamp(MovieFastForwardSpeed, 1.0, 8.0);
    public double GetClampedAutoHideSeconds() => Math.Clamp(OverlayAutoHideSeconds, 0.0, 30.0);
    public double GetClampedOverlayScale() => Math.Clamp(OverlayScale, 0.40, 1.50);
    public double GetClampedOpacity() => Math.Clamp(OverlayOpacity, 0.25, 1.0);
}
