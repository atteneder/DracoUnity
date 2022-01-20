// Copyright 2017 The Draco Authors.
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

#if UNITY_2020_2_OR_NEWER
#define DRACO_MESH_DATA
#endif

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("DracoEncoder")]

namespace Draco {
    
    // These values must be exactly the same as the values in draco_types.h.
    // Attribute data type.
    enum DataType {
        DT_INVALID = 0,
        DT_INT8,
        DT_UINT8,
        DT_INT16,
        DT_UINT16,
        DT_INT32,
        DT_UINT32,
        DT_INT64,
        DT_UINT64,
        DT_FLOAT32,
        DT_FLOAT64,
        DT_BOOL
    }

    // These values must be exactly the same as the values in
    // geometry_attribute.h.
    // Attribute type.
    enum AttributeType {
        INVALID = -1,
        POSITION = 0,
        NORMAL,
        COLOR,
        TEX_COORD,
        // A special id used to mark attributes that are not assigned to any known
        // predefined use case. Such attributes are often used for a shader specific
        // data.
        GENERIC
    }
    
    [BurstCompile]
    unsafe internal class DracoNative {
        
#if !UNITY_EDITOR && (UNITY_WEBGL || UNITY_IOS)
        const string DRACODEC_UNITY_LIB = "__Internal";
#elif UNITY_ANDROID || UNITY_STANDALONE || UNITY_WSA || UNITY_EDITOR || PLATFORM_LUMIN
        const string DRACODEC_UNITY_LIB = "dracodec_unity";
#endif
        
        public const int maxStreamCount = 4;
        
        /// <summary>
        /// If Draco mesh has more vertices than this value, memory is allocated persistent,
        /// which is slower, but safe when spanning multiple frames.
        /// </summary>
        const int persistentDataThreshold = 5_000;
        
        const int meshPtrIndex = 0;
        const int decoderPtrIndex = 1;
        const int bufferPtrIndex = 2;

        // Cached function pointers
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueInt8Method;
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueUInt8Method;
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueInt16Method;
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueUInt16Method;
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueInt32Method;
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueUInt32Method;
        
        /// <summary>
        /// If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).
        /// </summary>
        bool convertSpace;

        List<AttributeMapBase> attributes;
        int[] streamStrides;
        int[] streamMemberCount;

        Allocator allocator;
        NativeArray<int> dracoDecodeResult;
        NativeArray<IntPtr> dracoTempResources;

        bool isPointCloud;

#if DRACO_MESH_DATA
        Mesh.MeshData mesh;
        int indicesCount;
        
#else
        Mesh mesh;
        int streamCount;
        NativeIndexBufferBase indices;
        NativeArray<byte>[] vData;
        byte*[] vDataPtr;
#endif

#region BlendHack
        // TODO: BLENDHACK; Unity does not support setting bone weights and indices via new Mesh API
        // https://fogbugz.unity3d.com/default.asp?1320869_7g7qeq40va98n6h6
        // As a workaround we extract those attributes separately so they can be fed into
        // Mesh.SetBoneWeights after the Mesh was created.
        AttributeMap boneIndexMap;
        AttributeMap boneWeightMap;
        public bool hasBoneWeightData => boneIndexMap!=null && boneWeightMap!=null;
        public NativeArray<byte> bonesPerVertex;
        public NativeArray<BoneWeight1> boneWeights;
#endregion BlendHack

        public DracoNative(
#if DRACO_MESH_DATA
            Mesh.MeshData mesh,
#endif
            bool convertSpace = true
            )
        {
            this.convertSpace = convertSpace;
#if DRACO_MESH_DATA
            this.mesh = mesh;
#endif
        }

        public JobHandle Init(IntPtr encodedData, int size) {
            var decodeJob = CreateDecodeJob(encodedData, size);
            return decodeJob.Schedule();
        }

#if UNITY_EDITOR
        public void InitSync(IntPtr encodedData, int size) {
            var decodeJob = CreateDecodeJob(encodedData, size);
            decodeJob.Run();
        }
#endif

        DecodeJob CreateDecodeJob(IntPtr encodedData, int size) {
            dracoDecodeResult = new NativeArray<int>(1, Allocator.Persistent);
            dracoTempResources = new NativeArray<IntPtr>(3, Allocator.Persistent);
            var decodeJob = new DecodeJob() {
                encodedData = (byte*)encodedData,
                size = size,
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources
            };
            return decodeJob;
        }

        public bool ErrorOccured() {
            return dracoDecodeResult[0] < 0;
        }
        
