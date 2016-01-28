﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using ILCompiler.DependencyAnalysisFramework;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Internal.TypeSystem;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Object writer using https://github.com/dotnet/llilc
    /// </summary>
    internal class ObjectWriter : IDisposable
    {
        // This is used to look up file id for the given file name.
        // This is a global table across nodes.
        private Dictionary<string, int> _debugFileToId = new Dictionary<string, int>();

        // This is used to look up DebugLocInfo for the given native offset.
        // This is for individual node and should be flushed once node is emitted.
        private Dictionary<int, DebugLocInfo> _offsetToDebugLoc = new Dictionary<int, DebugLocInfo>();

        // Code offset to defined names
        private Dictionary<int, List<string>> _offsetToDefName = new Dictionary<int, List<string>>();

        // Code offset to Cfi blobs
        private Dictionary<int, List<byte[]>> _offsetToCfis = new Dictionary<int, List<byte[]>>();
        // Code offsets that starts a frame
        private HashSet<int> _offsetToCfiStart = new HashSet<int>();
        // Code offsets that ends a frame
        private HashSet<int> _offsetToCfiEnd = new HashSet<int>();
        // Used to assert whether frames are not overlapped.
        private bool _frameOpened;
        public bool IsFrameOpened() { return _frameOpened; }

        // The first defined symbol name for the current object node being processed.
        private string _nodeName;
        public string getNodeName() { return _nodeName; }

        private const string NativeObjectWriterFileName = "objwriter";

        // Target platform ObjectWriter is instantiated for.
        private TargetDetails _targetPlatform;

        // Nodefactory for which ObjectWriter is instantiated for.
        private NodeFactory _nodeFactory;

#if DEBUG
        static HashSet<string> _previouslyWrittenNodeNames = new HashSet<string>();
#endif

        [DllImport(NativeObjectWriterFileName)]
        private static extern IntPtr InitObjWriter(string objectFilePath);

        [DllImport(NativeObjectWriterFileName)]
        private static extern void FinishObjWriter(IntPtr objWriter);

        [DllImport(NativeObjectWriterFileName)]
        private static extern void SwitchSection(IntPtr objWriter, string sectionName);
        public void SwitchSection(string sectionName)
        {
            SwitchSection(_nativeObjectWriter, sectionName);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitAlignment(IntPtr objWriter, int byteAlignment);
        public void EmitAlignment(int byteAlignment)
        {
            EmitAlignment(_nativeObjectWriter, byteAlignment);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitBlob(IntPtr objWriter, int blobSize, byte[] blob);
        public void EmitBlob(int blobSize, byte[] blob)
        {
            EmitBlob(_nativeObjectWriter, blobSize, blob);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitIntValue(IntPtr objWriter, ulong value, int size);
        public void EmitIntValue(ulong value, int size)
        {
            EmitIntValue(_nativeObjectWriter, value, size);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitSymbolDef(IntPtr objWriter, string symbolName);
        public void EmitSymbolDef(string symbolName)
        {
            EmitSymbolDef(_nativeObjectWriter, symbolName);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitSymbolRef(IntPtr objWriter, string symbolName, int size, bool isPCRelative, int delta = 0);
        public void EmitSymbolRef(string symbolName, int size, bool isPCRelative, int delta = 0)
        {
            EmitSymbolRef(_nativeObjectWriter, symbolName, size, isPCRelative, delta);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitWinFrameInfo(IntPtr objWriter, string methodName, int startOffset, int endOffset, int blobSize, byte[] blobData,
                                                 string personalityFunctionName, int LSDASize, byte[] LSDA);
        public void EmitWinFrameInfo(int startOffset, int endOffset, int blobSize, byte[] blobData,
                                  string personalityFunctionName = null, int LSDASize = 0, byte[] LSDA = null)
        {
            EmitWinFrameInfo(_nativeObjectWriter, _nodeName, startOffset, endOffset, blobSize, blobData,
                          personalityFunctionName, LSDASize, LSDA);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitCFIStart(IntPtr objWriter, int nativeOffset);
        public void EmitCFIStart(int nativeOffset)
        {
            Debug.Assert(!_frameOpened);
            EmitCFIStart(_nativeObjectWriter, nativeOffset);
            _frameOpened = true;
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitCFIEnd(IntPtr objWriter, int nativeOffset);
        public void EmitCFIEnd(int nativeOffset)
        {
            Debug.Assert(_frameOpened);
            EmitCFIEnd(_nativeObjectWriter, nativeOffset);
            _frameOpened = false;
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitCFIBlob(IntPtr objWriter, int nativeOffset, byte[] blob);
        public void EmitCFIBlob(int nativeOffset, byte[] blob)
        {
            Debug.Assert(_frameOpened);
            EmitCFIBlob(_nativeObjectWriter, nativeOffset, blob);
        }


        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitDebugFileInfo(IntPtr objWriter, int fileInfoSize, string[] fileInfos);
        public void EmitDebugFileInfo(int fileInfoSize, string[] fileInfos)
        {
            EmitDebugFileInfo(_nativeObjectWriter, fileInfoSize, fileInfos);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void EmitDebugLoc(IntPtr objWriter, int nativeOffset, int fileId, int linueNumber, int colNumber);
        public void EmitDebugLoc(int nativeOffset, int fileId, int linueNumber, int colNumber)
        {
            EmitDebugLoc(_nativeObjectWriter, nativeOffset, fileId, linueNumber, colNumber);
        }

        [DllImport(NativeObjectWriterFileName)]
        private static extern void FlushDebugLocs(IntPtr objWriter, string methodName, int methodSize);
        public void FlushDebugLocs(int methodSize)
        {
            // No interest if there is no debug location emission/map before.
            if (_offsetToDebugLoc.Count == 0)
            {
                return;
            }
            FlushDebugLocs(_nativeObjectWriter, _nodeName, methodSize);

            // Ensure clean up the map for the next node.
            _offsetToDebugLoc.Clear();
        }

        public string[] BuildFileInfoMap(IEnumerable<DependencyNode> nodes)
        {
            // TODO: DebugInfo on Unix https://github.com/dotnet/corert/issues/608
            if (_targetPlatform.OperatingSystem != TargetOS.Windows)
                return null;

            ArrayBuilder<string> debugFileInfos = new ArrayBuilder<string>();
            foreach (DependencyNode node in nodes)
            {
                if (node is INodeWithDebugInfo)
                {
                    DebugLocInfo[] debugLocInfos = ((INodeWithDebugInfo)node).DebugLocInfos;
                    if (debugLocInfos != null)
                    {
                        foreach (DebugLocInfo debugLocInfo in debugLocInfos)
                        {
                            string fileName = debugLocInfo.FileName;
                            if (!_debugFileToId.ContainsKey(fileName))
                            {
                                _debugFileToId.Add(fileName, debugFileInfos.Count);
                                debugFileInfos.Add(fileName);
                            }
                        }
                    }
                }
            }

            return debugFileInfos.Count > 0 ? debugFileInfos.ToArray() : null;
        }

        public void BuildDebugLocInfoMap(ObjectNode node)
        {
            // No interest if file map is no built before.
            if (_debugFileToId.Count == 0)
            {
                return;
            }

            // No interest if it's not a debug node.
            if (!(node is INodeWithDebugInfo))
            {
                return;
            }

            DebugLocInfo[] locs = (node as INodeWithDebugInfo).DebugLocInfos;
            if (locs != null)
            {
                foreach (var loc in locs)
                {
                    _offsetToDebugLoc.Add(loc.NativeOffset, loc);
                }
            }
        }

        public void BuildCFIMap(ObjectNode node)
        {
            _offsetToCfis.Clear();
            _offsetToCfiStart.Clear();
            _offsetToCfiEnd.Clear();
            _frameOpened = false;

            if (!(node is INodeWithFrameInfo))
            {
                return;
            }

            FrameInfo[] frameInfos = ((INodeWithFrameInfo)node).FrameInfos;
            if (frameInfos == null)
            {
                return;
            }

            foreach (var frameInfo in frameInfos)
            {
                int start = frameInfo.StartOffset;
                int end = frameInfo.EndOffset;
                int len = frameInfo.BlobData.Length;
                byte[] blob = frameInfo.BlobData;

                if (_targetPlatform.OperatingSystem == TargetOS.Windows)
                {
                    // For window, just emit the frame blob (UNWIND_INFO) as a whole.
                    EmitWinFrameInfo(start, end, len, blob);
                }
                else
                {
                    // For Unix, we build CFI blob map for each offset.
                    Debug.Assert(len % FrameInfo.CfiBlobSize == 0);

                    // Record start/end of frames which shouldn't be overlapped.
                    _offsetToCfiStart.Add(start);
                    _offsetToCfiEnd.Add(end);
                    for (int j = 0; j < len; j += FrameInfo.CfiBlobSize)
                    {
                        // The first byte of Cfi Blob is offset from the range the frame covers.
                        // Compute code offset from the root method.
                        int codeOffset = blob[j] + start;
                        List<byte[]> cfis;
                        if (!_offsetToCfis.TryGetValue(codeOffset, out cfis))
                        {
                            cfis = new List<byte[]>();
                            _offsetToCfis.Add(codeOffset, cfis);
                        }
                        byte[] cfi = new byte[FrameInfo.CfiBlobSize];
                        Array.Copy(blob, j, cfi, 0, FrameInfo.CfiBlobSize);
                        cfis.Add(cfi);
                    }
                }
            }
        }

        public void EmitCFIBlobs(int offset)
        {
            // Emit end the old frame before start a frame.
            if (_offsetToCfiEnd.Contains(offset))
            {
                EmitCFIEnd(offset);
            }

            if (_offsetToCfiStart.Contains(offset))
            {
                EmitCFIStart(offset);
            }

            // Emit individual cfi blob for the given offset
            List<byte[]> cfis;
            if (_offsetToCfis.TryGetValue(offset, out cfis))
            {
                foreach(byte[] cfi in cfis)
                {
                    EmitCFIBlob(offset, cfi);
                }
            }
        }

        public void EmitDebugLocInfo(int offset)
        {
            DebugLocInfo loc;
            if (_offsetToDebugLoc.TryGetValue(offset, out loc))
            {
                Debug.Assert(_debugFileToId.Count > 0);
                EmitDebugLoc(offset,
                    _debugFileToId[loc.FileName],
                    loc.LineNumber,
                    loc.ColNumber);
            }
        }

        public void BuildSymbolDefinitionMap(ISymbolNode[] definedSymbols)
        {
            _offsetToDefName.Clear();
            foreach (ISymbolNode n in definedSymbols)
            {
                if (!_offsetToDefName.ContainsKey(n.Offset))
                {
                    _offsetToDefName[n.Offset] = new List<string>();
                }

                string symbolToEmit = GetSymbolToEmitForTargetPlatform(n.MangledName);
                _offsetToDefName[n.Offset].Add(symbolToEmit);

                string alternateName = _nodeFactory.GetSymbolAlternateName(n);
                if (alternateName != null)
                {
                    symbolToEmit = GetSymbolToEmitForTargetPlatform(alternateName);
                    _offsetToDefName[n.Offset].Add(symbolToEmit);
                }
            }

            // First entry is the node (entry point) name.
            _nodeName = _offsetToDefName[0][0];
        }

        private string GetSymbolToEmitForTargetPlatform(string symbol)
        {
            string symbolToEmit = symbol;

            if (_targetPlatform.OperatingSystem == TargetOS.OSX)
            {
                // On OSX, we need to prefix an extra underscore to account for correct linkage of 
                // extern "C" functions.
                symbolToEmit = "_"+symbol;
            }

            return symbolToEmit;
        }

        public void EmitSymbolDefinition(int currentOffset)
        {
            List<string> nodes;
            if (_offsetToDefName.TryGetValue(currentOffset, out nodes))
            {
                foreach (var name in nodes)
                {
                    EmitSymbolDef(name);
                }
            }
        }

        private IntPtr _nativeObjectWriter = IntPtr.Zero;

        public ObjectWriter(string objectFilePath, NodeFactory factory)
        {
            _nativeObjectWriter = InitObjWriter(objectFilePath);
            if (_nativeObjectWriter == IntPtr.Zero)
            {
                throw new IOException("Fail to initialize Native Object Writer");
            }

            _nodeFactory = factory;
            _targetPlatform = _nodeFactory.Target;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual void Dispose(bool bDisposing)
        {
            if (_nativeObjectWriter != null)
            {
                // Finalize object emission.
                FinishObjWriter(_nativeObjectWriter);
                _nativeObjectWriter = IntPtr.Zero;
            }

            _nodeFactory = null;

            if (bDisposing)
            {
                GC.SuppressFinalize(this);
            }
        }

        ~ObjectWriter()
        {
            Dispose(false);
        }

        public static void EmitObject(string objectFilePath, IEnumerable<DependencyNode> nodes, NodeFactory factory)
        {
            using (ObjectWriter objectWriter = new ObjectWriter(objectFilePath, factory))
            {
                string currentSection = "";

                // Build file info map.
                string[] debugFileInfos = objectWriter.BuildFileInfoMap(nodes);
                if (debugFileInfos != null)
                {
                    objectWriter.EmitDebugFileInfo(debugFileInfos.Length, debugFileInfos);
                }

                foreach (DependencyNode depNode in nodes)
                {
                    ObjectNode node = depNode as ObjectNode;
                    if (node == null)
                        continue;

                    if (node.ShouldSkipEmittingObjectNode(factory))
                        continue;

#if DEBUG
                    Debug.Assert(_previouslyWrittenNodeNames.Add(node.GetName()), "Duplicate node name emitted to file", "Node {0} has already been written to the output object file {1}", node.GetName(), objectFilePath);
#endif
                    ObjectNode.ObjectData nodeContents = node.GetData(factory);

                    if (currentSection != node.Section)
                    {
                        currentSection = node.Section;
                        objectWriter.SwitchSection(currentSection);
                    }

                    objectWriter.EmitAlignment(nodeContents.Alignment);

                    Relocation[] relocs = nodeContents.Relocs;
                    int nextRelocOffset = -1;
                    int nextRelocIndex = -1;
                    if (relocs != null && relocs.Length > 0)
                    {
                        nextRelocOffset = relocs[0].Offset;
                        nextRelocIndex = 0;
                    }

                    // Build symbol definition map.
                    objectWriter.BuildSymbolDefinitionMap(nodeContents.DefinedSymbols);

                    // Build CFI map (Unix) or publish unwind blob (Windows).
                    objectWriter.BuildCFIMap(node);

                    // Build debug location map
                    objectWriter.BuildDebugLocInfoMap(node);

                    for (int i = 0; i < nodeContents.Data.Length; i++)
                    {
                        // Emit symbol definitions if necessary
                        objectWriter.EmitSymbolDefinition(i);

                        // Emit CFI blobs for the given offset.
                        objectWriter.EmitCFIBlobs(i);

                        // Emit debug loc info if needed.
                        objectWriter.EmitDebugLocInfo(i);

                        if (i == nextRelocOffset)
                        {
                            Relocation reloc = relocs[nextRelocIndex];

                            ISymbolNode target = reloc.Target;
                            string targetName = objectWriter.GetSymbolToEmitForTargetPlatform(target.MangledName);
                            int size = 0;
                            bool isPCRelative = false;
                            switch (reloc.RelocType)
                            {
                                case RelocType.IMAGE_REL_BASED_DIR64:
                                    size = 8;
                                    break;
                                case RelocType.IMAGE_REL_BASED_REL32:
                                    size = 4;
                                    isPCRelative = true;
                                    break;
                                default:
                                    throw new NotImplementedException();
                            }
                            // Emit symbol reference
                            objectWriter.EmitSymbolRef(targetName, size, isPCRelative, reloc.Delta);

                            // Update nextRelocIndex/Offset
                            if (++nextRelocIndex < relocs.Length)
                            {
                                nextRelocOffset = relocs[nextRelocIndex].Offset;
                            }
                            i += size - 1;
                            continue;
                        }

                        objectWriter.EmitIntValue(nodeContents.Data[i], 1);
                    }

                    // It is possible to have a symbol just after all of the data.
                    objectWriter.EmitSymbolDefinition(nodeContents.Data.Length);

                    // Emit the last frame loc info to close the frame.
                    objectWriter.EmitCFIBlobs(nodeContents.Data.Length);

                    objectWriter.FlushDebugLocs(nodeContents.Data.Length);
                    objectWriter.SwitchSection(currentSection);
                }
            }
        }
    }
}
