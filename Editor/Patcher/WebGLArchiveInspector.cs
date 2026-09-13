using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Reads a static library and says whether a WebGL player can link it.
    /// </summary>
    /// <remarks>
    /// A .a file says nothing about what is inside it. An archive of MSVC objects,
    /// which is what CMake produces on Windows when it falls back to its Visual
    /// Studio generator, imports into Unity without complaint and only fails once
    /// the build reaches wasm-ld - minutes in, as one "neither Wasm object file nor
    /// LLVM bitcode" warning per object followed by undefined symbols.
    ///
    /// Deliberately free of Unity types, so it can be exercised outside the editor.
    /// </remarks>
    internal static class WebGLArchiveInspector
    {
        private static readonly byte[] ArchiveMagic = Encoding.ASCII.GetBytes("!<arch>\n");

        private const int MemberHeaderLength = 60;

        /// <summary>
        /// Null if every object in the archive is WebAssembly or LLVM bitcode -
        /// the two things wasm-ld accepts - and otherwise a phrase describing what
        /// is wrong, fit to follow "it cannot be linked because".
        /// </summary>
        public static string DescribeProblem(string path)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return $"it could not be read ({ex.Message})";
            }

            // A universal Mach-O library wraps one archive per architecture in a
            // fat header. It is what Apple toolchains ship - Dissonance's own iOS
            // libopus.a is one - and never anything wasm-ld can use.
            if (data.Length >= 4 && data[0] == 0xCA && data[1] == 0xFE && data[2] == 0xBA && data[3] == 0xBE)
                return "it is a universal Mach-O library for macOS or iOS, not WebAssembly";

            if (data.Length < ArchiveMagic.Length || !data.Take(ArchiveMagic.Length).SequenceEqual(ArchiveMagic))
                return "it is not a static library archive";

            var usable = 0;
            var foreign = 0;
            string foreignKind = null;

            var position = ArchiveMagic.Length;
            while (position + MemberHeaderLength <= data.Length)
            {
                var name = Encoding.ASCII.GetString(data, position, 16).TrimEnd();
                var sizeText = Encoding.ASCII.GetString(data, position + 48, 10).Trim();

                if (!int.TryParse(sizeText, out var size) || size < 0)
                    return "its member headers are malformed";

                var body = position + MemberHeaderLength;
                if (body + size > data.Length)
                    return "it is truncated";

                if (!IsIndex(name) && size >= 4)
                {
                    if (IsWasm(data, body) || IsBitcode(data, body))
                    {
                        usable++;
                    }
                    else
                    {
                        foreign++;
                        foreignKind = foreignKind ?? DescribeObject(data, body);
                    }
                }

                // Members are padded to an even length.
                position = body + size + (size & 1);
            }

            if (foreign > 0)
                return $"{foreign} of its {usable + foreign} object files are {foreignKind}, not WebAssembly";

            if (usable == 0)
                return "it contains no object files";

            return null;
        }

        /// <summary>
        /// The archive's own bookkeeping rather than an object: the symbol index
        /// ("/" in GNU and Microsoft archives, "__.SYMDEF" in BSD ones) and the
        /// long name table ("//"). "/0", "/30" and so on are real objects whose
        /// names live in that table.
        /// </summary>
        private static bool IsIndex(string name)
        {
            return name == "/"
                || name == "//"
                || name == "/SYM64/"
                || name.StartsWith("__.SYMDEF", StringComparison.Ordinal);
        }

        private static bool IsWasm(byte[] data, int offset)
        {
            return data[offset] == 0x00 && data[offset + 1] == (byte)'a' && data[offset + 2] == (byte)'s' && data[offset + 3] == (byte)'m';
        }

        private static bool IsBitcode(byte[] data, int offset)
        {
            // Raw bitcode, and bitcode inside its wrapper header.
            return (data[offset] == (byte)'B' && data[offset + 1] == (byte)'C' && data[offset + 2] == 0xC0 && data[offset + 3] == 0xDE)
                || (data[offset] == 0xDE && data[offset + 1] == 0xC0 && data[offset + 2] == 0x17 && data[offset + 3] == 0x0B);
        }

        private static string DescribeObject(byte[] data, int offset)
        {
            var b0 = data[offset];
            var b1 = data[offset + 1];
            var b2 = data[offset + 2];
            var b3 = data[offset + 3];

            if (b0 == 0x64 && b1 == 0x86)
                return "x86-64 Windows objects (MSVC)";
            if (b0 == 0x4C && b1 == 0x01)
                return "x86 Windows objects (MSVC)";
            if (b0 == 0x64 && b1 == 0xAA)
                return "ARM64 Windows objects (MSVC)";
            if (b0 == 0x7F && b1 == (byte)'E' && b2 == (byte)'L' && b3 == (byte)'F')
                return "ELF objects (Linux or Android)";
            if ((b0 == 0xCF || b0 == 0xCE) && b1 == 0xFA && b2 == 0xED && b3 == 0xFE)
                return "Mach-O objects (macOS or iOS)";

            return "in a format that is not WebAssembly";
        }
    }
}
