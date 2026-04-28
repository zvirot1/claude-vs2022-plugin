// Creates a Win32 .res file embedding the Menus.ctmenu as resource #1000
// This is the format VS expects for ProvideMenuResource("Menus.ctmenu", 1)
using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        string ctmenuPath = args[0]; // input: Menus.ctmenu
        string resPath = args[1];    // output: Menus.res

        byte[] ctmenuData = File.ReadAllBytes(ctmenuPath);

        using (var fs = new FileStream(resPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            // Win32 .res file format:
            // 1. Empty resource header (32-byte null header)
            WriteNullHeader(bw);

            // 2. The ctmenu resource entry
            // Resource type: "Menus.ctmenu" (custom string type)
            // Resource name: #1000 (integer)
            WriteResourceEntry(bw, "MENUS.CTMENU", 1000, ctmenuData);
        }

        Console.WriteLine($"Created {resPath} ({new FileInfo(resPath).Length} bytes) from {ctmenuPath} ({ctmenuData.Length} bytes)");
    }

    static void WriteNullHeader(BinaryWriter bw)
    {
        // Null resource header: DataSize=0, HeaderSize=0x20, Type=0, Name=0
        bw.Write((uint)0);   // DataSize
        bw.Write((uint)0x20); // HeaderSize
        bw.Write((ushort)0xFFFF); // Type (ordinal marker)
        bw.Write((ushort)0);      // Type = 0
        bw.Write((ushort)0xFFFF); // Name (ordinal marker)
        bw.Write((ushort)0);      // Name = 0
        bw.Write((uint)0);   // DataVersion
        bw.Write((ushort)0); // MemoryFlags
        bw.Write((ushort)0); // LanguageId
        bw.Write((uint)0);   // Version
        bw.Write((uint)0);   // Characteristics
    }

    static void WriteResourceEntry(BinaryWriter bw, string typeName, int nameId, byte[] data)
    {
        long startPos = bw.BaseStream.Position;

        // Calculate header size first
        // DataSize (4) + HeaderSize (4) + Type (variable) + Name (variable) + padding + fixed fields (16)

        // Write placeholders for DataSize and HeaderSize
        long dataSizePos = bw.BaseStream.Position;
        bw.Write((uint)data.Length); // DataSize
        long headerSizePos = bw.BaseStream.Position;
        bw.Write((uint)0);          // HeaderSize (placeholder)

        long headerStart = bw.BaseStream.Position;

        // Type: string "MENUS.CTMENU"
        foreach (char c in typeName)
            bw.Write((ushort)c);
        bw.Write((ushort)0); // null terminator

        // Align to DWORD
        while (bw.BaseStream.Position % 4 != 0)
            bw.Write((byte)0);

        // Name: ordinal #1000
        bw.Write((ushort)0xFFFF); // ordinal marker
        bw.Write((ushort)nameId);

        // Align to DWORD
        while (bw.BaseStream.Position % 4 != 0)
            bw.Write((byte)0);

        // Fixed fields
        bw.Write((uint)0);       // DataVersion
        bw.Write((ushort)0);     // MemoryFlags
        bw.Write((ushort)0x0409); // LanguageId (English US)
        bw.Write((uint)0);       // Version
        bw.Write((uint)0);       // Characteristics

        long headerEnd = bw.BaseStream.Position;
        uint headerSize = (uint)(headerEnd - startPos);

        // Write data
        bw.Write(data);

        // Align data to DWORD boundary
        while (bw.BaseStream.Position % 4 != 0)
            bw.Write((byte)0);

        // Go back and fix HeaderSize
        long endPos = bw.BaseStream.Position;
        bw.BaseStream.Position = headerSizePos;
        bw.Write(headerSize);
        bw.BaseStream.Position = endPos;
    }
}