        void CalculateVertexParams(
            DracoMesh* dracoMesh,
            bool requireNormals,
            bool requireTangents,
            int weightsAttributeId,
            int jointsAttributeId,
            out bool calculateNormals,
            bool forceUnityLayout = false
            )
        {
            Profiler.BeginSample("CalculateVertexParams");
            attributes = new List<AttributeMapBase>();
            var attributeTypes = new HashSet<VertexAttribute>();
            
            bool CreateAttributeMaps(AttributeType attributeType, int count, DracoMesh* draco, bool normalized = false) {
                bool foundAttribute = false;
                for (var i = 0; i < count; i++) {
                    var type = GetVertexAttribute(attributeType, i);
                    if (!type.HasValue) {
#if UNITY_EDITOR

                        // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
                        Debug.LogWarning($"Unknown attribute {attributeType}!");
#endif
                        continue;
                    }
                    if (attributeTypes.Contains(type.Value)) {
#if UNITY_EDITOR
                        // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
                        Debug.LogWarning($"Multiple {type.Value} attributes!");
#endif
                        continue;
                    }

                    DracoAttribute* attribute = null;
                    if (GetAttributeByType(draco, attributeType, i, &attribute)) {
                        var format = GetVertexAttributeFormat((DataType)attribute->dataType, normalized);
                        if (!format.HasValue) { continue; }
                        var map = new AttributeMap(attribute, type.Value, format.Value, convertSpace && ConvertSpace(type.Value));
                        attributes.Add(map);
                        attributeTypes.Add(type.Value);
                        foundAttribute = true;
                    }
                    else {
                        // attributeType was not found
                        break;
                    }
                }
                return foundAttribute;
            }
            
            bool CreateAttributeMapById(VertexAttribute type, int id, DracoMesh* draco, out AttributeMap map, bool normalized = false) {
                map = null;
                if (attributeTypes.Contains(type)) {
#if UNITY_EDITOR
                    // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
                    Debug.LogWarning($"Multiple {type} attributes!");
#endif
                    return false;
                }

                DracoAttribute* attribute;
                if (GetAttributeByUniqueId(draco, id, &attribute)) {
                    var format = GetVertexAttributeFormat((DataType)attribute->dataType, normalized);
                    if (!format.HasValue) { return false; }

                    map = new AttributeMap(attribute, type, format.Value, convertSpace && ConvertSpace(type));
                    attributeTypes.Add(type);
                    return true;
                }
                return false;
            }

            // Vertex attributes are added in the order defined here:
            // https://docs.unity3d.com/2020.1/Documentation/ScriptReference/Rendering.VertexAttributeDescriptor.html
            //
            CreateAttributeMaps(AttributeType.POSITION, 1, dracoMesh);
            var hasNormals = CreateAttributeMaps(AttributeType.NORMAL, 1, dracoMesh, true);
            calculateNormals = !hasNormals && requireNormals;
            if (calculateNormals) {
                calculateNormals = true;
                attributes.Add(new CalculatedAttributeMap(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 4 ));
            }
            if (requireTangents) {
                attributes.Add(new CalculatedAttributeMap(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, 4 ));
            }
            var hasTexCoordOrColor = CreateAttributeMaps(AttributeType.COLOR, 1, dracoMesh, true);
            hasTexCoordOrColor |= CreateAttributeMaps(AttributeType.TEX_COORD, 8, dracoMesh, true);

            var hasSkinning = false;
            if (weightsAttributeId >= 0) {
                if (CreateAttributeMapById(VertexAttribute.BlendWeight, weightsAttributeId, dracoMesh, out var map, true)) {
                    // BLENDHACK: Don't add bone weights, as they won't exist after Mesh.SetBoneWeights
                    // attributes.Add(map);
                    boneWeightMap = map;
                    hasSkinning = true;
                }
            }
            if (jointsAttributeId >= 0) {
                if (CreateAttributeMapById(VertexAttribute.BlendIndices, jointsAttributeId, dracoMesh, out var map)) {
                    attributes.Add(map);
                    boneIndexMap = map;
                    hasSkinning = true;
                }
            }
            
            streamStrides = new int[maxStreamCount];
            streamMemberCount = new int[maxStreamCount];
            var streamIndex = 0;
            
            // skinning requires SkinnedMeshRenderer layout
            forceUnityLayout |= hasSkinning;
            
            // On scenes with lots of small meshes the overhead of lots
            // of dedicated vertex buffers can have severe negative impact
            // on performance. Therefore we stick to Unity's layout (which
            // combines pos+normal+tangent in one stream) for smaller meshes.
            // See: https://github.com/atteneder/glTFast/issues/197
            forceUnityLayout |= dracoMesh->numVertices <= ushort.MaxValue;

            foreach (var attributeMap in attributes) {
                // Stream assignment:
                // Positions get a dedicated stream (0)
                // The rest lands on stream 1
                
                // If blend weights or blend indices are present, they land on stream 1
                // while the rest is combined in stream 0

                // Mesh layout SkinnedMeshRenderer (used for skinning and blend shapes)
                // requires:
                // stream 0: position,normal,tangent
                // stream 1: UVs,colors
                // stream 2: blend weights/indices

                switch (attributeMap.attribute) {
                    case VertexAttribute.Position:
                        // Attributes that define/change the position go to stream 0
                        streamIndex = 0;
                        break;
                    case VertexAttribute.Normal:
                    case VertexAttribute.Tangent:
                        streamIndex = forceUnityLayout ? 0 : 1;
                        break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                    case VertexAttribute.Color:
                        streamIndex = 1;
                        break;
                    case VertexAttribute.BlendWeight:
                    case VertexAttribute.BlendIndices:
                        // Special case: blend weights/joints always have a special stream
                        streamIndex = hasTexCoordOrColor ? 2 : 1;
                        break;
                }
#if !DRACO_MESH_DATA
                streamCount = Mathf.Max(streamCount, streamIndex+1);
#endif
                var elementSize = attributeMap.elementSize;
                attributeMap.offset = streamStrides[streamIndex];
                attributeMap.stream = streamIndex;
                streamStrides[streamIndex] += elementSize;
                streamMemberCount[streamIndex]++;
            }
            attributes.Sort();
            Profiler.EndSample(); // CalculateVertexParams
        }

#if !DRACO_MESH_DATA
        void AllocateIndices(DracoMesh* dracoMesh) {
            Profiler.BeginSample("AllocateIndices");
            if (dracoMesh->indexFormat == IndexFormat.UInt16) {
                indices = new NativeIndexBuffer<ushort>(dracoMesh->numFaces * 3, allocator);
            } else {
                indices = new NativeIndexBuffer<uint>(dracoMesh->numFaces * 3, allocator);
            }
            Profiler.EndSample(); // AllocateIndices
        }
        
