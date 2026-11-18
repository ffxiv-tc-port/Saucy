using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
namespace Saucy.TripleTriad.Utils;

public class GUINodeUtils
{
    // 節點位址只在單次走訪內暫放：每段用完就把那段歸零截掉，不留任何一幀讀到的位址給下一次用。
    [ThreadStatic] private static NodeAddrBuffer? addrScratch;

    private static NodeAddrBuffer AddrScratch => addrScratch ??= new();

    public static unsafe bool IsNodeVisible(AtkResNode* node) =>
        node != null && node->IsVisible();

    public static unsafe AtkResNode* PickChildNode(AtkResNode* maybeCompNode, int childIdx, int expectedNumChildren)
    {
        if (maybeCompNode != null && (int)maybeCompNode->Type >= 1000)
        {
            var compNode = (AtkComponentNode*)maybeCompNode;
            if (compNode->Component->UldManager.NodeListCount == expectedNumChildren && childIdx < expectedNumChildren)
            {
                return compNode->Component->UldManager.NodeList[childIdx];
            }
        }

        return null;
    }

    public static unsafe AtkResNode* PickChildNode(AtkComponentBase* compPtr, int childIdx, int expectedNumChildren)
    {
        if (compPtr != null && compPtr->UldManager.NodeListCount == expectedNumChildren && childIdx < expectedNumChildren)
        {
            return compPtr->UldManager.NodeList[childIdx];
        }

        return null;
    }

    public static unsafe AtkResNode*[]? GetImmediateChildNodes(AtkResNode* node)
    {
        using var nodes = BorrowImmediateChildNodes(node);
        return nodes.ToArray();
    }

    public static unsafe AtkResNode*[]? GetAllChildNodes(AtkResNode* node)
    {
        if (node != null)
        {
            using var nodes = BorrowAllChildNodes(node);
            return nodes.ToArray();
        }

        return null;
    }

    /// <summary>Same nodes, same order and same reads as <see cref="GetImmediateChildNodes" />, without the array.</summary>
    /// <remarks>Only valid until disposed; nested borrows must be disposed innermost first (<c>using var</c> does that).</remarks>
    public static unsafe BorrowedNodes BorrowImmediateChildNodes(AtkResNode* node)
    {
        var buffer = AddrScratch;
        var start = buffer.Count;
        if (node != null && node->ChildNode != null)
        {
            buffer.Add((nint)node->ChildNode);

            node = node->ChildNode;
            while (node->PrevSiblingNode != null)
            {
                buffer.Add((nint)node->PrevSiblingNode);
                node = node->PrevSiblingNode;
            }
        }

        return new(buffer, start);
    }

    /// <summary>Same nodes, same order and same reads as <see cref="GetAllChildNodes" />, without the array.</summary>
    public static unsafe BorrowedNodes BorrowAllChildNodes(AtkResNode* node)
    {
        var buffer = AddrScratch;
        var start = buffer.Count;
        RecursiveAppendChildNodes(node, buffer);
        return new(buffer, start);
    }

    private static unsafe void RecursiveAppendChildNodes(AtkResNode* node, NodeAddrBuffer listAddr)
    {
        if (node != null)
        {
            listAddr.Add((nint)node);

            if (node->ChildNode != null)
            {
                RecursiveAppendChildNodes(node->ChildNode, listAddr);

                var linkNode = node->ChildNode;
                while (linkNode->PrevSiblingNode != null)
                {
                    RecursiveAppendChildNodes(linkNode->PrevSiblingNode, listAddr);
                    linkNode = linkNode->PrevSiblingNode;
                }
            }
        }
    }

