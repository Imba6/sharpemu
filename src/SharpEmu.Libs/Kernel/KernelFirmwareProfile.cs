// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Globalization;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// The single canonical PS5 system-software (firmware) version VirtualPS5
/// presents to the guest.
/// </summary>
/// <remarks>
/// The value is the native SDK uint32 encoding whose high 16 bits are the
/// major/minor version (for example <c>0x07000000</c> for 7.00,
/// <c>0x07610000</c> for 7.61). It is intentionally the only place a firmware
/// version lives: HLE code that needs the presented version — sysctl
/// <c>kern.46</c> today, generation/version gates later — reads it here rather
/// than embedding a constant.
///
/// Resolution policy, evaluated by <see cref="TryGetPresentedFirmwareVersion"/>:
/// <list type="number">
///   <item>An explicitly configured version always wins.</item>
///   <item>Otherwise Auto mode presents the guest's own compiled
///     <c>sdk_ps5_ver</c> (from <c>SceProcParam</c>), when the loader found one.
///     This is a fallback heuristic, not a permanent equivalence: firmware
///     version and compiled SDK version are distinct concepts, and an explicit
///     configuration is expected to override it.</item>
///   <item>If neither exists the version is genuinely unknown; callers must
///     surface that (kern.46 returns ENOENT) rather than invent one.</item>
/// </list>
/// </remarks>
public static class KernelFirmwareProfile
{
    private static readonly object Gate = new();

    private static uint? _explicitVersion;

    private static uint? _autoSdkPs5Version;

    /// <summary>
    /// Sets the resolution inputs for the current process. <paramref name="explicitVersion"/>
    /// is an operator-provided override; <paramref name="autoSdkPs5Version"/> is
    /// the guest's <c>sdk_ps5_ver</c> the loader parsed, or null when absent.
    /// A zero <paramref name="autoSdkPs5Version"/> is treated as absent.
    /// </summary>
    public static void Configure(uint? explicitVersion, uint? autoSdkPs5Version)
    {
        lock (Gate)
        {
            _explicitVersion = explicitVersion;
            _autoSdkPs5Version = autoSdkPs5Version is 0 or null ? null : autoSdkPs5Version;
        }
    }

    /// <summary>Clears all resolution inputs (test support).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _explicitVersion = null;
            _autoSdkPs5Version = null;
        }
    }

    /// <summary>
    /// Resolves the presented firmware version per the policy above. Returns
    /// false when neither an explicit value nor a guest SDK version is known.
    /// </summary>
    public static bool TryGetPresentedFirmwareVersion(out uint version)
    {
        lock (Gate)
        {
            if (_explicitVersion is { } configured)
            {
                version = configured;
                return true;
            }

            if (_autoSdkPs5Version is { } auto)
            {
                version = auto;
                return true;
            }
        }

        version = 0;
        return false;
    }

    /// <summary>
    /// Parses an operator-supplied firmware version string in the native uint32
    /// encoding (<c>0x07610000</c>, or decimal). Shared by every configuration
    /// surface so none of them re-implements the format.
    /// </summary>
    public static bool TryParseConfiguredVersion(string? text, out uint version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                trimmed.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out version);
        }

        return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
    }
}