        void AllocateVertexBuffers(DracoMesh* dracoMesh) {
            int streamIndex;
            vData = new NativeArray<byte>[streamCount];
            vDataPtr = new byte*[streamCount];
            for (streamIndex = 0; streamIndex < streamCount; streamIndex++) {
                vData[streamIndex] = new NativeArray<byte>(streamStrides[streamIndex] * dracoMesh->numVertices, allocator, NativeArrayOptions.UninitializedMemory);
                vDataPtr[streamIndex] = (byte*)vData[streamIndex].GetUnsafePtr();
            }
        }
#endif

#if UNITY_EDITOR
        public void DecodeVertexDataSync() {
            DecodeVertexData(true);
        }
#endif

        public JobHandle DecodeVertexData(
#if UNITY_EDITOR
            bool sync = false
#endif
            )
        {
            var decodeVerticesJob = new DecodeVerticesJob() {
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources
            };
            var decodeVerticesJobHandle = decodeVerticesJob.Schedule();
#if UNITY_EDITOR
            if (sync) {
                decodeVerticesJobHandle.Complete();
            }
#endif
            
            var indicesJob = new GetDracoIndicesJob() {
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources,
                flip = convertSpace,
                dataType = mesh.indexFormat == IndexFormat.UInt16 ? DataType.DT_UINT16 : DataType.DT_UINT32, 
#if DRACO_MESH_DATA
                mesh = mesh
#else
                indicesPtr = indices.unsafePtr,
                indicesLength =  indices.Length
#endif
            };
            var jobCount = attributes.Count + 1;
            
            if (hasBoneWeightData) jobCount++;

            var jobHandles = new NativeArray<JobHandle>(jobCount, allocator) {
                [0] = indicesJob.Schedule(decodeVerticesJobHandle)
            };

#if UNITY_EDITOR
            if (sync) {
                jobHandles[0].Complete();
            }
#endif
            
            int jobIndex = 1;
            foreach (var mapBase in attributes) {
                var map = mapBase as AttributeMap;
                if(map == null) continue;
                
                // BLENDHACK: skip blend indices here (done below)
                // weights were removed from attributes before
                if(map.attribute == VertexAttribute.BlendIndices) continue; // Blend
                
                if (streamMemberCount[map.stream] > 1) {
                    var job = new GetDracoDataInterleavedJob() {
                        result = dracoDecodeResult,
                        dracoTempResources = dracoTempResources,
                        attribute = map.dracoAttribute,
                        stride = streamStrides[map.stream],
                        flip = map.convertSpace,
#if DRACO_MESH_DATA                        
                        mesh = mesh, 
                        streamIndex = map.stream, 
                        offset = map.offset
#else
                        dstPtr = vDataPtr[map.stream] + map.offset
#endif                        
                    };
                    jobHandles[jobIndex] = job.Schedule(decodeVerticesJobHandle);
                }
                else {
                    var job = new GetDracoDataJob() {
                        result = dracoDecodeResult,
                        dracoTempResources = dracoTempResources,
                        attribute = map.dracoAttribute,
                        flip = map.convertSpace,
#if DRACO_MESH_DATA                        
                        mesh = mesh, 
                        streamIndex = map.stream
#else
                        dstPtr = vDataPtr[map.stream] + map.offset
#endif
                    };
                    jobHandles[jobIndex] = job.Schedule(decodeVerticesJobHandle);
                }
#if UNITY_EDITOR
                if (sync) {
                    jobHandles[jobIndex].Complete();
                }
#endif
                jobIndex++;
            }

            if (hasBoneWeightData) {
                // TODO: BLENDHACK;
                var job = new GetDracoBonesJob() {
                    result = dracoDecodeResult,
                    dracoTempResources = dracoTempResources,
                    indicesAttribute = boneIndexMap.dracoAttribute,
                    weightsAttribute = boneWeightMap.dracoAttribute,
                    bonesPerVertex = bonesPerVertex,
                    boneWeights = boneWeights,
                    indexValueConverter = GetIndexValueConverter(boneIndexMap.format)
                };
                jobHandles[jobIndex] = job.Schedule(decodeVerticesJobHandle);
            }
            
            var jobHandle = JobHandle.CombineDependencies(jobHandles);
            jobHandles.Dispose();

            var releaseDracoMeshJob = new ReleaseDracoMeshJob {
                dracoTempResources = dracoTempResources
            };
            var releaseDreacoMeshJobHandle = releaseDracoMeshJob.Schedule(jobHandle);

#if UNITY_EDITOR
            if (sync) {
                releaseDreacoMeshJobHandle.Complete();
            }
#endif
            return releaseDreacoMeshJobHandle;
        }

