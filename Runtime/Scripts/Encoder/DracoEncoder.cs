// Copyright 2021 The Draco Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Draco.Encoder {

    public struct EncodeResult {
        public uint indexCount;
        public uint vertexCount;
        public NativeArray<byte> data;

        public void Dispose() {
            data.Dispose();
        }
    }
    
    public static class DracoEncoder {
        
#if UNITY_EDITOR_OSX || UNITY_WEBGL || UNITY_IOS
        const string DRACOENC_UNITY_LIB = "__Internal";
#elif UNITY_ANDROID || UNITY_STANDALONE || UNITY_WSA || UNITY_EDITOR || PLATFORM_LUMIN
        const string DRACOENC_UNITY_LIB = "dracoenc_unity";
#endif

        struct AttributeData {
            public int stream;
            public int offset;
        }

        /// <summary>
        /// Calculates the idea quantization value based on the largest dimension and desired precision
        /// </summary>
        /// <param name="largestDimension">Length of the largest dimension (width/depth/height)</param>
        /// <param name="precision">Desired minimum precision in world units</param>
        /// <returns></returns>
        static int GetIdealQuantization(float largestDimension, float precision) {
            var value = Mathf.RoundToInt(largestDimension / precision);
            var mostSignificantBit = -1;
            while (value > 0) {
                mostSignificantBit++;
                value >>= 1;
            }
            return Mathf.Clamp(mostSignificantBit,4,24);
        }

        /// <summary>
        /// Applies Draco compression to a given mesh and returns the encoded result (one per submesh)
        /// The quality and quantization parameters are calculated from the mesh's bounds, its worldScale and desired precision.
        /// The quantization paramters help to find a balance between compressed size and quality / precision.
        /// </summary>
        /// <param name="unityMesh">Input mesh</param>
        /// <param name="worldScale">Local-to-world scale this mesh is present in the scene</param>
        /// <param name="precision">Desired minimum precision in world units</param>
        /// <param name="encodingSpeed">Encoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="decodingSpeed">Decoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="normalQuantization">Normal quantization</param>
        /// <param name="texCoordQuantization">Texture coordinate quantization</param>
        /// <param name="colorQuantization">Color quantization</param>
        /// <param name="genericQuantization">Generic quantization (e.g. blend weights and indices). unused at the moment</param>
        /// <returns></returns>
        public static unsafe EncodeResult[] EncodeMesh(
            Mesh unityMesh,
            Vector3 worldScale,
            float precision = .001f,
            int encodingSpeed = 0,
            int decodingSpeed = 4,
            int normalQuantization = 10,
            int texCoordQuantization = 12,
            int colorQuantization = 8,
            int genericQuantization = 12
            )
        {

            if (!unityMesh.isReadable) {
                Debug.LogError("Mesh is not readable");
                return null;
            }
            
            var bounds = unityMesh.bounds;
            var scale = new Vector3(Mathf.Abs(worldScale.x), Mathf.Abs(worldScale.y), Mathf.Abs(worldScale.z));
            var maxSize = Mathf.Max(bounds.extents.x*scale.x, bounds.extents.y*scale.y, bounds.extents.z*scale.z) * 2;
            var positionQuantization = GetIdealQuantization(maxSize, precision);

            return EncodeMesh(
                unityMesh,
                encodingSpeed,
                decodingSpeed,
                positionQuantization,
                normalQuantization,
                texCoordQuantization,
                colorQuantization,
                genericQuantization
                );
        }
        
        /// <summary>
        /// Applies Draco compression to a given mesh and returns the encoded result (one per submesh)
        /// The quantization paramters help to find a balance between encoded size and quality / precision.
        /// </summary>
        /// <param name="unityMesh">Input mesh</param>
        /// <param name="encodingSpeed">Encoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="decodingSpeed">Decoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="positionQuantization">Vertex position quantization</param>
        /// <param name="normalQuantization">Normal quantization</param>
        /// <param name="texCoordQuantization">Texture coordinate quantization</param>
        /// <param name="colorQuantization">Color quantization</param>
        /// <param name="genericQuantization">Generic quantization (e.g. blend weights and indices). unused at the moment</param>
        /// <returns></returns>
        public static unsafe EncodeResult[] EncodeMesh(
            Mesh unityMesh,
            int encodingSpeed = 0,
            int decodingSpeed = 4,
            int positionQuantization = 14,
            int normalQuantization = 10,
            int texCoordQuantization = 12,
            int colorQuantization = 8,
            int genericQuantization = 12
            )
        {
#if UNITY_2020_1_OR_NEWER
            if (!unityMesh.isReadable) {
                Debug.LogError("Mesh is not readable");
                return null;
            }
            
            var mesh = unityMesh;
            var result = new EncodeResult[mesh.subMeshCount];
            var vertexAttributes = mesh.GetVertexAttributes();

            var strides = new int[DracoNative.maxStreamCount];
            var attrDatas = new Dictionary<VertexAttribute, AttributeData>();
            
            foreach (var attribute in vertexAttributes) {
                var attrData = new AttributeData { offset = strides[attribute.stream], stream = attribute.stream };
                var size = attribute.dimension * GetAttributeSize(attribute.format);
                strides[attribute.stream] += size;
                attrDatas[attribute.attribute] = attrData;
            }

            var streamCount = 1;
            for (var stream = 0; stream < strides.Length; stream++) {
                var stride = strides[stream];
                if(stride<=0) continue;
                streamCount = stream + 1;
            }

            var dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var data = dataArray[0];
            
            var vData = new NativeArray<byte>[streamCount];
            var vDataPtr = new IntPtr[streamCount];
            for (var stream = 0; stream < streamCount; stream++) {
                vData[stream] = data.GetVertexData<byte>(stream);
                vDataPtr[stream] = (IntPtr) vData[stream].GetUnsafeReadOnlyPtr();
            }
            
            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++) {

                var submesh = mesh.GetSubMesh(submeshIndex);
                
                if (submesh.topology != MeshTopology.Triangles) {
                    Debug.LogError("Only triangles are supported");
                    return null;
                }
                var indices = mesh.GetIndices(submeshIndex);
                var faceCount = indices.Length / 3;
                
                var dracoEncoder = dracoEncoderCreate(mesh.vertexCount);

                var attributeIds = new Dictionary<VertexAttribute, uint>();

                foreach (var pair in attrDatas) {
                    var attribute = pair.Key;
                    var attrData = pair.Value;
                    var format = mesh.GetVertexAttributeFormat(attribute);
                    var dimension = mesh.GetVertexAttributeDimension(attribute);
                    var stride = strides[attrData.stream];
                    var baseAddr = vDataPtr[attrData.stream] + attrData.offset;
                    attributeIds[attribute] = dracoEncoderSetAttribute(dracoEncoder, (int) GetAttributeType(attribute), GetDataType(format), dimension, stride, baseAddr);
                }

                var indicesData = (IntPtr) UnsafeUtility.PinGCArrayAndGetDataAddress(indices, out var gcHandle);
                dracoEncoderSetIndices(dracoEncoder, DataType.DT_UINT32, (uint) indices.Length, indicesData);
                UnsafeUtility.ReleaseGCObject(gcHandle);

                // For both encoding and decoding (0 = slow and best compression; 10 = fast) 
                dracoEncoderSetCompressionSpeed(dracoEncoder, Mathf.Clamp(encodingSpeed,0,10), Mathf.Clamp(decodingSpeed,0,10));
                dracoEncoderSetQuantizationBits(
                    dracoEncoder,
                    Mathf.Clamp(positionQuantization,4,24),
                    Mathf.Clamp(normalQuantization,4,24),
                    Mathf.Clamp(texCoordQuantization,4,24),
                    Mathf.Clamp(colorQuantization,4,24),
                    Mathf.Clamp(genericQuantization,4,24)
                );

                dracoEncoderEncode(dracoEncoder, false);
                
                var dracoDataSize = (int) dracoEncoderGetByteLength(dracoEncoder);
                
                var dracoData = new NativeArray<byte>(dracoDataSize, Allocator.Persistent);
                dracoEncoderCopy(dracoEncoder,dracoData.GetUnsafePtr());
                
                result[submeshIndex] = new EncodeResult {
                    indexCount = dracoEncoderGetEncodedIndexCount(dracoEncoder),
                    vertexCount = dracoEncoderGetEncodedVertexCount(dracoEncoder),
                    data = dracoData
                };
                
                dracoEncoderRelease(dracoEncoder);
            }
            
            for (var stream = 0; stream < streamCount; stream++) {
                vData[stream].Dispose();
            }
            dataArray.Dispose();
            
            return result;
#else
            Debug.LogError("Draco Encoding only works on Unity 2020.1 or newer");
            return null;
#endif
        }

        static DataType GetDataType(VertexAttributeFormat format) {
            switch (format) {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.Float16:
                    return DataType.DT_FLOAT32;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.UInt8:
                    return DataType.DT_UINT8;
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.SInt8:
                    return DataType.DT_INT8;
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.UNorm16:
                    return DataType.DT_UINT16;
                case VertexAttributeFormat.SInt16:
                case VertexAttributeFormat.SNorm16:
                    return DataType.DT_INT16;
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return DataType.DT_INT32;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        static AttributeType GetAttributeType(VertexAttribute attribute) {
            switch (attribute) {
                case VertexAttribute.Position:
                    return AttributeType.POSITION;
                case VertexAttribute.Normal:
                    return AttributeType.NORMAL;
                case VertexAttribute.Color:
                    return AttributeType.COLOR;
                case VertexAttribute.TexCoord0:
                case VertexAttribute.TexCoord1:
                case VertexAttribute.TexCoord2:
                case VertexAttribute.TexCoord3:
                case VertexAttribute.TexCoord4:
                case VertexAttribute.TexCoord5:
                case VertexAttribute.TexCoord6:
                case VertexAttribute.TexCoord7:
                    return AttributeType.TEX_COORD;
                case VertexAttribute.Tangent:
                case VertexAttribute.BlendWeight:
                case VertexAttribute.BlendIndices:
                    return AttributeType.GENERIC;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null);
            }
        }

        static unsafe int GetAttributeSize(VertexAttributeFormat format) {
            switch (format) {
                case VertexAttributeFormat.Float32:
                    return sizeof(float);
                case VertexAttributeFormat.Float16:
                    return sizeof(half);
                case VertexAttributeFormat.UNorm8:
                    return sizeof(byte);
                case VertexAttributeFormat.SNorm8:
                    return sizeof(sbyte);
                case VertexAttributeFormat.UNorm16:
                    return sizeof(ushort);
                case VertexAttributeFormat.SNorm16:
                    return sizeof(short);
                case VertexAttributeFormat.UInt8:
                    return sizeof(byte);
                case VertexAttributeFormat.SInt8:
                    return sizeof(sbyte);
                case VertexAttributeFormat.UInt16:
                    return sizeof(ushort);
                case VertexAttributeFormat.SInt16:
                    return sizeof(short);
                case VertexAttributeFormat.UInt32:
                    return sizeof(uint);
                case VertexAttributeFormat.SInt32:
                    return sizeof(int);
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern IntPtr dracoEncoderCreate(int vertexCount);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern void dracoEncoderRelease(IntPtr encoder);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern void dracoEncoderSetCompressionSpeed(IntPtr encoder, int encodingSpeed, int decodingSpeed);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern void dracoEncoderSetQuantizationBits(IntPtr encoder, int position, int normal, int uv, int color, int generic);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern bool dracoEncoderEncode(IntPtr encoder, bool preserveTriangleOrder);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern uint dracoEncoderGetEncodedVertexCount(IntPtr encoder);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern uint dracoEncoderGetEncodedIndexCount(IntPtr encoder);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern ulong dracoEncoderGetByteLength(IntPtr encoder);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern unsafe void dracoEncoderCopy(IntPtr encoder, void *data);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern unsafe bool dracoEncoderSetIndices(IntPtr encoder, DataType indexComponentType, uint indexCount, IntPtr indices);
        
        [DllImport (DRACOENC_UNITY_LIB)]
        static extern uint dracoEncoderSetAttribute(IntPtr encoder, int attributeType, DataType dracoDataType, int componentCount, int stride, IntPtr data);
    }
}