    public readonly unsafe ref struct BorrowedNodes
    {
        private readonly NodeAddrBuffer buffer;
        private readonly int start;
        private readonly int end;

        internal BorrowedNodes(NodeAddrBuffer buffer, int start)
        {
            this.buffer = buffer;
            this.start = start;
            end = buffer.Count;
        }

        public int Count => end - start;

        public AtkResNode* this[int idx] => (uint)idx < (uint)Count ? (AtkResNode*)buffer.Items[start + idx] : throw new IndexOutOfRangeException();

        public AtkResNode*[]? ToArray()
        {
            if (Count > 0)
            {
                var typedArr = new AtkResNode*[Count];
                for (var idx = 0; idx < typedArr.Length; idx++)
                {
                    typedArr[idx] = (AtkResNode*)buffer.Items[start + idx];
                }

                return typedArr;
            }

            return null;
        }

        public void Dispose() => buffer.Truncate(start);
    }

    internal sealed class NodeAddrBuffer
    {
        public nint[] Items = new nint[64];
        public int Count;

        public void Add(nint addr)
        {
            if (Count == Items.Length)
            {
                Array.Resize(ref Items, Items.Length * 2);
            }

            Items[Count++] = addr;
        }

        public void Truncate(int newCount)
        {
            if (newCount < Count)
            {
                Array.Clear(Items, newCount, Count - newCount);
                Count = newCount;
            }
        }
    }

    public static unsafe AtkResNode* PickNode(AtkResNode*[]? nodes, int nodeIdx, int expectedNumNodes)
    {
        if (nodes != null && nodes.Length == expectedNumNodes && nodeIdx < expectedNumNodes)
        {
            return nodes[nodeIdx];
        }

        return null;
    }

    /// <summary>Same answer as the array overload for the same nodes.</summary>
    public static unsafe AtkResNode* PickNode(in BorrowedNodes nodes, int nodeIdx, int expectedNumNodes)
    {
        if (nodes.Count > 0 && nodes.Count == expectedNumNodes && nodeIdx < expectedNumNodes)
        {
            return nodes[nodeIdx];
        }

        return null;
    }

    public static unsafe AtkResNode* GetChildNode(AtkResNode* node) => node != null ? node->ChildNode : null;

    public static unsafe string? GetNodeTexturePath(AtkResNode* maybeImageNode)
    {
        if (maybeImageNode != null && maybeImageNode->Type == NodeType.Image)
        {
            var imageNode = (AtkImageNode*)maybeImageNode;
            if (imageNode->PartsList != null && imageNode->PartId <= imageNode->PartsList->PartCount)
            {
                var textureInfo = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
                var texType = textureInfo->AtkTexture.TextureType;
                if (texType == TextureType.Resource)
                {
                    var texFileNameStdString = &textureInfo->AtkTexture.Resource->TexFileResourceHandle->ResourceHandle.FileName;
                    var texString = texFileNameStdString->Length < 16
                        ? Marshal.PtrToStringAnsi((nint)texFileNameStdString->Buffer)
                        : Marshal.PtrToStringAnsi((nint)texFileNameStdString->BufferPtr);

                    return texString;
                }
            }
        }

        return null;
    }

    public static unsafe string? GetNodeText(AtkResNode* maybeTextNode)
    {
        if (maybeTextNode != null && maybeTextNode->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)maybeTextNode;
            var text = Marshal.PtrToStringUTF8(new(textNode->NodeText.StringPtr));
            return text;
        }

        return null;
    }

    public static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
        var pos = new Vector2(node->X, node->Y);
        var par = node->ParentNode;
        while (par != null)
        {
            pos *= new Vector2(par->ScaleX, par->ScaleY);
            pos += new Vector2(par->X, par->Y);
            par = par->ParentNode;
        }

        return pos;
    }

    public static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
        if (node == null)
        {
            return new(1, 1);
        }
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        while (node->ParentNode != null)
        {
            node = node->ParentNode;
            scale *= new Vector2(node->ScaleX, node->ScaleY);
        }

        return scale;
    }

    public static unsafe (Vector2, Vector2) GetNodePosAndSize(AtkResNode* node)
    {
        if (node != null)
        {
            var pos = GetNodePosition(node);
            var scale = GetNodeScale(node);
            var size = new Vector2(node->Width * scale.X, node->Height * scale.Y);

            return (pos, size);
        }

        return (Vector2.Zero, Vector2.Zero);
    }
}