        internal void CreateMesh(
            out bool calculateNormals,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
        )
        {
            Profiler.BeginSample("CreateMesh");
            
            var dracoMesh = (DracoMesh*)dracoTempResources[meshPtrIndex];
            allocator = dracoMesh->numVertices > persistentDataThreshold ? Allocator.Persistent : Allocator.TempJob;
            
            CalculateVertexParams(
                dracoMesh,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                out calculateNormals,
                forceUnityLayout
                );
            
            Profiler.BeginSample("SetParameters");
            isPointCloud = dracoMesh->isPointCloud;
#if DRACO_MESH_DATA
            indicesCount = dracoMesh->numFaces * 3;
#else
            mesh = new Mesh();
#endif
            if (!isPointCloud) {
                mesh.SetIndexBufferParams(dracoMesh->numFaces*3, dracoMesh->indexFormat);
            }
            var vertexParams = new List<VertexAttributeDescriptor>(attributes.Count);
            foreach (var map in attributes) {
                vertexParams.Add(map.GetVertexAttributeDescriptor());
            }
            mesh.SetVertexBufferParams(dracoMesh->numVertices, vertexParams.ToArray());
#if !DRACO_MESH_DATA
            AllocateIndices(dracoMesh);
            AllocateVertexBuffers(dracoMesh);
#endif
            if (hasBoneWeightData) {
                var boneCount = boneIndexMap.numComponents;
                bonesPerVertex = new NativeArray<byte>(dracoMesh->numVertices, Allocator.Persistent);
                boneWeights = new NativeArray<BoneWeight1>(dracoMesh->numVertices * boneCount, Allocator.Persistent);
            }
            Profiler.EndSample(); // SetParameters
            Profiler.EndSample(); // CreateMesh
        }

        public void DisposeDracoMesh() {
            dracoDecodeResult.Dispose();
            dracoTempResources.Dispose();
        }

#if DRACO_MESH_DATA
        public bool 
#else
        public Mesh
#endif
        PopulateMeshData() {
            
            Profiler.BeginSample("PopulateMeshData");
            
            foreach (var map in attributes) {
                map.Dispose();
            }
            attributes = null;

            Profiler.BeginSample("MeshAssign");

            const MeshUpdateFlags flags = DracoMeshLoader.defaultMeshUpdateFlags;

#if !DRACO_MESH_DATA
            for (var streamIndex = 0; streamIndex < streamCount; streamIndex++) {
                mesh.SetVertexBufferData(vData[streamIndex], 0, 0, vData[streamIndex].Length, streamIndex, flags);
            }

            indices.SetMeshIndexBufferData(mesh);
            var indicesCount = indices.Length;

            if (hasBoneWeightData) {
                mesh.SetBoneWeights(bonesPerVertex,boneWeights);
            }
#endif

            mesh.subMeshCount = 1;
            var submeshDescriptor = new SubMeshDescriptor(0, indicesCount, isPointCloud ? MeshTopology.Points : MeshTopology.Triangles) { firstVertex = 0, baseVertex = 0, vertexCount = mesh.vertexCount };
            mesh.SetSubMesh(0, submeshDescriptor, flags);
            Profiler.EndSample(); // CreateUnityMesh.CreateMesh

#if !DRACO_MESH_DATA
            Profiler.BeginSample("Dispose");
            indices.Dispose();
            foreach (var nativeArray in vData) {
                nativeArray.Dispose();
            }
            if (hasBoneWeightData) {
                DisposeBoneWeightData();
            }
            Profiler.EndSample();
#endif
            Profiler.EndSample();
            
#if DRACO_MESH_DATA
            return true;
#else
            return mesh;
#endif
        }

        public void DisposeBoneWeightData() {
#if !DRACO_MESH_DATA
            // If MeshData is used, NativeArrays are passed to user and
            // it becomes their responsibility to properly Dispose them.
            bonesPerVertex.Dispose();
            boneWeights.Dispose();
#endif
            boneIndexMap = null;
            boneWeightMap = null;
        }

