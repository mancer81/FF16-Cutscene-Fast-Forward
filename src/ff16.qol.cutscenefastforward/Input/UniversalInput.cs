using System.Runtime.InteropServices;
using System.Text;

namespace ff16.qol.cutscenefastforward.Input;

public enum ExtraKeyboardKey
{
    None = 0,
    Space = 0x20,
    Tab = 0x09,
    E = 0x45,
    F = 0x46,
    LeftShift = 0xA0,
    RightControl = 0xA3,
    Period = 0xBE
}

public enum ExtraGamepadButton
{
    None = 0,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    RightTrigger = -1
}

[Flags]
internal enum InputTriggerSource
{
    None = 0,
    StandardSkipButton = 1 << 0,
    ExtraKeyboardKey = 1 << 1,
    ExtraGamepadButton = 1 << 2
}

/// <summary>
/// Universal input reader supporting:
/// 1. FFXVI's native unified InputSnapshot (works automatically for DualSense HID, Xbox, DirectInput, Keyboard &amp; Mouse).
/// 2. Optional extra keyboard key (GetAsyncKeyState, gated on game window focus).
/// 3. Optional extra XInput gamepad button (gated on game window focus).
/// </summary>
internal sealed unsafe class UniversalInput
{
    /// <summary>
    /// FFXVI ActionId = 1: Confirm / Standard Dialogue Advance button
    /// (Cross on PlayStation / DualSense, A on Xbox, Confirm/E/Enter/LMB on Keyboard/Mouse).
    /// </summary>
    public const int ActionConfirmAdvance = 1;

    /// <summary>
    /// InputSnapshot buffer size is 0x2BC0 bytes; entries sit at actionId * 0x30 with slots at +0x524/+0x534/+0x544.
    /// (0x2BC0 - 0x550) / 0x30 = 205 max safe entries -> cap at 200.
    /// </summary>
    private const int MaxTraceActionId = 200;

    private readonly uint _currentPid = (uint)Environment.ProcessId;
    private readonly bool[] _prevActionHeld = new bool[MaxTraceActionId + 1];
    private ushort _prevXInputMask;
    private bool _prevExtraKeyHeld;
    private bool _xinput14Failed;
    private bool _xinput910Failed;

    /// <summary>
    /// Mirrors ffxvi.exe+0x28FBB0 (InputState::IsActionHeld) across all 3 device slots
    /// (gamepad, keyboard, mouse/alt).
    /// </summary>
    public static bool IsGameActionHeld(byte* inputSnapshot, int actionId)
    {
        if (inputSnapshot == null || actionId < 0 || actionId > MaxTraceActionId)
            return false;

        byte* entry = inputSnapshot + (actionId * 0x30);
        return entry[0x524] != 0 || entry[0x534] != 0 || entry[0x544] != 0;
    }

    public bool IsGameWindowFocused()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == _currentPid;
    }

    public InputTriggerSource PollFastForwardTriggers(
        byte* inputSnapshot,
        bool useStandardSkipButton,
        ExtraKeyboardKey extraKey,
        ExtraGamepadButton extraGamepad)
    {
        InputTriggerSource triggers = InputTriggerSource.None;

        // FFXVI's native InputSnapshot only updates when the game itself accepts input
        // (e.g. DualSense HID works in borderless fullscreen even after Alt-Tab when focus is on another monitor).
        if (useStandardSkipButton && IsGameActionHeld(inputSnapshot, ActionConfirmAdvance))
            triggers |= InputTriggerSource.StandardSkipButton;

        // Raw Win32 keyboard & XInput polling stay gated on game window focus so typing in other apps never triggers FF.
        if (IsGameWindowFocused())
        {
            if (extraKey != ExtraKeyboardKey.None && IsVirtualKeyHeld((int)extraKey))
                triggers |= InputTriggerSource.ExtraKeyboardKey;

            if (extraGamepad != ExtraGamepadButton.None && IsXInputButtonHeld(extraGamepad))
                triggers |= InputTriggerSource.ExtraGamepadButton;
        }

        return triggers;
    }

    public void ResetDebugTraceState()
    {
        Array.Clear(_prevActionHeld);
        _prevXInputMask = 0;
        _prevExtraKeyHeld = false;
    }

    public string? SampleNewlyPressedDebugInputs(byte* inputSnapshot, ExtraKeyboardKey extraKey)
    {
        StringBuilder? sb = null;

        if (inputSnapshot != null)
        {
            for (int id = 0; id <= MaxTraceActionId; id++)
            {
                byte* entry = inputSnapshot + (id * 0x30);
                byte s0 = entry[0x524];
                byte s1 = entry[0x534];
                byte s2 = entry[0x544];
                bool held = s0 != 0 || s1 != 0 || s2 != 0;

                if (held && !_prevActionHeld[id])
                {
                    sb ??= new StringBuilder(128);
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append($"ActionId={id}(0x{id:X})[s0={s0},s1={s1},s2={s2}]");
                }

                _prevActionHeld[id] = held;
            }
        }

        ushort xmask = GetCombinedXInputButtonsMask(out byte maxRt);
        ushort newlyPressedX = (ushort)(xmask & ~_prevXInputMask);
        _prevXInputMask = xmask;
        if (newlyPressedX != 0)
        {
            sb ??= new StringBuilder(64);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"XInputNew=0x{newlyPressedX:X4}(RT={maxRt})");
        }

        if (extraKey != ExtraKeyboardKey.None)
        {
            bool keyHeld = IsVirtualKeyHeld((int)extraKey);
            if (keyHeld && !_prevExtraKeyHeld)
            {
                sb ??= new StringBuilder(64);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"ExtraKey={extraKey}");
            }
            _prevExtraKeyHeld = keyHeld;
        }

        if (sb == null)
            return null;

        sb.Append($" | Focused={IsGameWindowFocused()}");
        return sb.ToString();
    }

    private static bool IsVirtualKeyHeld(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private ushort GetCombinedXInputButtonsMask(out byte maxRightTrigger)
    {
        ushort combined = 0;
        maxRightTrigger = 0;
        for (uint user = 0; user < 4; user++)
        {
            if (!TryGetXInputState(user, out XInputState state))
                continue;
            combined |= state.Gamepad.Buttons;
            if (state.Gamepad.RightTrigger > maxRightTrigger)
                maxRightTrigger = state.Gamepad.RightTrigger;
        }
        return combined;
    }

    private bool IsXInputButtonHeld(ExtraGamepadButton button)
    {
        for (uint user = 0; user < 4; user++)
        {
            if (!TryGetXInputState(user, out XInputState state))
                continue;

            if (button == ExtraGamepadButton.RightTrigger)
            {
                if (state.Gamepad.RightTrigger > 120)
                    return true;
            }
            else
            {
                ushort mask = (ushort)button;
                if ((state.Gamepad.Buttons & mask) != 0)
                    return true;
            }
        }
        return false;
    }

    private bool TryGetXInputState(uint userIndex, out XInputState state)
    {
        state = default;
        if (!_xinput14Failed)
        {
            try
            {
                return XInputGetState_1_4(userIndex, out state) == 0;
            }
            catch (DllNotFoundException)
            {
                _xinput14Failed = true;
            }
        }

        if (!_xinput910Failed)
        {
            try
            {
                return XInputGetState_9_1_0(userIndex, out state) == 0;
            }
            catch (DllNotFoundException)
            {
                _xinput910Failed = true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState_1_4(uint dwUserIndex, out XInputState pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState_9_1_0(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }
}
