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
    
    public static class DracoEncoder {

        struct AttributeData {
            public int stream;
            public int offset;
        }

        public static unsafe NativeArray<byte>[] EncodeMesh(Mesh unityMesh) {
#if UNITY_2020_1_OR_NEWER
            if (!unityMesh.isReadable) return null;
            
            var mesh = unityMesh;
            
            var result = new NativeArray<byte>[mesh.subMeshCount];
            
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
                
                var dracoMesh = CreateDracoMeshEncoder(faceCount);

                var attributeIds = new Dictionary<VertexAttribute, int>();

                foreach (var pair in attrDatas) {
                    var attribute = pair.Key;
                    var attrData = pair.Value;
                    var format = mesh.GetVertexAttributeFormat(attribute);
                    var dimension = mesh.GetVertexAttributeDimension(attribute);
                    // Debug.Log($"attr {attribute}: {dimension} x {format}");
                    attributeIds[attribute] = DracoMeshAddAttribute(dracoMesh, GetAttributeType(attribute),GetDataType(format),dimension);
                    
                    var baseAddr = vDataPtr[attrData.stream] + attrData.offset;
                    
                    var stride = strides[attrData.stream];
                    
                    for (int faceId = 0; faceId < faceCount; faceId++) {
                        var index0 = indices[faceId * 3];
                        var index1 = indices[faceId * 3 + 1];
                        var index2 = indices[faceId * 3 + 2];
                        var dataPtr0 = baseAddr + stride * index0;
                        var dataPtr1 = baseAddr + stride * index1;
                        var dataPtr2 = baseAddr + stride * index2;
                        DracoMeshAddFaceValues(dracoMesh, faceId, attributeIds[attribute], dimension, dataPtr0, dataPtr1, dataPtr2);
                    }
                }

                DracoMeshCreateEncoder(dracoMesh, out var meshPtr, out var encoderPtr);

                foreach (var pair in attrDatas) {
                    var attribute = pair.Key;
                    if(attribute == VertexAttribute.BlendIndices) continue;
                    DracoMeshSetAttributeQuantization(encoderPtr, attributeIds[attribute], GetDefaultQuantization(attribute));
                }

                DracoMeshFinalize(dracoMesh, encoderPtr, meshPtr, out var dracoBuffer, out var encodedData, out var size);
                result[submeshIndex] = new NativeArray<byte>(size, Allocator.Persistent);
                UnsafeUtility.MemCpy(result[submeshIndex].GetUnsafePtr(), encodedData, size);
                ReleaseDracoMeshBuffer(dracoBuffer);
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

        static int GetDefaultQuantization(VertexAttribute attribute) {
            switch (attribute) {
                case VertexAttribute.Position:
                    return 14;
                case VertexAttribute.Normal:
                case VertexAttribute.Tangent:
                    return 10;
                case VertexAttribute.Color:
                case VertexAttribute.BlendWeight:
                    return 8;
                default:
                    return 12;
            }
        }
        
        [DllImport ("dracoenc_unity")]
        unsafe static extern void* CreateDracoMeshEncoder(int faceCount);
        [DllImport ("dracoenc_unity")]
        unsafe static extern int DracoMeshAddAttribute(void * dracoMesh, AttributeType attributeType, DataType dataType, int numComponents);
        [DllImport ("dracoenc_unity")]
        unsafe static extern void DracoMeshAddFaceValues(void * dracoMesh, int faceIndex, int attributeId, int numComponents, IntPtr data0, IntPtr data1, IntPtr data2);
        [DllImport ("dracoenc_unity")]
        unsafe static extern void DracoMeshCreateEncoder(void * dracoMesh, out void * meshPtr, out void * encoderPtr);
        [DllImport ("dracoenc_unity")]
        unsafe static extern void DracoMeshSetAttributeQuantization(void * encoderPtr, int attributeId, int quantization);
        [DllImport ("dracoenc_unity")]
        unsafe static extern void DracoMeshFinalize(void * dracoMesh, void* encoderPtr, void* meshPtr, out void* dracoBuffer, out void *result, out int size);
        
        [DllImport ("dracoenc_unity")]
        unsafe static extern void ReleaseDracoMeshBuffer(void * dracoBuffer);
    }
}