        /// <summary>
        /// Returns Burst compatible function that converts a (bone) index
        /// of type `format` into an int
        /// </summary>
        /// <param name="format">Data type of bone index</param>
        /// <returns>Burst Function Pointer to correct conversion function</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        static FunctionPointer<GetDracoBonesJob.GetIndexValueDelegate> GetIndexValueConverter(VertexAttributeFormat format) {
            switch (format) {
                case VertexAttributeFormat.UInt8:
                    if (!GetIndexValueUInt8Method.IsCreated) {
                        GetIndexValueUInt8Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueUInt8);
                    }
                    return GetIndexValueUInt8Method;
                case VertexAttributeFormat.SInt8:
                    if (!GetIndexValueInt8Method.IsCreated) {
                        GetIndexValueInt8Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueInt8);
                    }
                    return GetIndexValueInt8Method;
                case VertexAttributeFormat.UInt16:
                    if (!GetIndexValueUInt16Method.IsCreated) {
                        GetIndexValueUInt16Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueUInt16);
                    }
                    return GetIndexValueUInt16Method;
                case VertexAttributeFormat.SInt16:
                    if (!GetIndexValueInt16Method.IsCreated) {
                        GetIndexValueInt16Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueInt16);
                    }
                    return GetIndexValueInt16Method;
                case VertexAttributeFormat.UInt32:
                    if (!GetIndexValueUInt32Method.IsCreated) {
                        GetIndexValueUInt32Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueUInt32);
                    }
                    return GetIndexValueUInt32Method;
                case VertexAttributeFormat.SInt32:
                    if (!GetIndexValueInt32Method.IsCreated) {
                        GetIndexValueInt32Method = BurstCompiler.CompileFunctionPointer<GetDracoBonesJob.GetIndexValueDelegate>(GetDracoBonesJob.GetIndexValueInt32);
                    }
                    return GetIndexValueInt32Method;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        // The order must be consistent with C++ interface.
        [StructLayout (LayoutKind.Sequential)] public struct DracoData
        {
            public int dataType;
            public IntPtr data;
        }

        [StructLayout (LayoutKind.Sequential)] public struct DracoAttribute
        {
            public int attributeType;
            public int dataType;
            public int numComponents;
            public int uniqueId;
        }

        [StructLayout (LayoutKind.Sequential)] public struct DracoMesh
        {
            public int numFaces;
            public int numVertices;
            public int numAttributes;
            public bool isPointCloud;

            public IndexFormat indexFormat => numVertices >= ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        }

        // Release data associated with DracoMesh.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern void ReleaseDracoMesh(
            DracoMesh**mesh);
        // Release data associated with DracoAttribute.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern void
            ReleaseDracoAttribute(DracoAttribute**attr);
        // Release attribute data.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern void ReleaseDracoData(
            DracoData**data);

        // Decodes compressed Draco::Mesh in buffer to mesh. On input, mesh
        // must be null. The returned mesh must released with ReleaseDracoMesh.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern int DecodeDracoMeshStep1(
            byte* buffer, int length, DracoMesh**mesh, void**decoder, void** decoderBuffer);
        
        // Decodes compressed Draco::Mesh in buffer to mesh. On input, mesh
        // must be null. The returned mesh must released with ReleaseDracoMesh.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern int DecodeDracoMeshStep2(
            DracoMesh**mesh, void* decoder, void* decoderBuffer);
        
        // Returns the DracoAttribute at index in mesh. On input, attribute must be
        // null. The returned attr must be released with ReleaseDracoAttribute.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern bool GetAttribute(
            DracoMesh* mesh, int index, DracoAttribute**attr);
        // Returns the DracoAttribute of type at index in mesh. On input, attribute
        // must be null. E.g. If the mesh has two texture coordinates then
        // GetAttributeByType(mesh, AttributeType.TEX_COORD, 1, &attr); will return
        // the second TEX_COORD attribute. The returned attr must be released with
        // ReleaseDracoAttribute.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern bool GetAttributeByType(
            DracoMesh* mesh, AttributeType type, int index, DracoAttribute**attr);
        // Returns the DracoAttribute with unique_id in mesh. On input, attribute
        // must be null.The returned attr must be released with
        // ReleaseDracoAttribute.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern bool
            GetAttributeByUniqueId(DracoMesh* mesh, int unique_id,
                DracoAttribute**attr);
        
        /// <summary>
        /// Returns an array of indices as well as the type of data in data_type. On
        /// input, indices must be null. The returned indices must be released with
        /// ReleaseDracoData. 
        /// </summary>
        /// <param name="mesh">DracoMesh to extract indices from</param>
        /// <param name="dataType">Index data type (int or short) </param>
        /// <param name="indices">Destination index buffer</param>
        /// <param name="indicesCount">Number of indices (equals triangle count * 3)</param>
        /// <param name="flip">If true, triangle vertex order is reverted</param>
        /// <returns>True if extraction succeeded, false otherwise</returns>
        [DllImport (DRACODEC_UNITY_LIB)] static extern bool GetMeshIndices(
            DracoMesh* mesh,
            DataType dataType,
            void* indices,
            int indicesCount,
            bool flip
            );

        // Returns an array of attribute data as well as the type of data in
        // data_type. On input, data must be null. The returned data must be
        // released with ReleaseDracoData.
        [DllImport (DRACODEC_UNITY_LIB)] unsafe static extern bool GetAttributeData(
            DracoMesh* mesh, DracoAttribute* attr, DracoData**data, bool flip);

        abstract class AttributeMapBase : IComparable<AttributeMapBase> {
            readonly public VertexAttribute attribute;
            public VertexAttributeFormat format;
            public int offset;
            public int stream;
            public bool flip;
        
            public AttributeMapBase (VertexAttribute attribute, VertexAttributeFormat format) {
                this.attribute = attribute;
                this.format = format;
                offset = 0;
                stream = 0;
            }

            public abstract int numComponents { get; }
            public abstract int elementSize { get; }

            public virtual void Dispose() {}

            public VertexAttributeDescriptor GetVertexAttributeDescriptor() {
                return new VertexAttributeDescriptor(attribute,format,numComponents,stream);
            }

            public int CompareTo(AttributeMapBase b) {
                var result = stream.CompareTo(b.stream);
                if (result == 0) result = offset.CompareTo(b.offset);
                return result;
            }
        }
        
        class AttributeMap : AttributeMapBase {
            public DracoAttribute* dracoAttribute;
            public bool convertSpace = false;

            public AttributeMap (DracoAttribute* dracoAttribute, VertexAttribute attribute, VertexAttributeFormat format, bool convertSpace) : base(attribute,format) {
                this.dracoAttribute = dracoAttribute;
                this.convertSpace = convertSpace;
            }

            public override int numComponents => dracoAttribute->numComponents;
            public override int elementSize => DataTypeSize((DataType)dracoAttribute->dataType) * dracoAttribute->numComponents;

            public override void Dispose() {
                var tmp = dracoAttribute;
                ReleaseDracoAttribute(&tmp);
                dracoAttribute = null;
            }
        }

        
        class CalculatedAttributeMap : AttributeMapBase {
            public int m_numComponents;
            public int m_elementSize;
            
            public CalculatedAttributeMap (VertexAttribute attribute, VertexAttributeFormat format, int numComponents, int componentSize) : base(attribute,format) {
                m_numComponents = numComponents;
                m_elementSize = componentSize * numComponents;
            }

            public override int numComponents => m_numComponents;
            public override int elementSize => m_elementSize;
        }

