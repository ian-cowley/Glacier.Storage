namespace Glacier.Storage.Tests;

using System;
using System.IO;
using Glacier.Storage.Doc;
using Xunit;

public class GDocTests
{
    [Fact]
    public void GDoc_WriteAndReadMemory_Roundtrip()
    {
        var writer = new GDocWriter(vectorDim: 4);

        // Document root
        uint root = writer.AddNode(GDocNodeType.Document, "Document Title");

        // Section
        uint sec1 = writer.AddNode(GDocNodeType.Section, "Introduction");
        writer.LinkChild(root, sec1);

        // Paragraph with vector
        float[] vec = [0.5f, 0.5f, 0.5f, 0.5f];
        uint vecIdx = writer.AddVector(vec);
        uint p1 = writer.AddNode(GDocNodeType.Paragraph, "This is the first paragraph.", vectorIndex: vecIdx);
        writer.LinkChild(sec1, p1);

        // Code block
        uint code = writer.AddNode(GDocNodeType.CodeBlock, "Console.WriteLine(\"Hello Glacier\");");
        writer.LinkChild(sec1, code);

        byte[] gdocBytes = writer.ToByteArray();
        Assert.NotNull(gdocBytes);
        Assert.True(gdocBytes.Length >= 64);

        using var reader = GDocReader.FromMemory(gdocBytes);
        Assert.Equal(4u, reader.NodeCount);
        Assert.Equal(1u, reader.VectorCount);
        Assert.Equal(4u, reader.VectorDim);

        // Inspect root
        ref readonly var rootNode = ref reader.GetNode(root);
        Assert.Equal("Document Title", reader.GetText(in rootNode));
        Assert.Equal((ushort)GDocNodeType.Document, rootNode.NodeType);

        // Inspect section children
        var secChildren = reader.GetChildren(sec1);
        Assert.Equal(2, secChildren.Count);
        Assert.Equal(p1, secChildren[0]);
        Assert.Equal(code, secChildren[1]);

        // Inspect vector
        ref readonly var p1Node = ref reader.GetNode(p1);
        var readVec = reader.GetVector(in p1Node);
        Assert.Equal(4, readVec.Length);
        Assert.Equal(0.5f, readVec[0]);

        // Breadcrumbs
        var breadcrumbs = reader.GetBreadcrumbs(code);
        Assert.Equal(3, breadcrumbs.Count);
        Assert.Equal(root, breadcrumbs[0]);
        Assert.Equal(sec1, breadcrumbs[1]);
        Assert.Equal(code, breadcrumbs[2]);
    }

    [Fact]
    public void GDoc_FileMemoryMapped_Roundtrip()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_test_{Guid.NewGuid():N}.gdoc");
        try
        {
            var writer = new GDocWriter(vectorDim: 8);
            uint n0 = writer.AddNode(GDocNodeType.Heading1, "Architecture");
            float[] queryVec = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
            float[] targetVec = [0.99f, 0.05f, 0f, 0f, 0f, 0f, 0f, 0f];
            float[] otherVec = [0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f];

            uint v0 = writer.AddVector(targetVec);
            uint v1 = writer.AddVector(otherVec);

            uint matchNode = writer.AddNode(GDocNodeType.Paragraph, "Target Content", vectorIndex: v0);
            uint otherNode = writer.AddNode(GDocNodeType.Paragraph, "Other Content", vectorIndex: v1);

            writer.WriteToFile(tempFile);

            using var reader = GDocReader.OpenFile(tempFile);
            Assert.Equal(3u, reader.NodeCount);

            var (bestNodeId, score) = reader.FindBestMatch(queryVec);
            Assert.Equal(matchNode, bestNodeId);
            Assert.True(score > 0.95f, $"Expected score > 0.95, got {score}");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
