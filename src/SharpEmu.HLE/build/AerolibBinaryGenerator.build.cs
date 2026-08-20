// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Build-time-only source. It is NOT compiled into SharpEmu.HLE (excluded via
// <Compile Remove> in SharpEmu.HLE.csproj); MSBuild's RoslynCodeTaskFactory compiles
// it standalone into a temporary task assembly (see the UsingTask in the csproj).
//
// It is deliberately self-contained — no dependency on SharpEmu.SourceGenerators.dll.
// The previous design loaded the freshly built analyzer assembly in a separate
// TaskHostFactory process; on the Windows host building over the \\wsl.localhost 9P
// share, that cross-process read of a just-written project output was not always
// coherent, intermittently failing with "GenerateAerolibBinaryTask could not be
// loaded" (MSB4062) and cascading into a missing SharpEmu.HLE reference assembly.
// Compiling the task from local source removes that cross-process/cross-node read.
//
// The NID derivation below is the same algorithm as SharpEmu.SourceGenerators.Ps5Nid;
// both are pinned to golden NIDs by tests (Ps5NidTests for the analyzer copy,
// AerolibCatalogTests for the catalog this task emits), so a divergence is caught.

// RoslynCodeTaskFactory compiles this file without the project's <Nullable> setting.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;

namespace SharpEmu.HLE.Build;

/// <summary>
/// Builds aerolib.bin (the runtime NID -> name catalog) from scripts/ps5_names.txt at
/// build time. Format: uint32 entry count, then per entry a byte-length-prefixed NID
/// and a ushort-length-prefixed name, little-endian UTF-8.
/// </summary>
public sealed class GenerateAerolibBinaryTask : ITask
{
    // SHA1(name + Suffix), first eight bytes byte-reversed, base64 ('+','-' alphabet,
    // no padding). Matches SharpEmu.SourceGenerators.Ps5Nid exactly.
    private static readonly byte[] Suffix =
    {
        0x51, 0x8D, 0x64, 0xA6, 0x35, 0xDE, 0xD8, 0xC1,
        0xE6, 0xB0, 0x39, 0xB1, 0xC3, 0xE5, 0x52, 0x30,
    };

    public IBuildEngine? BuildEngine { get; set; }

    public ITaskHost? HostObject { get; set; }

    [Required]
    public string NamesFile { get; set; } = string.Empty;

    [Required]
    public string OutputFile { get; set; } = string.Empty;

    private static string ComputeNid(string symbolName, SHA1 sha1)
    {
        var nameBytes = Encoding.UTF8.GetBytes(symbolName);
        var input = new byte[nameBytes.Length + Suffix.Length];
        nameBytes.CopyTo(input, 0);
        Suffix.CopyTo(input, nameBytes.Length);

        var hash = sha1.ComputeHash(input);

        var reversed = new byte[8];
        for (var index = 0; index < 8; index++)
        {
            reversed[index] = hash[7 - index];
        }

        return Convert.ToBase64String(reversed).TrimEnd('=').Replace('/', '-');
    }

    public bool Execute()
    {
        try
        {
            var names = new List<string>();
            foreach (var line in File.ReadAllLines(NamesFile))
            {
                var name = line.Trim();
                if (name.Length != 0)
                {
                    names.Add(name);
                }
            }

            var outputDirectory = Path.GetDirectoryName(OutputFile);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (var sha1 = SHA1.Create())
            using (var stream = File.Create(OutputFile))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((uint)names.Count);
                foreach (var name in names)
                {
                    var nidBytes = Encoding.UTF8.GetBytes(ComputeNid(name, sha1));
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    if (nameBytes.Length > ushort.MaxValue)
                    {
                        // A silent (ushort) truncation would corrupt the catalog.
                        throw new InvalidDataException(
                            $"Symbol name exceeds the format's ushort length prefix ({nameBytes.Length} bytes): '{name.Substring(0, 64)}...'");
                    }

                    writer.Write((byte)nidBytes.Length);
                    writer.Write(nidBytes);
                    writer.Write((ushort)nameBytes.Length);
                    writer.Write(nameBytes);
                }
            }

            BuildEngine?.LogMessageEvent(new BuildMessageEventArgs(
                $"aerolib: {names.Count} symbols -> {OutputFile}",
                helpKeyword: null,
                senderName: nameof(GenerateAerolibBinaryTask),
                MessageImportance.Normal));
            return true;
        }
        catch (Exception exception)
        {
            BuildEngine?.LogErrorEvent(new BuildErrorEventArgs(
                subcategory: null,
                code: "SHEMAERO",
                file: NamesFile,
                lineNumber: 0,
                columnNumber: 0,
                endLineNumber: 0,
                endColumnNumber: 0,
                message: exception.ToString(),
                helpKeyword: null,
                senderName: nameof(GenerateAerolibBinaryTask)));
            return false;
        }
    }
}
