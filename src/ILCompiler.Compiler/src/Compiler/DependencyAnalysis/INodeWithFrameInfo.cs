// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace ILCompiler.DependencyAnalysis
{
    public class FrameInfo
    {
        public int StartOffset;
        public int EndOffset;
        public byte[] BlobData;

        // The size of CFI_CODE blob that RyuJit passes.
        public const int CfiBlobSize = 8;
    }

    public interface INodeWithFrameInfo
    {
        FrameInfo[] FrameInfos
        {
            get;
        }
    }
}
