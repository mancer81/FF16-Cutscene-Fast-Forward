namespace ff16.qol.cutscenefastforward.Game;

/// <summary>
/// Verified unique AOB signatures for FINAL FANTASY XVI (PC / Steam v1.03, no-Denuvo).
/// Every pattern matches exactly once in ffxvi.exe .text.
/// </summary>
internal static class Signatures
{
    /// <summary>
    /// MainFrameTick(nint framework) @ ffxvi.exe+0x9B0EB0.
    /// Runs once per frame on the main thread right before FrameClock delta computation and task dispatch.
    /// </summary>
    public const string MainFrameTick =
        "48 89 5C 24 10 48 89 6C 24 18 56 57 41 54 41 56 41 57 48 83 EC 50 " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 40 48 8B 1D ?? ?? ?? ??";

    /// <summary>
    /// TaskGraph::ExecuteFrame(nint taskGraph, FrameClock* clock) @ ffxvi.exe+0x813EAC.
    /// Executes all 0x172 engine task lists (EventManager, cutscenes, actors, camera, animations, VFX).
    /// Copies Pending TimeScaleChannels (+0x5128) -> Active (+0x515C) during Task 0x8D and checks PauseBitmask (+0x517C).
    /// </summary>
    public const string TaskGraphExecuteFrame =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 50 " +
        "4C 8B FA 48 8D 71 38 48 8B F9 33 ED";

    /// <summary>
    /// EventManager::StartEvent global load @ ffxvi.exe+0x6418B4.
    /// Instruction at +0: mov rbx, [rip + disp32] -> &amp;g_EventManager (0x18166A0).
    /// </summary>
    public const string EventMgrGlobalRef =
        "48 8B 1D ?? ?? ?? ?? B8 FF FF FF FF 48 8B F9 48 8B AB F8 03 00 00 48 39 83 00 04 00 00";

    /// <summary>
    /// bool EventManager::IsActive(nint eventMgr) @ ffxvi.exe+0x6485A8.
    /// Checks [eventMgr + 0x408] != null &amp;&amp; vtable[+0x50]().
    /// </summary>
    public const string EventMgrIsActive =
        "40 53 48 83 EC 20 48 8B 89 08 04 00 00 33 DB 48 85 C9 74 0C 48 8B 01 FF 50 50";

    /// <summary>
    /// bool Event::IsBlockingCutscene(nint activeEvent) @ ffxvi.exe+0x63F7F4.
    /// Distinguishes full blocking cutscenes (ScenarioCutscene / QuestCutscene / LevelCutscene)
    /// from ambient/non-blocking events.
    /// </summary>
    public const string EventIsBlockingCutscene =
        "40 53 48 83 EC 20 48 8B 01 48 8B D9 FF 50 68 84 C0 0F 84 ?? ?? ?? ?? 8B 9B B0 00 00 00";

    /// <summary>
    /// bool Event::CanPause(nint activeEvent) @ ffxvi.exe+0x63F748.
    /// </summary>
    public const string EventCanPause =
        "40 53 48 83 EC 20 48 8B D9 48 83 C1 10 48 8B 01 FF 90 40 01 00 00 84 C0 74 07";

    /// <summary>
    /// SimpleEventManager global load @ ffxvi.exe+0x2508FA.
    /// Instruction at +0: mov rax, [rip + disp32] -> &amp;g_SimpleEventManager (0x1816618).
    /// </summary>
    public const string SimpleEventMgrRef =
        "48 8B 05 ?? ?? ?? ?? 48 8B 88 E8 00 00 00 48 85 C9 74 2A 48 8B 01 FF 90 80 00 00 00 84 C0";

    /// <summary>
    /// Unified InputState global load @ ffxvi.exe+0x20B1AD.
    /// Instruction at +0: mov rdx, [rip + disp32] -> &amp;g_InputState (0x18170B8).
    /// Snapshot data begins at *(nint*)g_InputState + 0x08.
    /// </summary>
    public const string InputStateGlobalRef =
        "48 8B 15 ?? ?? ?? ?? 48 8B D9 41 B8 C0 00 00 00 49 8D 81 D0 50 10 00 " +
        "4C 0F 45 C0 48 83 C2 08 41 8B 38";

    /// <summary>
    /// Cutscene mode flag writer @ ffxvi.exe+0x45A2C5.
    /// Instruction at +0x12: mov rax, [rip + disp32] -> &amp;g_CutsceneModeState (0x2743670).
    /// byte [*(nint*)g_CutsceneModeState + 0x128E] == 1 when mode == 5 (Cutscene).
    /// </summary>
    public const string CutsceneModeUpdater =
        "B2 05 88 87 F4 1A 00 00 38 51 1F 74 05 C5 FA 10 71 30 48 8B 05 ?? ?? ?? ?? 0F 94 C1";

    /// <summary>
    /// MovieManager global load @ ffxvi.exe+0xC04C20.
    /// Instruction at +4: mov rcx, [rip + disp32] -> &amp;g_MovieManager (0x2743680).
    /// Holds vector&lt;MovieInstance*&gt; at [+0x38..+0x40] for both embedded cutscene .bk2 movies and standalone FMVs.
    /// </summary>
    public const string MovieMgrGlobalRef =
        "48 83 EC 28 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 05 E8 ?? ?? ?? ?? 48 83 C4 28 C3 CC CC 48 8B C4 48 89 58 18 55";

    /// <summary>
    /// FieldState + ControlLock global loads @ ffxvi.exe+0x20B20A.
    /// Instruction at +0x00: mov rax, [rip + disp32] -> &amp;g_FieldState (0x1816678, uint state @ +0x28).
    /// Instruction at +0x1E: mov rcx, [rip + disp32] -> &amp;g_ControlLock (0x1816A08, ulong activeMask @ +0x10).
    /// </summary>
    public const string FieldAndControlLockRef =
        "48 8B 05 ?? ?? ?? ?? F7 40 28 FA FF FF FF 0F 85 ?? ?? ?? ?? 83 78 28 01 0F 84 ?? ?? ?? ?? " +
        "48 8B 0D ?? ?? ?? ?? BA 18 00 00 00 E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? " +
        "48 8B 05 ?? ?? ?? ?? 48 8B 88 08 04 00 00 48 85 C9 74 13 E8 ?? ?? ?? ?? 84 C0 74 0A";

    public static unsafe nint ResolveRipRelative(nint instructionAddress, int dispOffset = 3, int instructionLength = 7)
    {
        int disp = *(int*)(instructionAddress + dispOffset);
        return instructionAddress + instructionLength + disp;
    }
}