#if !DRACO_MESH_DATA
        abstract class NativeIndexBufferBase : IDisposable {
            public abstract void* unsafePtr {get;}
            public abstract int Length {get;}
            public abstract void SetMeshIndexBufferData(Mesh mesh);
            public abstract void Dispose();
        }
        
        class NativeIndexBuffer<T> : NativeIndexBufferBase where T : struct {
            NativeArray<T> indices;

            public NativeIndexBuffer(int length, Allocator allocator) {
                indices = new NativeArray<T>(length, allocator, NativeArrayOptions.UninitializedMemory);
            }

            public override void* unsafePtr => indices.GetUnsafePtr();
            public override int Length => indices.Length;
            
            public override void SetMeshIndexBufferData(Mesh mesh) {
                mesh.SetIndexBufferData( indices, 0, 0, indices.Length);
            }

            public override void Dispose() {
                indices.Dispose();
            }
        }
#endif

        [BurstCompile]
        struct DecodeJob : IJob {
            
            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public byte* encodedData;

            [ReadOnly]
            public int size;

            public NativeArray<int> result;
            public NativeArray<IntPtr> dracoTempResources;

            public void Execute() {
                DracoMesh* dracoMeshPtr;
                DracoMesh** dracoMeshPtrPtr = &dracoMeshPtr;
                void* decoder;
                void* buffer;
                var decodeResult = DecodeDracoMeshStep1(encodedData, size, dracoMeshPtrPtr, &decoder, &buffer);
                result[0] = decodeResult;
                if (decodeResult < 0) {
                    return;
                }
                dracoTempResources[meshPtrIndex] = (IntPtr) dracoMeshPtr;
                dracoTempResources[decoderPtrIndex] = (IntPtr) decoder;
                dracoTempResources[bufferPtrIndex] = (IntPtr) buffer;
                result[0] = 0;
            }
        }
        
        [BurstCompile]
        struct DecodeVerticesJob : IJob {
            
            public NativeArray<int> result;
            public NativeArray<IntPtr> dracoTempResources;

            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMeshPtr = (DracoMesh*) dracoTempResources[meshPtrIndex];
                var dracoMeshPtrPtr = &dracoMeshPtr;
                var decoder = (void*) dracoTempResources[decoderPtrIndex];
                var buffer = (void*) dracoTempResources[bufferPtrIndex];
                var decodeResult = DecodeDracoMeshStep2(dracoMeshPtrPtr, decoder, buffer);
                result[0] = decodeResult;
            }
        }
        
        [BurstCompile]
        struct GetDracoIndicesJob : IJob {
            
            [ReadOnly]
            public NativeArray<int> result;
            [ReadOnly]
            public NativeArray<IntPtr> dracoTempResources;
            [ReadOnly]
            public bool flip;
            [ReadOnly]
            public DataType dataType; 
#if DRACO_MESH_DATA
            public Mesh.MeshData mesh;
#else
            [NativeDisableUnsafePtrRestriction]
            public void* indicesPtr;
            public int indicesLength;
#endif

            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];
                if (dracoMesh->isPointCloud) {
                    return;
                }
