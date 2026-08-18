using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace VirtualPS5.HLE.Pad;

public static class PadExports
{
    private const int PadErrorInvalidHandle =
        unchecked((int)0x80920003);

    private const int PadErrorNotInitialized =
        unchecked((int)0x80920005);

    private const int PadErrorDeviceNotConnected =
        unchecked((int)0x80920007);

    private const int PrimaryUserId = 0x10000000;
    private const int PrimaryPadHandle = 1;
    private const int StandardPortType = 0;
    private const int PadDataSize = 0x78;

    private const uint ButtonShare = 0x0001;
    private const uint ButtonL3 = 0x0002;
    private const uint ButtonR3 = 0x0004;
    private const uint ButtonOptions = 0x0008;
    private const uint ButtonUp = 0x0010;
    private const uint ButtonRight = 0x0020;
    private const uint ButtonDown = 0x0040;
    private const uint ButtonLeft = 0x0080;
    private const uint ButtonL2 = 0x0100;
    private const uint ButtonR2 = 0x0200;
    private const uint ButtonL1 = 0x0400;
    private const uint ButtonR1 = 0x0800;
    private const uint ButtonTriangle = 0x1000;
    private const uint ButtonCircle = 0x2000;
    private const uint ButtonCross = 0x4000;
    private const uint ButtonSquare = 0x8000;
    private const uint ButtonTouchPad = 0x100000;

    private static int _initialized;

    private static readonly long StartTimestamp =
        Stopwatch.GetTimestamp();

    private readonly record struct PadState(
        uint Buttons,
        byte LeftX,
        byte LeftY,
        byte RightX,
        byte RightY,
        byte L2,
        byte R2);

