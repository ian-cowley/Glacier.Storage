namespace Glacier.Storage.Doc;

using System;
using System.Runtime.InteropServices;

public enum GDocNodeType : ushort
{
    Document = 0,
    Section = 1,
    Heading1 = 2,
    Heading2 = 3,
    Heading3 = 4,
    Paragraph = 5,
    CodeBlock = 6,
    Table = 7,
    List = 8,
    ListItem = 9,
    Bold = 10,
    Italic = 11,
    Link = 12,
    Text = 13
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct GDocHeader
{
    public const uint MagicValue = 0x434F4447; // "GDOC"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public uint StringPoolOffset;
    public uint StringPoolLength;
    public uint AstTableOffset;
    public uint AstNodeCount;
    public uint AstNodeSize;
    public uint TokenSpanOffset;
    public uint TokenSpanCount;
    public uint TokenSpanSize;
    public uint VectorOffset;
    public uint VectorCount;
    public uint VectorDim;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
public struct GDocAstNode
{
    public const uint NoNode = 0xFFFFFFFF;
    public const uint NoVector = 0xFFFFFFFF;

    public uint Id;
    public ushort NodeType;
    public ushort Flags;
    public uint ParentId;
    public uint FirstChildId;
    public uint NextSiblingId;
    public uint TextOffset;
    public ushort TextLength;
    public ushort TokenSpanIndex;
    public uint VectorIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public struct GDocTokenSpan
{
    public uint StartOffset;
    public uint EndOffset;
    public ushort TokenType;
    public ushort Flags;
    public uint Metadata;
}