#if DRACO_MESH_DATA
                void* indicesPtr;
                int indicesLength;

                switch (dataType) {
                    case DataType.DT_UINT16: {
                        var indices = mesh.GetIndexData<ushort>();
                        indicesPtr = indices.GetUnsafePtr();
                        indicesLength = indices.Length;
                        break;
                    }
                    case DataType.DT_UINT32: {
                        var indices = mesh.GetIndexData<uint>();
                        indicesPtr = indices.GetUnsafePtr();
                        indicesLength = indices.Length;
                        break;
                    }
                    default:
                        result[0] = -1;
                        return;
                }
#endif
                GetMeshIndices(dracoMesh, dataType, indicesPtr, indicesLength, flip);
            }
        }

        [BurstCompile]
        struct GetDracoDataJob : IJob {
            
            [ReadOnly]
            public NativeArray<int> result;
            [ReadOnly]
            public NativeArray<IntPtr> dracoTempResources;

            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public DracoAttribute* attribute;

            [ReadOnly]
            public bool flip;

#if DRACO_MESH_DATA
            public Mesh.MeshData mesh;
            [ReadOnly]
            public int streamIndex;
#else
            [WriteOnly]
            [NativeDisableUnsafePtrRestriction]
            public byte* dstPtr;
        
#endif
            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];
                DracoData* data = null;
                GetAttributeData(dracoMesh, attribute, &data, flip);
                var elementSize = DataTypeSize((DataType)data->dataType) * attribute->numComponents;
#if DRACO_MESH_DATA
                var dst = mesh.GetVertexData<byte>(streamIndex);
                var dstPtr = dst.GetUnsafePtr();
#endif
                UnsafeUtility.MemCpy(dstPtr, (void*)data->data, elementSize*dracoMesh->numVertices);
                ReleaseDracoData(&data);
            }
        }

        [BurstCompile]
        struct GetDracoDataInterleavedJob : IJob {

            [ReadOnly]
            public NativeArray<int> result;
            [ReadOnly]
            public NativeArray<IntPtr> dracoTempResources;

            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public DracoAttribute* attribute;
        
            [ReadOnly]
            public int stride;
            
            [ReadOnly]
            public bool flip;

#if DRACO_MESH_DATA
            public Mesh.MeshData mesh;
            [ReadOnly]
            public int streamIndex;
            [ReadOnly]
            public int offset;
#else
            [WriteOnly]
            [NativeDisableUnsafePtrRestriction]
            public byte* dstPtr;
#endif
            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];
                DracoData* data = null;
                GetAttributeData(dracoMesh, attribute, &data, flip);
                var elementSize = DataTypeSize((DataType)data->dataType) * attribute->numComponents;
#if DRACO_MESH_DATA
                var dst = mesh.GetVertexData<byte>(streamIndex);
                var dstPtr =  ((byte*)dst.GetUnsafePtr()) + offset;
