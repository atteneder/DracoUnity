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
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Draco.Encoder {

    /// <summary>
    /// Contains encoded data and additional meta information.
    /// The responsibility to dispose this struct and the native resources behind it (via <see cref="Dispose"/>)
    /// is handed over to the receiver.
    /// </summary>
    public unsafe struct EncodeResult : IDisposable {

        /// <summary>Number of triangle indices</summary>
        public uint indexCount;
        /// <summary>Number vertices</summary>
        public uint vertexCount;
        /// <summary>Encoded data</summary>
        public NativeArray<byte> data;
        /// <summary>Vertex attribute to Draco property ID mapping</summary>
        public Dictionary<VertexAttribute,(uint identifier,int dimensions)> vertexAttributes;

        IntPtr m_DracoEncoder;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_SafetyHandle;
#endif

        /// <summary>
        /// Constructs an EncodeResult.
        /// </summary>
        /// <param name="dracoEncoder">Native Draco encoder instance.</param>
        /// <param name="indexCount">Number of indices.</param>
        /// <param name="vertexCount">Number of vertices.</param>
        /// <param name="vertexAttributes">For each vertex attribute type there's a tuple containing
        /// the draco identifier and the attribute dimensions (e.g. 3 for 3D positions).</param>
        public EncodeResult(
            IntPtr dracoEncoder,
            uint indexCount,
            uint vertexCount,
            Dictionary<VertexAttribute,(uint identifier,int dimensions)> vertexAttributes
            )
        {
            m_DracoEncoder = dracoEncoder;
            this.indexCount = indexCount;
            this.vertexCount = vertexCount;
            DracoEncoder.dracoEncoderGetEncodeBuffer(m_DracoEncoder, out var dracoData, out var size);
            data = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dracoData, (int)size, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_SafetyHandle = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(array: ref data, m_SafetyHandle);
#endif
            this.vertexAttributes = vertexAttributes;
        }
        
        /// <summary>
        /// Releases allocated resources.
        /// </summary>
        public void Dispose()
        {
            vertexAttributes = null;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(m_SafetyHandle);
#endif
            if(m_DracoEncoder!=IntPtr.Zero)
                DracoEncoder.dracoEncoderRelease(m_DracoEncoder);
            m_DracoEncoder = IntPtr.Zero;
        }
    }
    
    /// <summary>
    /// Provides Draco encoding capabilities.
    /// </summary>
    public static class DracoEncoder {
        
#if UNITY_EDITOR_OSX || UNITY_WEBGL || UNITY_IOS
        const string k_DracoEncUnityLib = "__Internal";
#elif UNITY_ANDROID || UNITY_STANDALONE || UNITY_WSA || UNITY_EDITOR || PLATFORM_LUMIN
        const string k_DracoEncUnityLib = "dracoenc_unity";
#endif

        struct AttributeData {
            public int stream;
            public int offset;
        }

        /// <summary>
        /// Calculates the ideal quantization value based on the largest dimension and desired precision
        /// </summary>
        /// <param name="largestDimension">Length of the largest dimension (width/depth/height)</param>
        /// <param name="precision">Desired minimum precision in world units</param>
        /// <returns>Ideal quantization in bits</returns>
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
        /// Calculates the ideal position quantization value based on an object's world scale, bounds and the desired
        /// precision in world unit.
        /// </summary>
        /// <param name="worldScale">World scale of the object</param>
        /// <param name="precision">Desired minimum precision in world units</param>
        /// <param name="bounds"></param>
        /// <returns>Ideal quantization in bits</returns>
        static int GetIdealQuantization(Vector3 worldScale, float precision, Bounds bounds) {
            var scale = new Vector3(Mathf.Abs(worldScale.x), Mathf.Abs(worldScale.y), Mathf.Abs(worldScale.z));
            var maxSize = Mathf.Max(
                bounds.extents.x * scale.x,
                bounds.extents.y * scale.y,
                bounds.extents.z * scale.z
                ) * 2;
            var positionQuantization = GetIdealQuantization(maxSize, precision);
            return positionQuantization;
        }

        /// <summary>
        /// Applies Draco compression to a given mesh and returns the encoded result (one per submesh)
        /// The quality and quantization parameters are calculated from the mesh's bounds, its worldScale and desired precision.
        /// The quantization parameters help to find a balance between compressed size and quality / precision.
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
        /// <returns>Encoded data (one per submesh)</returns>
        public static async Task<EncodeResult[]> EncodeMesh(
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
#if !UNITY_EDITOR
            if (!unityMesh.isReadable) {
                Debug.LogError("Mesh is not readable");
                return null;
            }
#endif
            var positionQuantization = GetIdealQuantization(worldScale, precision, unityMesh.bounds);

            return await EncodeMesh(
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
        /// Applies Draco compression to a given mesh/meshData and returns the encoded result (one per submesh)
        /// The user is responsible for
        /// <see cref="UnityEngine.Mesh.AcquireReadOnlyMeshData(List&lt;Mesh&gt;)">acquiring the readable MeshData</see>
        /// and disposing it.
        /// The quality and quantization parameters are calculated from the mesh's bounds, its worldScale and desired precision.
        /// The quantization parameters help to find a balance between compressed size and quality / precision.
        /// </summary>
        /// <param name="mesh">Input mesh</param>
        /// <param name="meshData">Previously acquired readable mesh data</param>
        /// <param name="worldScale">Local-to-world scale this mesh is present in the scene</param>
        /// <param name="precision">Desired minimum precision in world units</param>
        /// <param name="encodingSpeed">Encoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="decodingSpeed">Decoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="normalQuantization">Normal quantization</param>
        /// <param name="texCoordQuantization">Texture coordinate quantization</param>
        /// <param name="colorQuantization">Color quantization</param>
        /// <param name="genericQuantization">Generic quantization (e.g. blend weights and indices). unused at the moment</param>
        /// <returns>Encoded data (one per submesh)</returns>
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh mesh,
            Mesh.MeshData meshData,
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
            return await EncodeMesh(
                mesh,
                meshData,
                encodingSpeed,
                decodingSpeed,
                GetIdealQuantization(worldScale, precision, mesh.bounds),
                normalQuantization,
                texCoordQuantization,
                colorQuantization,
                genericQuantization
                );
        }

        /// <summary>
        /// Applies Draco compression to a given mesh and returns the encoded result (one per submesh)
        /// The quantization parameters help to find a balance between encoded size and quality / precision.
        /// </summary>
        /// <param name="unityMesh">Input mesh</param>
        /// <param name="encodingSpeed">Encoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="decodingSpeed">Decoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="positionQuantization">Vertex position quantization</param>
        /// <param name="normalQuantization">Normal quantization</param>
        /// <param name="texCoordQuantization">Texture coordinate quantization</param>
        /// <param name="colorQuantization">Color quantization</param>
        /// <param name="genericQuantization">Generic quantization (e.g. blend weights and indices). unused at the moment</param>
        /// <returns>Encoded data (one per submesh)</returns>
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh unityMesh,
            int encodingSpeed = 0,
            int decodingSpeed = 4,
            int positionQuantization = 14,
            int normalQuantization = 10,
            int texCoordQuantization = 12,
            int colorQuantization = 8,
            int genericQuantization = 12
        ) {
            var dataArray = Mesh.AcquireReadOnlyMeshData(unityMesh);
            var data = dataArray[0];

            var result = await EncodeMesh(
                unityMesh,
                data,
                encodingSpeed,
                decodingSpeed,
                positionQuantization,
                normalQuantization,
                texCoordQuantization,
                colorQuantization,
                genericQuantization
            );
            
            dataArray.Dispose();
            return result;
        }

        /// <summary>
        /// Applies Draco compression to a given mesh/meshData and returns the encoded result (one per submesh)
        /// The user is responsible for
        /// <see cref="UnityEngine.Mesh.AcquireReadOnlyMeshData(List&lt;Mesh&gt;)">acquiring the readable MeshData</see>
        /// and disposing it.
        /// The quantization parameters help to find a balance between encoded size and quality / precision.
        /// </summary>
        /// <param name="mesh">Input mesh</param>
        /// <param name="meshData">Previously acquired readable mesh data</param>
        /// <param name="encodingSpeed">Encoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="decodingSpeed">Decoding speed level. 0 means slow and small. 10 is fastest.</param>
        /// <param name="positionQuantization">Vertex position quantization</param>
        /// <param name="normalQuantization">Normal quantization</param>
        /// <param name="texCoordQuantization">Texture coordinate quantization</param>
        /// <param name="colorQuantization">Color quantization</param>
        /// <param name="genericQuantization">Generic quantization (e.g. blend weights and indices). unused at the moment</param>
        /// <returns>Encoded data (one per submesh)</returns>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh mesh,
            Mesh.MeshData meshData,
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
#if !UNITY_EDITOR
            if (!mesh.isReadable) {
                Debug.LogError("Mesh is not readable");
                return null;
            }
#endif
            Profiler.BeginSample("EncodeMesh.Prepare");
            
            var result = new EncodeResult[meshData.subMeshCount];
            var vertexAttributes = mesh.GetVertexAttributes();

            var strides = new int[DracoNative.maxStreamCount];
            var attributeDataDict = new Dictionary<VertexAttribute, AttributeData>();
            
            foreach (var attribute in vertexAttributes) {
                var attributeData = new AttributeData { offset = strides[attribute.stream], stream = attribute.stream };
                var size = attribute.dimension * GetAttributeSize(attribute.format);
                strides[attribute.stream] += size;
                attributeDataDict[attribute.attribute] = attributeData;
            }

            var streamCount = 1;
            for (var stream = 0; stream < strides.Length; stream++) {
                var stride = strides[stream];
                if(stride<=0) continue;
                streamCount = stream + 1;
            }

            var vData = new NativeArray<byte>[streamCount];
            for (var stream = 0; stream < streamCount; stream++) {
                vData[stream] = meshData.GetVertexData<byte>(stream);
            }

            var vDataPtr = GetReadOnlyPointers(streamCount, vData);
            Profiler.EndSample(); // EncodeMesh.Prepare
            
            for (var submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++) {

                Profiler.BeginSample("EncodeMesh.Submesh.Prepare");
                var submesh = mesh.GetSubMesh(submeshIndex);
                
                if (submesh.topology != MeshTopology.Triangles && submesh.topology != MeshTopology.Points) {
                    Debug.LogError($"Mesh topology {submesh.topology} is not supported");
                    return null;
                }
                
                var dracoEncoder = submesh.topology == MeshTopology.Triangles 
                    ? dracoEncoderCreate(mesh.vertexCount)
                    : dracoEncoderCreatePointCloud(mesh.vertexCount);

                var attributeIds = new Dictionary<VertexAttribute,(uint identifier,int dimensions)>();

                foreach (var attributeTuple in attributeDataDict)
                {
                    var attribute = attributeTuple.Key;
                    var attrData = attributeTuple.Value;
                    var format = mesh.GetVertexAttributeFormat(attribute);
                    var dimension = mesh.GetVertexAttributeDimension(attribute);
                    var stride = strides[attrData.stream];
                    var baseAddr = vDataPtr[attrData.stream] + attrData.offset;
                    var id = dracoEncoderSetAttribute(
                        dracoEncoder,
                        (int) GetAttributeType(attribute),
                        GetDataType(format),
                        dimension,
                        stride,
                        DracoNative.ConvertSpace(attribute),
                        baseAddr
                        );
                    attributeIds[attribute] = (id, dimension);
                }

                if (submesh.topology == MeshTopology.Triangles)
                {
                    var indices = mesh.GetIndices(submeshIndex);
                    var indicesData = PinArray(indices, out var gcHandle);
                    dracoEncoderSetIndices(
                        dracoEncoder,
                        DataType.UInt32,
                        (uint)indices.Length,
                        true,
                        indicesData
                        );
                    UnsafeUtility.ReleaseGCObject(gcHandle);
                }

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

                var encodeJob = new EncodeJob
                {
                    dracoEncoder = dracoEncoder
                };
                
                Profiler.EndSample(); //EncodeMesh.Submesh.Prepare

                var jobHandle = encodeJob.Schedule();
                while (!jobHandle.IsCompleted)
                {
                    await Task.Yield();
                }
                jobHandle.Complete();
                
                Profiler.BeginSample("EncodeMesh.Submesh.Aftermath");

                result[submeshIndex] = new EncodeResult (
                    dracoEncoder,
                    dracoEncoderGetEncodedIndexCount(dracoEncoder),
                    dracoEncoderGetEncodedVertexCount(dracoEncoder),
                    attributeIds
                );
                
                Profiler.EndSample(); // EncodeMesh.Submesh.Aftermath
            }
            
            Profiler.BeginSample("EncodeMesh.Aftermath");
            for (var stream = 0; stream < streamCount; stream++) {
                vData[stream].Dispose();
            }

            Profiler.EndSample();
            return result;
#else
            Debug.LogError("Draco Encoding only works on Unity 2020.1 or newer");
            return null;
#endif
        }

        static unsafe IntPtr PinArray(int[] indices, out ulong gcHandle)
        {
            return (IntPtr)UnsafeUtility.PinGCArrayAndGetDataAddress(indices, out gcHandle);
        }

        static unsafe IntPtr[] GetReadOnlyPointers(int count, NativeArray<byte>[] vData)
        {
            var result = new IntPtr[count];
            for (var stream = 0; stream < count; stream++) {
                result[stream] = (IntPtr) vData[stream].GetUnsafeReadOnlyPtr();
            }

            return result;
        }

        static DataType GetDataType(VertexAttributeFormat format) {
            switch (format) {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.Float16:
                    return DataType.Float32;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.UInt8:
                    return DataType.UInt8;
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.SInt8:
                    return DataType.Int8;
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.UNorm16:
                    return DataType.UInt16;
                case VertexAttributeFormat.SInt16:
                case VertexAttributeFormat.SNorm16:
                    return DataType.Int16;
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return DataType.Int32;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        static AttributeType GetAttributeType(VertexAttribute attribute) {
            switch (attribute) {
                case VertexAttribute.Position:
                    return AttributeType.Position;
                case VertexAttribute.Normal:
                    return AttributeType.Normal;
                case VertexAttribute.Color:
                    return AttributeType.Color;
                case VertexAttribute.TexCoord0:
                case VertexAttribute.TexCoord1:
                case VertexAttribute.TexCoord2:
                case VertexAttribute.TexCoord3:
                case VertexAttribute.TexCoord4:
                case VertexAttribute.TexCoord5:
                case VertexAttribute.TexCoord6:
                case VertexAttribute.TexCoord7:
                    return AttributeType.TextureCoordinate;
                case VertexAttribute.Tangent:
                case VertexAttribute.BlendWeight:
                case VertexAttribute.BlendIndices:
                    return AttributeType.Generic;
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
        
        [DllImport (k_DracoEncUnityLib)]
        static extern IntPtr dracoEncoderCreate(int vertexCount);
        
        [DllImport(k_DracoEncUnityLib)]
        static extern IntPtr dracoEncoderCreatePointCloud(int vertexCount);

        [DllImport (k_DracoEncUnityLib)]
        internal static extern void dracoEncoderRelease(IntPtr encoder);
        
        [DllImport (k_DracoEncUnityLib)]
        static extern void dracoEncoderSetCompressionSpeed(IntPtr encoder, int encodingSpeed, int decodingSpeed);
        
        [DllImport (k_DracoEncUnityLib)]
        static extern void dracoEncoderSetQuantizationBits(IntPtr encoder, int position, int normal, int uv, int color, int generic);
        
        [DllImport (k_DracoEncUnityLib)]
        internal static extern bool dracoEncoderEncode(IntPtr encoder, bool preserveTriangleOrder);
        
        [DllImport (k_DracoEncUnityLib)]
        static extern uint dracoEncoderGetEncodedVertexCount(IntPtr encoder);
        
        [DllImport (k_DracoEncUnityLib)]
        static extern uint dracoEncoderGetEncodedIndexCount(IntPtr encoder);
        
        [DllImport (k_DracoEncUnityLib)]
        internal static extern unsafe void dracoEncoderGetEncodeBuffer(IntPtr encoder, out void *data, out ulong size);

        
        [DllImport (k_DracoEncUnityLib)]
        static extern bool dracoEncoderSetIndices(
            IntPtr encoder,
            DataType indexComponentType,
            uint indexCount,
            bool flip,
            IntPtr indices
            );
        
        [DllImport (k_DracoEncUnityLib)]
        static extern uint dracoEncoderSetAttribute(
            IntPtr encoder,
            int attributeType,
            DataType dracoDataType,
            int componentCount,
            int stride,
            bool flip,
            IntPtr data);
    }

    struct EncodeJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public IntPtr dracoEncoder;
        
        public void Execute()
        {
            DracoEncoder.dracoEncoderEncode(dracoEncoder, false);
        }
    }
}
