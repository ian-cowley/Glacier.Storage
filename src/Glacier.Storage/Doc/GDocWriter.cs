namespace Glacier.Storage.Doc;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public sealed class GDocWriter
{
    private readonly MemoryStream _stringPool = new();
    private readonly List<GDocAstNode> _nodes = new();
    private readonly List<GDocTokenSpan> _tokenSpans = new();
    private readonly List<float[]> _vectors = new();
    private readonly int _vectorDim;

    public GDocWriter(int vectorDim = 768)
    {
        _vectorDim = vectorDim;
    }

    public uint AddString(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        uint offset = (uint)_stringPool.Position;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _stringPool.Write(bytes, 0, bytes.Length);
        return offset;
    }

    public uint AddVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != _vectorDim)
        {
            throw new ArgumentException($"Vector dimension must be {_vectorDim}.", nameof(vector));
        }

        uint index = (uint)_vectors.Count;
        _vectors.Add(vector.ToArray());
        return index;
    }

    public uint AddTokenSpan(uint startOffset, uint endOffset, ushort tokenType, ushort flags = 0, uint metadata = 0)
    {
        uint index = (uint)_tokenSpans.Count;
        _tokenSpans.Add(new GDocTokenSpan
        {
            StartOffset = startOffset,
            EndOffset = endOffset,
            TokenType = tokenType,
            Flags = flags,
            Metadata = metadata
        });
        return index;
    }

    public uint AddNode(
        GDocNodeType nodeType,
        string? text = null,
        uint parentId = GDocAstNode.NoNode,
        uint firstChildId = GDocAstNode.NoNode,
        uint nextSiblingId = GDocAstNode.NoNode,
        uint tokenSpanIndex = 0,
        uint vectorIndex = GDocAstNode.NoVector,
        ushort flags = 0)
    {
        uint id = (uint)_nodes.Count;
        uint textOffset = 0;
        ushort textLen = 0;

        if (!string.IsNullOrEmpty(text))
        {
            textOffset = AddString(text);
            textLen = (ushort)Encoding.UTF8.GetByteCount(text);
        }

        _nodes.Add(new GDocAstNode
        {
            Id = id,
            NodeType = (ushort)nodeType,
            Flags = flags,
            ParentId = parentId,
            FirstChildId = firstChildId,
            NextSiblingId = nextSiblingId,
            TextOffset = textOffset,
            TextLength = textLen,
            TokenSpanIndex = (ushort)tokenSpanIndex,
            VectorIndex = vectorIndex
        });

        return id;
    }

    public void LinkChild(uint parentId, uint childId)
    {
        var parent = _nodes[(int)parentId];
        if (parent.FirstChildId == GDocAstNode.NoNode)
        {
            parent.FirstChildId = childId;
            _nodes[(int)parentId] = parent;
        }
        else
        {
            uint current = parent.FirstChildId;
            while (true)
            {
                var currNode = _nodes[(int)current];
                if (currNode.NextSiblingId == GDocAstNode.NoNode)
                {
                    currNode.NextSiblingId = childId;
                    _nodes[(int)current] = currNode;
                    break;
                }
                current = currNode.NextSiblingId;
            }
        }

        var child = _nodes[(int)childId];
        child.ParentId = parentId;
        _nodes[(int)childId] = child;
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        WriteToStream(ms);
        return ms.ToArray();
    }

    public void WriteToFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteToStream(fs);
    }

    public void WriteToStream(Stream destination)
    {
        const int headerSize = 64;
        byte[] stringBytes = _stringPool.ToArray();

        uint stringPoolOffset = headerSize;
        uint stringPoolLength = (uint)stringBytes.Length;

        uint astTableOffset = stringPoolOffset + stringPoolLength;
        astTableOffset = (astTableOffset + 7) & ~7u;

        uint astNodeCount = (uint)_nodes.Count;
        uint astNodeSize = 32;

        uint tokenSpanOffset = astTableOffset + (astNodeCount * astNodeSize);
        tokenSpanOffset = (tokenSpanOffset + 7) & ~7u;

        uint tokenSpanCount = (uint)_tokenSpans.Count;
        uint tokenSpanSize = 16;

        uint vectorOffset = tokenSpanOffset + (tokenSpanCount * tokenSpanSize);
        vectorOffset = (vectorOffset + 7) & ~7u;

        uint vectorCount = (uint)_vectors.Count;

        var header = new GDocHeader
        {
            Magic = GDocHeader.MagicValue,
            Version = GDocHeader.CurrentVersion,
            StringPoolOffset = stringPoolOffset,
            StringPoolLength = stringPoolLength,
            AstTableOffset = astTableOffset,
            AstNodeCount = astNodeCount,
            AstNodeSize = astNodeSize,
            TokenSpanOffset = tokenSpanOffset,
            TokenSpanCount = tokenSpanCount,
            TokenSpanSize = tokenSpanSize,
            VectorOffset = vectorOffset,
            VectorCount = vectorCount,
            VectorDim = (uint)_vectorDim
        };

        // Write header
        Span<byte> headerBytes = stackalloc byte[headerSize];
        MemoryMarshal.Write(headerBytes, in header);
        destination.Write(headerBytes);

        // Write string pool
        destination.Write(stringBytes);

        // Padding to AST table
        PadTo(destination, astTableOffset);

        // Write AST nodes
        Span<byte> nodeBytes = stackalloc byte[32];
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            MemoryMarshal.Write(nodeBytes, in node);
            destination.Write(nodeBytes);
        }

        // Padding to Token spans
        PadTo(destination, tokenSpanOffset);

        // Write Token spans
        Span<byte> spanBytes = stackalloc byte[16];
        for (int i = 0; i < _tokenSpans.Count; i++)
        {
            var span = _tokenSpans[i];
            MemoryMarshal.Write(spanBytes, in span);
            destination.Write(spanBytes);
        }

        // Padding to Vectors
        PadTo(destination, vectorOffset);

        // Write Vectors
        for (int i = 0; i < _vectors.Count; i++)
        {
            ReadOnlySpan<float> vec = _vectors[i];
            ReadOnlySpan<byte> rawVec = MemoryMarshal.AsBytes(vec);
            destination.Write(rawVec);
        }

        destination.Flush();
    }

    private static void PadTo(Stream stream, uint targetOffset)
    {
        while ((uint)stream.Position < targetOffset)
        {
            stream.WriteByte(0);
        }
    }
}