    [SysAbiExport(
        ExportName = "scePadInit",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int Init(CpuContext ctx)
    {
        HostPlatform.Current.Input.EnsureStarted();

        Volatile.Write(
            ref _initialized,
            1);

        Console.Error.WriteLine(
            "[VPS5][PAD] scePadInit");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "scePadOpen",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int Open(CpuContext ctx)
    {
        var userId =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var type =
            unchecked((int)ctx[CpuRegister.Rsi]);

        var index =
            unchecked((int)ctx[CpuRegister.Rdx]);

        var parameterAddress =
            ctx[CpuRegister.Rcx];

        if (Volatile.Read(
                ref _initialized) == 0)
        {
            return ctx.SetReturn(
                PadErrorNotInitialized);
        }

        if (userId != PrimaryUserId ||
            type != StandardPortType ||
            index != 0 ||
            parameterAddress != 0)
        {
            return ctx.SetReturn(
                PadErrorDeviceNotConnected);
        }

        var input = HostPlatform.Current.Input;
        input.EnsureStarted();

        Console.Error.WriteLine(
            input.DescribeConnectedGamepad() is { } gamepadName
                ? $"[VPS5][PAD] scePadOpen -> handle={PrimaryPadHandle}, host={gamepadName}"
                : $"[VPS5][PAD] scePadOpen -> handle={PrimaryPadHandle}, host=keyboard");

        /*
         * scePadOpen returns the handle directly,
         * not an Orbis error code on success.
         */
        return ctx.SetReturn(
            PrimaryPadHandle);
    }

    [SysAbiExport(
        ExportName = "scePadReadState",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int ReadState(CpuContext ctx)
    {
        var handle =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var dataAddress =
            ctx[CpuRegister.Rsi];

        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(
                PadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state =
            ReadHostInputState();

        Span<byte> data =
            stackalloc byte[PadDataSize];

        data.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(
            data[0x00..],
            state.Buttons);

        data[0x04] = state.LeftX;
        data[0x05] = state.LeftY;
        data[0x06] = state.RightX;
        data[0x07] = state.RightY;
        data[0x08] = state.L2;
        data[0x09] = state.R2;

        /*
         * Neutral orientation quaternion W.
         */
        BinaryPrimitives.WriteSingleLittleEndian(
            data[0x18..],
            1.0f);

        /*
         * The VirtualPS5 primary pad is always logically connected.
         * Keyboard is the fallback when no physical controller is present.
         */
        data[0x4C] = 1;

        var elapsedTicks =
            Stopwatch.GetTimestamp() - StartTimestamp;

        var timestampMicroseconds =
            (ulong)Math.Max(
                0,
                elapsedTicks * 1_000_000L /
                Stopwatch.Frequency);

        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);

        data[0x68] = 1;

        if (!ctx.Memory.TryWrite(
                dataAddress,
                data))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        /*
         * Intentionally no per-poll logging here. Real games call
         * scePadReadState every frame and the old diagnostic line flooded
         * stdout at display rate.
         */
        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static PadState ReadHostInputState()
    {
        var input =
            HostPlatform.Current.Input;

        var acceptsKeyboardInput =
            input.IsHostWindowFocused();

        var buttons = acceptsKeyboardInput
            ? ReadKeyboardButtons(input)
            : 0u;

        var leftX = acceptsKeyboardInput
            ? ReadAnalogStick(
                input.IsKeyDown(0x41),
                input.IsKeyDown(0x44))
            : (byte)128;

        var leftY = acceptsKeyboardInput
            ? ReadAnalogStick(
                input.IsKeyDown(0x57),
                input.IsKeyDown(0x53))
            : (byte)128;

        var rightX = acceptsKeyboardInput
            ? ReadAnalogStick(
                input.IsKeyDown(0x4A),
                input.IsKeyDown(0x4C))
            : (byte)128;

        var rightY = acceptsKeyboardInput
            ? ReadAnalogStick(
                input.IsKeyDown(0x49),
                input.IsKeyDown(0x4B))
            : (byte)128;

        var l2 = acceptsKeyboardInput &&
                 input.IsKeyDown(0x52)
            ? (byte)255
            : (byte)0;

        var r2 = acceptsKeyboardInput &&
                 input.IsKeyDown(0x46)
            ? (byte)255
            : (byte)0;

        Span<HostGamepadState> gamepads =
            stackalloc HostGamepadState[2];

        var gamepadCount =
            Math.Min(
                input.GetGamepadStates(gamepads),
                gamepads.Length);

        for (var index = 0; index < gamepadCount; index++)
        {
            var gamepad =
                gamepads[index];

            if (!gamepad.Connected)
            {
                continue;
            }

            buttons |=
                ToOrbisButtons(
                    gamepad.Buttons);

            leftX =
                MergeAxis(
                    gamepad.LeftX,
                    leftX);

            leftY =
                MergeAxis(
                    gamepad.LeftY,
                    leftY);

            rightX =
                MergeAxis(
                    gamepad.RightX,
                    rightX);

            rightY =
                MergeAxis(
                    gamepad.RightY,
                    rightY);

            l2 =
                Math.Max(
                    l2,
                    gamepad.LeftTrigger);

            r2 =
                Math.Max(
                    r2,
                    gamepad.RightTrigger);
        }

        return new PadState(
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2);
    }

    private static uint ReadKeyboardButtons(
        IHostInput input)
    {
        uint buttons = 0;

        if (input.IsKeyDown(0x25)) buttons |= ButtonLeft;
        if (input.IsKeyDown(0x27)) buttons |= ButtonRight;
        if (input.IsKeyDown(0x26)) buttons |= ButtonUp;
        if (input.IsKeyDown(0x28)) buttons |= ButtonDown;

        if (input.IsKeyDown(0x5A) ||
            input.IsKeyDown(0x0D)) buttons |= ButtonCross;

        if (input.IsKeyDown(0x58) ||
            input.IsKeyDown(0x1B)) buttons |= ButtonCircle;

        if (input.IsKeyDown(0x43)) buttons |= ButtonSquare;
        if (input.IsKeyDown(0x56)) buttons |= ButtonTriangle;

        if (input.IsKeyDown(0x51)) buttons |= ButtonL1;
        if (input.IsKeyDown(0x45)) buttons |= ButtonR1;
        if (input.IsKeyDown(0x52)) buttons |= ButtonL2;
        if (input.IsKeyDown(0x46)) buttons |= ButtonR2;

        if (input.IsKeyDown(0x09) ||
            input.IsKeyDown(0x08)) buttons |= ButtonOptions;

        return buttons;
    }

    private static uint ToOrbisButtons(
        HostGamepadButtons buttons)
    {
        uint result = 0;

        if ((buttons & HostGamepadButtons.Create) != 0) result |= ButtonShare;
        if ((buttons & HostGamepadButtons.Up) != 0) result |= ButtonUp;
        if ((buttons & HostGamepadButtons.Down) != 0) result |= ButtonDown;
        if ((buttons & HostGamepadButtons.Left) != 0) result |= ButtonLeft;
        if ((buttons & HostGamepadButtons.Right) != 0) result |= ButtonRight;
        if ((buttons & HostGamepadButtons.Cross) != 0) result |= ButtonCross;
        if ((buttons & HostGamepadButtons.Circle) != 0) result |= ButtonCircle;
        if ((buttons & HostGamepadButtons.Square) != 0) result |= ButtonSquare;
        if ((buttons & HostGamepadButtons.Triangle) != 0) result |= ButtonTriangle;
        if ((buttons & HostGamepadButtons.L1) != 0) result |= ButtonL1;
        if ((buttons & HostGamepadButtons.R1) != 0) result |= ButtonR1;
        if ((buttons & HostGamepadButtons.L2) != 0) result |= ButtonL2;
        if ((buttons & HostGamepadButtons.R2) != 0) result |= ButtonR2;
        if ((buttons & HostGamepadButtons.L3) != 0) result |= ButtonL3;
        if ((buttons & HostGamepadButtons.R3) != 0) result |= ButtonR3;
        if ((buttons & HostGamepadButtons.Options) != 0) result |= ButtonOptions;
        if ((buttons & HostGamepadButtons.TouchPad) != 0) result |= ButtonTouchPad;

        return result;
    }

    private static byte ReadAnalogStick(
        bool negative,
        bool positive)
    {
        if (negative && !positive)
        {
            return 0;
        }

        if (positive && !negative)
        {
            return 255;
        }

        return 128;
    }

    private static byte MergeAxis(
        byte controller,
        byte keyboard)
    {
        const int Deadzone = 10;

        return Math.Abs(
                   controller - 128) > Deadzone
            ? controller
            : keyboard;
    }
}
