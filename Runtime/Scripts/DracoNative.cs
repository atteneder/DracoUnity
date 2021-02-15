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

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Draco {

    unsafe class DracoNative {
        
        const int maxStreamCount = 4;
        
        /// <summary>
        /// If Draco mesh has more vertices than this value, memory is allocated persistent,
        /// which is slower, but safe when spanning multiple frames.
        /// </summary>
        const int persistentDataThreshold = 5_000;
        
        const int meshPtrIndex = 0;
        const int decoderPtrIndex = 1;
        const int bufferPtrIndex = 2;

        /// <summary>
        /// If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).
        /// </summary>
        bool convertSpace;

        Dictionary<VertexAttribute, AttributeMapBase> attributes;
        int streamCount;
        int[] streamStrides;
        int[] streamMemberCount;

        Allocator allocator;
        NativeArray<int> dracoDecodeResult;
        NativeArray<IntPtr> dracoTempResources;

#if UNITY_2020_2_OR_NEWER
        Mesh.MeshData mesh;
#else
        Mesh mesh;
        NativeArray<uint> indices;
        NativeArray<byte>[] vData;
        byte*[] vDataPtr;
#endif


        public DracoNative(
#if UNITY_2020_2_OR_NEWER
            Mesh.MeshData mesh,
#endif
            bool convertSpace = true
            )
        {
            this.convertSpace = convertSpace;
#if UNITY_2020_2_OR_NEWER
            this.mesh = mesh;
#endif
        }

        public JobHandle Init(IntPtr encodedData, int size) {
            dracoDecodeResult = new NativeArray<int>(1, Allocator.Persistent);
            dracoTempResources = new NativeArray<IntPtr>(3, Allocator.Persistent);
            var decodeJob = new DecodeJob() {
                encodedData = (byte*)encodedData,
                size = size,
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources
            };
            return decodeJob.Schedule();
        }

        public bool ErrorOccured() {
            return dracoDecodeResult[0] < 0;
        }
        
        void CalculateVertexParams(DracoMesh* dracoMesh, bool requireNormals, bool requireTangents, out bool calculateNormals)
        {
            Profiler.BeginSample("CalculateVertexParams");
            attributes = new Dictionary<VertexAttribute, AttributeMapBase>();
            
            bool CreateAttributeMaps(AttributeType attributeType, int count, DracoMesh* draco) {
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

                    if (attributes.ContainsKey(type.Value)) {
#if UNITY_EDITOR
                        // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
                        Debug.LogWarning($"Multiple {type.Value} attributes!");
#endif
                        continue;
                    }

                    DracoAttribute* attribute;
                    if (GetAttributeByType(draco, attributeType, i, &attribute)) {
                        var format = GetVertexAttributeFormat((DataType)attribute->dataType);
                        if (!format.HasValue) { continue; }

                        var map = new AttributeMap(attribute, format.Value);
                        attributes[type.Value] = map;
                        foundAttribute = true;
                    }
                    else {
                        // attributeType was not found
                        break;
                    }
                }
                return foundAttribute;
            }

            CreateAttributeMaps(AttributeType.POSITION, 1, dracoMesh);
            var hasNormals = CreateAttributeMaps(AttributeType.NORMAL, 1, dracoMesh);
            calculateNormals = !hasNormals && requireNormals;
            if (calculateNormals) {
                calculateNormals = true;
                attributes[VertexAttribute.Normal] = new CalculatedAttributeMap(VertexAttributeFormat.Float32, 3, 4 );
            }
            if (requireTangents) {
                attributes[VertexAttribute.Tangent] = new CalculatedAttributeMap(VertexAttributeFormat.Float32, 4, 4 );
            }
            CreateAttributeMaps(AttributeType.COLOR, 1, dracoMesh);
            var hasTexCoords = CreateAttributeMaps(AttributeType.TEX_COORD, 8, dracoMesh);

            // TODO: If known, query generic attributes by ID
            // CreateAttributeMaps(AttributeType.GENERIC,2);
        
            streamStrides = new int[maxStreamCount];
            streamMemberCount = new int[maxStreamCount];
            int streamIndex = 0;
            foreach (var pair in attributes) {
                // Naive stream assignment:
                // First 3 attributes get a dedicated stream (#1,#2 and #3 respectivly)
                // 4th and following get assigned to stream #4
                // TODO: Make smarter stream assignment decision
                var attributeMap = pair.Value;
                var elementSize = attributeMap.elementSize;
                attributeMap.offset = streamStrides[streamIndex];
                attributeMap.stream = streamIndex;
                streamStrides[streamIndex] += elementSize;
                streamMemberCount[streamIndex]++;
                if (streamIndex < maxStreamCount) { streamIndex++; }
            }
            streamCount = streamIndex;
            Profiler.EndSample(); // CalculateVertexParams
        }

#if !UNITY_2020_2_OR_NEWER
        void AllocateIndices(DracoMesh* dracoMesh) {
            Profiler.BeginSample("AllocateIndices");
            indices = new NativeArray<uint>(dracoMesh->numFaces * 3, allocator, NativeArrayOptions.UninitializedMemory);
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

        public JobHandle DecodeVertexData() {
            var decodeVerticesJob = new DecodeVerticesJob() {
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources
            };
            var decodeVerticesJobHandle = decodeVerticesJob.Schedule();
            
            var indicesJob = new GetDracoIndicesJob() {
                result = dracoDecodeResult,
                dracoTempResources = dracoTempResources,
                flip = convertSpace,
#if UNITY_2020_2_OR_NEWER
                mesh = mesh
#else
                indices = indices
#endif
            };
            
            NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(attributes.Count+1, allocator);
            
            jobHandles[0] = indicesJob.Schedule(decodeVerticesJobHandle);
            
            int jobIndex = 1;
            foreach (var pair in attributes) {
                AttributeMap map = pair.Value as AttributeMap;
                if(map == null) continue;
                if (streamMemberCount[map.stream] > 1) {
                    var job = new GetDracoDataInterleavedJob() {
                        result = dracoDecodeResult,
                        dracoTempResources = dracoTempResources,
                        attribute = map.dracoAttribute,
                        stride = streamStrides[map.stream],
                        flip = convertSpace,
#if UNITY_2020_2_OR_NEWER                        
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
                        flip = convertSpace,
#if UNITY_2020_2_OR_NEWER                        
                        mesh = mesh, 
                        streamIndex = map.stream
#else
                        dstPtr = vDataPtr[map.stream] + map.offset
#endif
                    };
                    jobHandles[jobIndex] = job.Schedule(decodeVerticesJobHandle);
                }
                jobIndex++;
            }
            var jobHandle = JobHandle.CombineDependencies(jobHandles);
            jobHandles.Dispose();

            var releaseDracoMeshJob = new ReleaseDracoMeshJob {
                dracoTempResources = dracoTempResources
            };
            return releaseDracoMeshJob.Schedule(jobHandle);
        }

        public void CreateMesh(
            out bool calculateNormals,
            bool requireNormals = false,
            bool requireTangents = false
            )
        {
            Profiler.BeginSample("CreateMesh");
            
            var dracoMesh = (DracoMesh*)dracoTempResources[meshPtrIndex];
            allocator = dracoMesh->numVertices > persistentDataThreshold ? Allocator.Persistent : Allocator.TempJob;
            
            CalculateVertexParams(dracoMesh, requireNormals, requireTangents,out calculateNormals);
            
            Profiler.BeginSample("SetParameters");
#if !UNITY_2020_2_OR_NEWER
            mesh = new Mesh();
#endif
            mesh.SetIndexBufferParams(dracoMesh->numFaces*3, IndexFormat.UInt32);
            var vertexParams = new List<VertexAttributeDescriptor>(attributes.Count);
            foreach (var pair in attributes) {
                var map = pair.Value;
                vertexParams.Add(new VertexAttributeDescriptor(pair.Key, map.format, map.numComponents, map.stream));
            }
            mesh.SetVertexBufferParams(dracoMesh->numVertices, vertexParams.ToArray());
#if !UNITY_2020_2_OR_NEWER
            AllocateIndices(dracoMesh);
            AllocateVertexBuffers(dracoMesh);
#endif
            Profiler.EndSample(); // SetParameters
            Profiler.EndSample(); // CreateMesh
        }

        public void DisposeDracoMesh() {
            dracoDecodeResult.Dispose();
            dracoTempResources.Dispose();
        }

#if UNITY_2020_2_OR_NEWER
        public bool 
#else
        public Mesh
#endif
        PopulateMeshData() {
            
            Profiler.BeginSample("PopulateMeshData");
            
            foreach (var map in attributes.Values) {
                map.Dispose();
            }
            attributes = null;

            Profiler.BeginSample("MeshAssign");
            
            const MeshUpdateFlags flags =
                MeshUpdateFlags.DontNotifyMeshUsers |
                MeshUpdateFlags.DontRecalculateBounds |
                MeshUpdateFlags.DontResetBoneBounds |
                MeshUpdateFlags.DontValidateIndices;

#if UNITY_2020_2_OR_NEWER
            var indices = mesh.GetIndexData<uint>();
#else
            for (var streamIndex = 0; streamIndex < streamCount; streamIndex++) {
                mesh.SetVertexBufferData(vData[streamIndex], 0, 0, vData[streamIndex].Length, streamIndex, flags);
            }

            mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
#endif

            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), flags);
            Profiler.EndSample(); // CreateUnityMesh.CreateMesh

#if UNITY_2020_2_OR_NEWER
#else
            Profiler.BeginSample("Dispose");
            indices.Dispose();
            foreach (var nativeArray in vData) {
                nativeArray.Dispose();
            }
            Profiler.EndSample();
#endif
            Profiler.EndSample();
            
#if UNITY_2020_2_OR_NEWER
            return true;
#else
            return mesh;
#endif
        }
      
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
        };

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
        };

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
        }

        // Release data associated with DracoMesh.
        [DllImport ("dracodec_unity")] unsafe static extern void ReleaseDracoMesh(
            DracoMesh**mesh);
        // Release data associated with DracoAttribute.
        [DllImport ("dracodec_unity")] unsafe static extern void
            ReleaseDracoAttribute(DracoAttribute**attr);
        // Release attribute data.
        [DllImport ("dracodec_unity")] unsafe static extern void ReleaseDracoData(
            DracoData**data);

        // Decodes compressed Draco::Mesh in buffer to mesh. On input, mesh
        // must be null. The returned mesh must released with ReleaseDracoMesh.
        [DllImport ("dracodec_unity")] unsafe static extern int DecodeDracoMeshStep1(
            byte* buffer, int length, DracoMesh**mesh, void**decoder, void** decoderBuffer);
        
        // Decodes compressed Draco::Mesh in buffer to mesh. On input, mesh
        // must be null. The returned mesh must released with ReleaseDracoMesh.
        [DllImport ("dracodec_unity")] unsafe static extern int DecodeDracoMeshStep2(
            DracoMesh**mesh, void* decoder, void* decoderBuffer);
        
        // Returns the DracoAttribute at index in mesh. On input, attribute must be
        // null. The returned attr must be released with ReleaseDracoAttribute.
        [DllImport ("dracodec_unity")] unsafe static extern bool GetAttribute(
            DracoMesh* mesh, int index, DracoAttribute**attr);
        // Returns the DracoAttribute of type at index in mesh. On input, attribute
        // must be null. E.g. If the mesh has two texture coordinates then
        // GetAttributeByType(mesh, AttributeType.TEX_COORD, 1, &attr); will return
        // the second TEX_COORD attribute. The returned attr must be released with
        // ReleaseDracoAttribute.
        [DllImport ("dracodec_unity")] unsafe static extern bool GetAttributeByType(
            DracoMesh* mesh, AttributeType type, int index, DracoAttribute**attr);
        // Returns the DracoAttribute with unique_id in mesh. On input, attribute
        // must be null.The returned attr must be released with
        // ReleaseDracoAttribute.
        [DllImport ("dracodec_unity")] unsafe static extern bool
            GetAttributeByUniqueId(DracoMesh* mesh, int unique_id,
                DracoAttribute**attr);

        // Returns an array of indices as well as the type of data in data_type. On
        // input, indices must be null. The returned indices must be released with
        // ReleaseDracoData.
        [DllImport ("dracodec_unity")] unsafe static extern bool GetMeshIndices(
            DracoMesh* mesh, DracoData**indices, bool flip);
        // Returns an array of attribute data as well as the type of data in
        // data_type. On input, data must be null. The returned data must be
        // released with ReleaseDracoData.
        [DllImport ("dracodec_unity")] unsafe static extern bool GetAttributeData(
            DracoMesh* mesh, DracoAttribute* attr, DracoData**data, bool flip);

        abstract class AttributeMapBase {
            public VertexAttributeFormat format;
            public int offset;
            public int stream;
        
            public AttributeMapBase (VertexAttributeFormat format) {
                this.format = format;
                offset = 0;
                stream = 0;
            }

            public abstract int numComponents { get; }
            public abstract int elementSize { get; }

            public virtual void Dispose() {}
        }
        
        class AttributeMap : AttributeMapBase {
            public DracoAttribute* dracoAttribute;

            public AttributeMap (DracoAttribute* dracoAttribute, VertexAttributeFormat format) : base(format) {
                this.dracoAttribute = dracoAttribute;
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
            
            public CalculatedAttributeMap (VertexAttributeFormat format, int numComponents, int componentSize) : base(format) {
                m_numComponents = numComponents;
                m_elementSize = componentSize * numComponents;
            }

            public override int numComponents => m_numComponents;
            public override int elementSize => m_elementSize;
        }

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
        
#if UNITY_2020_2_OR_NEWER
            public Mesh.MeshData mesh;
#else
            [WriteOnly]
            public NativeArray<uint> indices;
#endif

            public void Execute() {
                if (result[0]<0) {
                    return;
                }
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];
                DracoData* dracoIndices;
                GetMeshIndices(dracoMesh, &dracoIndices, flip);
                int indexSize = DataTypeSize((DataType)dracoIndices->dataType);
                int* indicesData = (int*)dracoIndices->data;
#if UNITY_2020_2_OR_NEWER
                var indices = mesh.GetIndexData<uint>();
#endif
                UnsafeUtility.MemCpy(indices.GetUnsafePtr(), indicesData, indices.Length * indexSize);
                ReleaseDracoData(&dracoIndices);
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

#if UNITY_2020_2_OR_NEWER
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
#if UNITY_2020_2_OR_NEWER
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

#if UNITY_2020_2_OR_NEWER
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
                var dracoMesh = (DracoMesh*) dracoTempResources[meshPtrIndex];
                DracoData* data = null;
                GetAttributeData(dracoMesh, attribute, &data, flip);
                var elementSize = DataTypeSize((DataType)data->dataType) * attribute->numComponents;
#if UNITY_2020_2_OR_NEWER
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
      
        VertexAttributeFormat? GetVertexAttributeFormat(DataType inputType) {
            switch (inputType) {
                case DataType.DT_INT8:
                    return VertexAttributeFormat.SInt8;
                case DataType.DT_UINT8:
                    return VertexAttributeFormat.UInt8;
                case DataType.DT_INT16:
                    return VertexAttributeFormat.SInt16;
                case DataType.DT_UINT16:
                    return VertexAttributeFormat.UInt16;
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
                    return (VertexAttribute) ((int)VertexAttribute.TexCoord0+index);
                // case AttributeType.GENERIC:
                //   // TODO: map generic to possible candidates (BlendWeights, BlendIndices)
                //   break;
                default:
                    return null;
            }
        }
    }
}