#endif
                for (var v = 0; v < dracoMesh->numVertices; v++) {
                    UnsafeUtility.MemCpy(dstPtr+(stride*v), ((byte*)data->data)+(elementSize*v), elementSize);
                }
                ReleaseDracoData(&data);
            }
        }
        
        [BurstCompile]
        struct GetDracoBonesJob : IJob {
            
            public delegate int GetIndexValueDelegate(IntPtr baseAddress, int index);
            
            public FunctionPointer<GetIndexValueDelegate> indexValueConverter;
            
            [ReadOnly]
            public NativeArray<int> result;
            [ReadOnly]
            public NativeArray<IntPtr> dracoTempResources;

            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public DracoAttribute* indicesAttribute;
            
            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public DracoAttribute* weightsAttribute;
            
            [WriteOnly]
            public NativeArray<byte> bonesPerVertex;
            
            [WriteOnly]
            public NativeArray<BoneWeight1> boneWeights;
            
            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];

                DracoData* indicesData = null;
                GetAttributeData(dracoMesh, indicesAttribute, &indicesData, false);
                var indicesDataType = (DataType)indicesData->dataType;
                var indexSize = DataTypeSize((DataType)indicesData->dataType) * indicesAttribute->numComponents;
                
                DracoData* weightsData = null;
                GetAttributeData(dracoMesh, weightsAttribute, &weightsData, false);
                var weightSize = DataTypeSize((DataType)weightsData->dataType) * weightsAttribute->numComponents;
                
                for (var v = 0; v < dracoMesh->numVertices; v++) {
                    bonesPerVertex[v] = (byte) indicesAttribute->numComponents;
                    var indicesPtr = (IntPtr) (((byte*)indicesData->data) + (indexSize * v));
                    var weightsPtr = (float*) (((byte*)weightsData->data) + (weightSize * v));
                    for (var b = 0; b < indicesAttribute->numComponents; b++) {
                        boneWeights[v * indicesAttribute->numComponents + b] = new BoneWeight1 {
                            boneIndex = indexValueConverter.Invoke(indicesPtr,b),
                            weight = *(weightsPtr + b)
                        };
                    }
                }
                ReleaseDracoData(&indicesData);
                ReleaseDracoData(&weightsData);
            }

            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueUInt8(IntPtr baseAddress, int index) {
                return *((byte*)baseAddress+index);
            }
            
            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueInt8(IntPtr baseAddress, int index) {
                return *(((sbyte*)baseAddress)+index);
            }
            
            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueUInt16(IntPtr baseAddress, int index) {
                return *(((ushort*)baseAddress)+index);
            }
            
            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueInt16(IntPtr baseAddress, int index) {
                return *(((short*)baseAddress)+index);
            }
            
            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueUInt32(IntPtr baseAddress, int index) {
                return (int) *(((uint*)baseAddress)+index);
            }
            
            [BurstCompile]
            [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
            public static int GetIndexValueInt32(IntPtr baseAddress, int index) {
                return *(((int*)baseAddress)+index);
            }
        }
      
        [BurstCompile]
        struct ReleaseDracoMeshJob : IJob {

            public NativeArray<IntPtr> dracoTempResources;

            public void Execute() {
                if (dracoTempResources[meshPtrIndex] != IntPtr.Zero) {
                    var dracoMeshPtr = (DracoMesh**) dracoTempResources.GetUnsafePtr();
                    ReleaseDracoMesh(dracoMeshPtr);
                }
                dracoTempResources[meshPtrIndex]=IntPtr.Zero;
                dracoTempResources[decoderPtrIndex]=IntPtr.Zero;
                dracoTempResources[bufferPtrIndex]=IntPtr.Zero;
            }
        }
      
        static int DataTypeSize(DataType dt) {
            switch (dt) {
                case DataType.DT_INT8:
                case DataType.DT_UINT8:
                    return 1;
                case DataType.DT_INT16:
                case DataType.DT_UINT16:
                    return 2;
                case DataType.DT_INT32:
                case DataType.DT_UINT32:
                    return 4;
                case DataType.DT_INT64:
                case DataType.DT_UINT64:
                    return 8;
                case DataType.DT_FLOAT32:
                    return 4;
                case DataType.DT_FLOAT64:
                    return 8;
                case DataType.DT_BOOL:
                    return 1;
                default:
                    return -1;
            }
        }
      
        VertexAttributeFormat? GetVertexAttributeFormat(DataType inputType, bool normalized = false) {
            switch (inputType) {
                case DataType.DT_INT8:
                    return normalized ? VertexAttributeFormat.SNorm8 : VertexAttributeFormat.SInt8;
                case DataType.DT_UINT8:
                    return normalized ? VertexAttributeFormat.UNorm8 : VertexAttributeFormat.UInt8;
                case DataType.DT_INT16:
                    return normalized ? VertexAttributeFormat.SNorm16 : VertexAttributeFormat.SInt16;
                case DataType.DT_UINT16:
                    return normalized ? VertexAttributeFormat.UNorm16 : VertexAttributeFormat.UInt16;
                case DataType.DT_INT32:
                    return VertexAttributeFormat.SInt32;
                case DataType.DT_UINT32:
                    return VertexAttributeFormat.UInt32;
                case DataType.DT_FLOAT32:
                    return VertexAttributeFormat.Float32;
                // Not supported by Unity
                // TODO: convert to supported types
                // case DataType.DT_INT64:
                // case DataType.DT_UINT64:
                // case DataType.DT_FLOAT64:
                // case DataType.DT_BOOL:
                default:
                    return null;
            }
        }

        VertexAttribute? GetVertexAttribute(AttributeType inputType, int index=0) {
            switch (inputType) {
                case AttributeType.POSITION:
                    return VertexAttribute.Position;
                case AttributeType.NORMAL:
                    return VertexAttribute.Normal;
                case AttributeType.COLOR:
                    return VertexAttribute.Color;
                case AttributeType.TEX_COORD:
                    Assert.IsTrue(index<8);
                    return (VertexAttribute) ((int)VertexAttribute.TexCoord0+index);
                default:
                    return null;
            }
        }

        bool ConvertSpace(VertexAttribute attr) {
            switch (attr) {
                case VertexAttribute.Position:
                case VertexAttribute.Normal:
                case VertexAttribute.Tangent:
                case VertexAttribute.TexCoord0:
                case VertexAttribute.TexCoord1:
                case VertexAttribute.TexCoord2:
                case VertexAttribute.TexCoord3:
                case VertexAttribute.TexCoord4:
                case VertexAttribute.TexCoord5:
                case VertexAttribute.TexCoord6:
                case VertexAttribute.TexCoord7:
                    return true;
                default:
                    return false;
            }
        }
    }
}
